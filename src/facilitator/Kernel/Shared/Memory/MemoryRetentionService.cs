using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Facilitator.Kernel.Shared.Memory;

/// <summary>
/// The memory retention pass (spec §14.6): the second hosted loop
/// (advisory lock <c>420003</c> — 420001 is migrations, 420002 artifact GC),
/// independent of the artifact loop by design. Candidates whose
/// <c>expires_at</c> lapsed are tombstoned under the D5 law — content drop
/// (which recomputes the tsvector, so the row leaves every search), share
/// revocation, usage rows retained as audit — each row in one short
/// transaction with <c>memory.gc</c> audited in-tx.
/// </summary>
public sealed class MemoryRetentionService(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    TimeProvider timeProvider,
    MemoryOptions options,
    ILogger<MemoryRetentionService> logger) : BackgroundService
{
    /// <summary>Advisory key serializing memory retention passes.</summary>
    internal const int MemoryGcLockKey = 420003;

    /// <summary>Rows re-evaluated per pass (bounded tx, the artifact-GC law).</summary>
    internal const int PassBudget = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
                // Log-and-continue: a failed pass retries next tick — a memory
                // outage never takes the host down (§14.6).
                logger.LogWarning(ex, "memory retention pass failed; retrying next tick");
            }
        }
    }

    /// <summary>One retention pass, public for tests. Silently no-ops when another instance holds the pass lock.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (!await TryLockAsync(db, ct))
            {
                return;
            }

            var candidates = await CandidateIdsAsync(db, now, ct);
            var collected = 0;
            foreach (var entryId in candidates)
            {
                // D5 per row: tombstone + content drop, share revocation, audit.
                var dropped = await db.MemoryEntries
                    .Where(e => e.EntryId == entryId && e.Status != "tombstoned")
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Status, "tombstoned")
                        .SetProperty(e => e.Content, string.Empty), ct);
                if (dropped == 0)
                {
                    continue;
                }

                await db.MemoryShares
                    .Where(s => s.EntryId == entryId && s.RevokedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
                audit.Append(db, new AuditRecord(
                    Actor: "kernel:memory-retention",
                    Action: "memory.gc",
                    Resource: $"memory/{entryId}",
                    Decision: "allow",
                    Reason: "retention",
                    EvidenceRef: null,
                    PolicyVersion: "memory-v1",
                    Detail: JsonSerializer.Serialize(new Dictionary<string, string?>
                    {
                        ["reason"] = "retention",
                        ["expired"] = now.ToString("O"),
                    }, Json)));
                collected++;
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (collected > 0)
            {
                logger.LogDebug("memory retention: {Collected} entries tombstoned", collected);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> TryLockAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT pg_try_advisory_xact_lock({MemoryGcLockKey})";
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private static async Task<List<Guid>> CandidateIdsAsync(FacilitatorDbContext db, DateTime now, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $$"""
            SELECT entry_id FROM facilitator.memory_entries
            WHERE expires_at IS NOT NULL AND expires_at < @now AND status <> 'tombstoned'
            ORDER BY entry_id
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
}
