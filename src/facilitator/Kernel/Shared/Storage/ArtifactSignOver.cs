using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Storage;

/// <summary>Requested owner for a set of existing artifact rows.</summary>
/// <param name="ArtifactIds">Rows to move, in request order.</param>
/// <param name="ToTeamSlug">Destination team (guard-resolved or admin-entered).</param>
/// <param name="FromTeamSlug">The team that must own a row for it to move — a row
/// owned by anyone else is <c>Skipped("foreign-owner")</c>, never moved.</param>
/// <param name="Actor">Canonical actor for the timeline + audit rows.</param>
/// <param name="Rule">The rule that decided this move (flow rule id or <c>admin:…</c>).</param>
/// <param name="ProviderEventId">Optional: stamp <c>|owner:&lt;to&gt;</c> onto this
/// ledger row's <c>verified_reference</c> (the ledger never moves; the stamp rides).</param>
/// <param name="Thread">Optional: repoint this conversation's teamless binding to the new owner.</param>
public sealed record SignOverRequest(
    IReadOnlyList<Guid> ArtifactIds,
    string ToTeamSlug,
    string FromTeamSlug,
    string Actor,
    string Rule,
    string? ProviderEventId = null,
    ThreadRef? Thread = null);

/// <summary>(provider, channel, root) — the conversation whose binding gets repointed.</summary>
public sealed record ThreadRef(string Provider, string Channel, string RootMessageId);

/// <summary>What one sign-over did: the rows that moved, the rows that did not (with
/// machine reasons), and the audit correlation id joining every row it wrote.</summary>
public sealed record SignOverOutcome(
    IReadOnlyList<Guid> Transferred,
    IReadOnlyList<string> Skipped,
    Guid AuditId);

