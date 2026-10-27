using System.Text.Json;
using System.Text.RegularExpressions;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// The retention-GC pass (spec appendix 8.2, plan 14 D7/D8): a serialized
/// background loop (one <c>pg_try_advisory_xact_lock</c> per pass — no
/// distributed scheduling) collecting rows whose retention lapsed and whose
/// Work (if any) is terminal, plus expired pending slots. Per row, in one
/// short transaction: <c>FOR UPDATE</c> re-check, reference check, guarded
/// update to <c>deleted</c> + audit (+ timeline when the row hangs on a Work).
/// The backend delete runs AFTER commit and is idempotent; a crash between
/// commit and delete leaves the tombstone as the in-tx fact and the blob
/// orphaned — an accepted trade (documented at the delete site). Semantics:
/// <c>permanent</c> never expires; <c>archive</c> never expires kernel-side
/// (tiering is the backend's job); <c>Nd</c> deletes when
/// <c>stored_at + N days</c> is past and no live Work references the blob;
/// expired pending slots never had bytes, so the sweep skips the backend.
/// </summary>
public sealed class RetentionGcService(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IArtifactStore store,
    AuditWriter audit,
    KernelMetrics metrics,
    TimeProvider timeProvider,
    ArtifactOptions options,
    ILogger<RetentionGcService> logger) : BackgroundService
{
    /// <summary>Advisory key serializing GC passes (migration lock is 420001).</summary>
    internal const int GcLockKey = 420002;

    /// <summary>Candidates re-evaluated per pass (lock-order determinism, bounded tx).</summary>
    internal const int PassBudget = 100;

    private static readonly Regex DaysPolicy = new("^[1-9][0-9]{0,3}d$", RegexOptions.Compiled);

    private static readonly string[] TerminalWires =
        Enum.GetValues<WorkState>().Where(StateGuard.IsTerminal).Select(w => w.Wire()).ToArray();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateTime _lastPass = timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Gc.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Log-and-continue: a failed pass retries on the next tick
                // (dispatcher precedent — GC must never take the host down).
                logger.LogWarning(ex, "retention GC pass failed; retrying next tick");
            }
        }
    }

    /// <summary>One GC pass, public for tests. Silently no-ops when another instance holds the pass lock.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;

            // Try-Lock: a busy pass means another kernel instance is already
            // collecting — skipping is correct, not an error.
            if (!await TryExecuteScalarBoolAsync(db, $"SELECT pg_try_advisory_xact_lock({GcLockKey})", ct))
            {
                return;
            }

            var candidates = await CandidateIdsAsync(db, now, ct);
            var collected = 0;
            var referenceHeld = 0;
            var backendDeletes = new List<string>();
            foreach (var candidate in candidates)
            {
                var outcome = await CollectRowAsync(db, candidate, now, ct);
                switch (outcome.Kind)
                {
                    case CollectKind.Collected:
                        collected++;
                        if (outcome.BlobKey is not null)
                        {
                            backendDeletes.Add(outcome.BlobKey);
                        }

                        metrics.ArtifactGcCollected(outcome.Reason);
                        break;
                    case CollectKind.ReferenceHeld:
                        referenceHeld++;
                        break;
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Backend deletes post-commit: the tombstone is the in-tx fact.
            // An orphaned blob after a crash here is accepted — the delete is
            // idempotent and the eligibility query can be rerun by hand
            // (next-resort tool; no scheduler state to reconcile).
            foreach (var key in backendDeletes)
            {
                try
                {
                    await store.DeleteAsync(key, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    metrics.ArtifactStoreErrors("gc");
                    logger.LogWarning(ex, "retention GC backend delete failed for {Key}", key);
                }
            }

            _lastPass = timeProvider.GetUtcNow().UtcDateTime;
            if (collected > 0 || referenceHeld > 0)
            {
                logger.LogDebug("retention GC: {Collected} collected, {Held} reference-held", collected, referenceHeld);
            }
        }
        finally
        {
            metrics.SetGcLastPassAge(
                (long)(timeProvider.GetUtcNow().UtcDateTime - _lastPass).TotalSeconds);
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> TryExecuteScalarBoolAsync(FacilitatorDbContext db, string sql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task<List<Guid>> CandidateIdsAsync(FacilitatorDbContext db, DateTime now, CancellationToken ct)
    {
        // Eligibility lives in SQL: Nd-lapsed stored rows UNION expired
        // pending slots. archive/permanent never age out, so they never match.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $$"""
            SELECT artifact_id FROM facilitator.artifacts
            WHERE (state = 'stored'
                   AND retention_policy ~ '^[1-9][0-9]{0,3}d$'
                   AND stored_at + (substring(retention_policy from '^[0-9]+')::int * INTERVAL '1 day') < @now)
               OR (state = 'pending' AND expires_at < @now)
            ORDER BY artifact_id
            LIMIT {{PassBudget}}
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "now";
        parameter.Value = now;
        command.Parameters.Add(parameter);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private enum CollectKind
    {
        None,
        Collected,
        ReferenceHeld,
    }

    private sealed record CollectOutcome(CollectKind Kind, string Reason, string? BlobKey);

    private async Task<CollectOutcome> CollectRowAsync(FacilitatorDbContext db, Guid artifactId, DateTime now, CancellationToken ct)
    {
        // Per-row FOR UPDATE re-check: the candidate list is a snapshot; the
        // row's live state is what the tombstone must reflect (the advisory
        // lock serializes passes, so lock-order determinism is free).
        var row = await db.Artifacts
            .FromSql($"SELECT * FROM facilitator.artifacts WHERE artifact_id = {artifactId} FOR UPDATE")
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
        if (row is null || row.State is not ("stored" or "pending"))
        {
            return new CollectOutcome(CollectKind.None, "raced", null);
        }

        if (row.State == "pending")
        {
            // Expired allocation slot: bytes never landed, nothing to delete.
            var slot = await TombstoneAsync(db, row, "expired-slot", now, ct);
            return slot
                ? new CollectOutcome(CollectKind.Collected, "expired-slot", null)
                : new CollectOutcome(CollectKind.None, "raced", null);
        }

        if (!DaysPolicy.IsMatch(row.RetentionPolicy))
        {
            return new CollectOutcome(CollectKind.None, "ineligible", null);
        }

        var ageDays = int.Parse(row.RetentionPolicy[..^1]);
        if (row.StoredAt is null || row.StoredAt.Value.AddDays(ageDays) >= now)
        {
            return new CollectOutcome(CollectKind.None, "ineligible", null);
        }

        // Reference check: a live (non-terminal) Work pins the blob.
        if (row.WorkId is not null)
        {
            var live = await db.Works.AsNoTracking()
                .Where(w => w.WorkId == row.WorkId && !TerminalWires.Contains(w.State))
                .AnyAsync(ct);
            if (live)
            {
                return new CollectOutcome(CollectKind.ReferenceHeld, "reference-held", null);
            }
        }

        // Plan 15: a recorded (non-superseded) result manifest pins the blob
        // it was stored under AND every output artifact its document declared,
        // regardless of the Work's state — the manifest is the durable claim.
        var idText = artifactId.ToString();
        var manifestPinned = await db.ResultManifests.AsNoTracking()
            .Where(m => m.State == "recorded"
                        && (m.ArtifactId == artifactId || m.OutputArtifactIds.Contains(idText)))
            .AnyAsync(ct);
        if (manifestPinned)
        {
            return new CollectOutcome(CollectKind.ReferenceHeld, "reference-held", null);
        }

        var key = StoreKeyOf(row.StorageUri);
        var tombstoned = await TombstoneAsync(db, row, "retention", now, ct);
        return tombstoned
            ? new CollectOutcome(CollectKind.Collected, "retention", key)
            : new CollectOutcome(CollectKind.None, "raced", null);
    }

    private async Task<bool> TombstoneAsync(FacilitatorDbContext db, ArtifactEntity row, string reason, DateTime now, CancellationToken ct)
    {
        var state = row.State;
        var policy = row.RetentionPolicy;
        var rows = await db.Artifacts
            .Where(a => a.ArtifactId == row.ArtifactId && a.State == state && a.RetentionPolicy == policy)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "deleted"), ct);
        if (rows == 0)
        {
            return false;
        }

        audit.Append(db, new AuditRecord(
            Actor: "kernel",
            Action: "artifact.gc",
            Resource: $"artifact/{row.ArtifactId}",
            Decision: "allow",
            Reason: reason,
            EvidenceRef: null,
            PolicyVersion: "artifact-v1",
            Detail: JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["policy"] = policy,
                ["reason"] = reason,
                ["referenced"] = "false",
            }, Json)));
        await AppendTimelineAsync(db, row, reason, now, ct);
        return true;
    }

    private async Task AppendTimelineAsync(FacilitatorDbContext db, ArtifactEntity row, string reason, DateTime now, CancellationToken ct)
    {
        if (row.WorkId is not { } workId)
        {
            return;
        }

        var version = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return;
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version.Value,
            Kind = "artifact.gc",
            Actor = "kernel",
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["artifactId"] = row.ArtifactId.ToString(),
                ["reason"] = reason,
            }, Json),
            CorrelationId = row.ArtifactId.ToString(),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary><c>{scheme}:{key}</c> → <c>key</c> when the scheme is the live backend's; foreign schemes get no delete.</summary>
    private string? StoreKeyOf(string storageUri)
    {
        var colon = storageUri.IndexOf(':');
        return colon > 0 && storageUri[..colon] == store.StorageUriScheme
            ? storageUri[(colon + 1)..]
            : null;
    }
}