/// <summary>
/// The transfer primitive (inbound leg of router-team routing): an atomic, audited,
/// digest re-verified move of artifact ownership (<c>artifacts.team_slug</c> — a
/// column, not a location: no bytes move) to a guard-resolved team, with the
/// binding repoint and the ledger ownership stamp that keep routing consistent.
/// Transfer changes ownership; a grant only rents access
/// (<see cref="ArtifactGrantStore"/>). One implementation serves both the flow
/// step and the admin mutation — identical audit shape either way.
/// </summary>
public sealed class ArtifactSignOver(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IArtifactStore store,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Move ownership of every movable row; unmovable rows are skipped with
    /// a reason and never touched. Digest re-verify runs inside the transaction
    /// before any UPDATE; a mismatching row denies its own transfer, the rest move.</summary>
    public async Task<SignOverOutcome> SignOverAsync(SignOverRequest r, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var auditId = Guid.NewGuid();
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var transferred = new List<Guid>();
        var skipped = new List<string>();
        foreach (var id in r.ArtifactIds)
        {
            // FOR UPDATE under the transfer tx: single-owner rows, one mover at a time.
            var row = (await db.Artifacts
                .FromSql($"SELECT * FROM facilitator.artifacts WHERE artifact_id = {id} FOR UPDATE")
                .FirstOrDefaultAsync(ct)) is { } found
                ? found
                : null;
            if (row is null)
            {
                skipped.Add($"{id}:not-found");
                continue;
            }

            if (string.Equals(row.TeamSlug, r.ToTeamSlug, StringComparison.Ordinal))
            {
                skipped.Add($"{id}:already-owned");
                continue;
            }

            if (row.TeamSlug is null || !string.Equals(row.TeamSlug, r.FromTeamSlug, StringComparison.Ordinal))
            {
                skipped.Add($"{id}:foreign-owner");
                continue;
            }

            // Digest re-verify BEFORE the move: ownership of bytes nobody can
            // re-prove is not worth transferring (deny audit, row untouched).
            if (!await DigestVerifiesAsync(row, ct))
            {
                skipped.Add($"{id}:digest-mismatch");
                audit.Append(db, Stamp(r, auditId, "deny", "digest-mismatch",
                    transferred, skipped, id));
                continue;
            }

            row.TeamSlug = r.ToTeamSlug;
            transferred.Add(id);

            if (row.WorkId is { } workId)
            {
                await AppendTimelineAsync(db, r, row, workId, now, ct);
            }
        }

        if (r.ProviderEventId is { Length: > 0 } eventId)
        {
            // The ledger rows never move — the ownership stamp rides the
            // existing judgment. The suffix is inert for every reader
            // (JudgmentWorkId only inspects a "work/" prefix at position 0;
            // duplicate detection compares bodies). A flow template resolves
            // the provider event by the delivery identity the envelope carries,
            // so both the PK and the (provider, delivery-id) spine resolve.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE facilitator.provider_events
                    SET verified_reference = COALESCE(verified_reference, '') || '|owner:' || {r.ToTeamSlug}
                  WHERE provider_event_id::text = {eventId}
                     OR provider_delivery_id = {eventId}
                 """, ct);
        }

        if (r.Thread is { } thread)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 UPDATE facilitator.communication_bindings
                    SET team = {r.ToTeamSlug}
                  WHERE provider = {thread.Provider} AND channel = {thread.Channel}
                    AND root_message_id = {thread.RootMessageId} AND work_id IS NULL
                 """, ct);
        }

        var decision = transferred.Count > 0 ? "allow" : "deny";
        var reason = transferred.Count > 0 ? "signed-over" : "nothing-transferred";
        var record = Stamp(r, auditId, decision, reason, transferred, skipped, ArtifactId: null);
        audit.Append(db, record);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new SignOverOutcome(transferred, skipped, auditId);
    }

    /// <summary>The blob must re-hash to the row's declared digest; external or
    /// foreign-scheme rows (the store cannot read them) do not re-verify.</summary>
    private async Task<bool> DigestVerifiesAsync(ArtifactEntity row, CancellationToken ct)
    {
        if (!row.Digest.StartsWith("sha256:", StringComparison.Ordinal) || !store.Owns(row.StorageUri))
        {
            return false;
        }

        await using var stream = await store.GetAsync(ArtifactKeys.StoreKeyOf(row.StorageUri), ct);
        if (stream is null)
        {
            return false;
        }

        var hash = await SHA256.HashDataAsync(stream, ct);
        return string.Equals(Convert.ToHexString(hash).ToLowerInvariant(), row.Digest["sha256:".Length..], StringComparison.Ordinal);
    }

    /// <summary>The <c>artifact.signed-over</c> timeline row — the exact
    /// <c>ArtifactLedger.AppendTimelineAsync</c> shape plus the ownership delta.</summary>
    private static async Task AppendTimelineAsync(
        FacilitatorDbContext db, SignOverRequest r, ArtifactEntity row, Guid workId, DateTime now, CancellationToken ct)
    {
        var version = await db.Works.AsNoTracking().Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return;
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version.Value,
            Kind = "artifact.signed-over",
            Actor = r.Actor,
            Detail = Serialize(new Dictionary<string, string?>
            {
                ["artifactId"] = row.ArtifactId.ToString(),
                ["digest"] = row.Digest,
                ["mediaType"] = row.MediaType,
                ["fromTeam"] = r.FromTeamSlug,
                ["toTeam"] = r.ToTeamSlug,
                ["rule"] = r.Rule,
            }),
            CorrelationId = row.ArtifactId.ToString(),
            CreatedAt = now,
        });
    }

    private AuditRecord Stamp(
        SignOverRequest r, Guid auditId, string decision, string reason,
        List<Guid> transferred, List<string> skipped, Guid? ArtifactId) => new(
            Actor: r.Actor,
            Action: "artifact.sign-over",
            Resource: ArtifactId is { } one ? $"artifact/{one}" : $"artifact/{string.Join(',', transferred)}",
            Decision: decision,
            Reason: reason,
            EvidenceRef: null,
            PolicyVersion: "artifact-v1",
            Detail: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["fromTeam"] = r.FromTeamSlug,
                ["toTeam"] = r.ToTeamSlug,
                ["actor"] = r.Actor,
                ["rule"] = r.Rule,
                ["transferred"] = transferred,
                ["skipped"] = skipped,
            }, Json),
            CorrelationId: auditId.ToString());

    private static string Serialize(Dictionary<string, string?> detail) =>
        JsonSerializer.Serialize(detail, Json);
}
