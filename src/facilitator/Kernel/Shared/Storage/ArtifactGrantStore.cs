using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// Delivery-scoped read grants (the outbound leg of router-team routing): a
/// rental of ONE foreign-team artifact's bytes for ONE reply. Minted kernel-side
/// when the reply command is appended, the read itself consumes the row (rent
/// exactly once), the adapter's receipt revokes a never-read grant, expiry is
/// enforced lazily at read time — no background sweeper. Transfer changes
/// ownership (<see cref="ArtifactSignOver"/>); a grant only rents access, so
/// the platform bot never owns a team's bytes at rest.
/// </summary>
public sealed class ArtifactGrantStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>A code law, like <see cref="ArtifactTicket.AllocationTtl"/>: a
    /// grant outliving a reply attempt is a lease, not a rental.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Stage one <c>active</c> grant on the caller's context — the mint
    /// commits atomically with the reply append that needed it. The unique
    /// (artifact, command) index absorbs a replay of the same command.</summary>
    public void Mint(
        FacilitatorDbContext db, Guid artifactId, string granteeTeamSlug, string granteeEndpointId,
        string commandId, string mintedBy, string? reason, DateTime now)
    {
        db.ArtifactGrants.Add(new ArtifactGrantEntity
        {
            GrantId = Guid.NewGuid(),
            ArtifactId = artifactId,
            GranteeTeamSlug = granteeTeamSlug,
            GranteeEndpointId = granteeEndpointId,
            CommandId = commandId,
            Status = "active",
            MintedBy = mintedBy,
            Reason = reason,
            CreatedAt = now,
            ExpiresAt = now + DefaultTtl,
        });
    }

    /// <summary>The content route's gate: true ⇒ the bytes may be served, exactly
    /// once. A single-writer law: a duplicate read loses the versioned UPDATE and
    /// gets false (404). An active-but-past-expiry row stamps <c>expired</c> and
    /// answers false — expiry is a read-time fact, never a sweeper.</summary>
    public async Task<bool> TryConsumeOnReadAsync(Guid artifactId, string endpointId, string teamSlug, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);

        // Lazy expiry: an active row past its ceiling dies here, visibly.
        await db.ArtifactGrants
            .Where(g => g.ArtifactId == artifactId && g.GranteeEndpointId == endpointId
                     && g.Status == "active" && g.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.Status, "expired")
                .SetProperty(g => g.ConsumedAt, now)
                .SetProperty(g => g.ConsumeReason, "expiry"), ct);

        // Consume with a status fence (versioned-free single-writer: the loser
        // of a racing read sees status != 'active' and gets false).
        var consumed = await db.ArtifactGrants
            .Where(g => g.ArtifactId == artifactId && g.GranteeEndpointId == endpointId
                     && g.Status == "active" && g.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.Status, "consumed")
                .SetProperty(g => g.ConsumedAt, now)
                .SetProperty(g => g.ConsumeReason, "read"), ct);
        if (consumed == 0)
        {
            return false;
        }

        var row = await db.ArtifactGrants.AsNoTracking()
            .FirstAsync(g => g.ArtifactId == artifactId && g.GranteeEndpointId == endpointId
                          && g.ConsumedAt == now && g.ConsumeReason == "read", ct);

        // One read = two audit lines (the read, then its consumption), atomic
        // with the consume on this context.
        audit.Append(db, Line("artifact.grant.read", row, teamSlug, "read"));
        audit.Append(db, Line("artifact.grant.consume", row, teamSlug, "read"));
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>The receipt leg: any <c>active</c> grants minted for this delivery
    /// are revoked (rental unused). Zero rows ⇒ no audit — a receipt replay
    /// answers with the stored receipt (the <c>LinkLedger.AckAsync</c> posture).
    /// Returns the number of grants revoked.</summary>
    public async Task<int> RevokeByCommandAsync(string commandId, string endpointId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.ArtifactGrants
            .Where(g => g.CommandId == commandId && g.GranteeEndpointId == endpointId && g.Status == "active")
            .ToListAsync(ct);
        if (rows.Count == 0)
        {
            return 0;
        }

        foreach (var row in rows)
        {
            row.Status = "revoked";
            row.ConsumedAt = now;
            row.ConsumeReason = "receipt";
            audit.Append(db, Line("artifact.grant.consume", row, row.GranteeTeamSlug, "receipt"));
        }

        await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    private static AuditRecord Line(string action, ArtifactGrantEntity row, string teamSlug, string reason) => new(
        Actor: $"link:{row.GranteeEndpointId}",
        Action: action,
        Resource: $"artifact/{row.ArtifactId}",
        Decision: "allow",
        Reason: reason,
        EvidenceRef: null,
        PolicyVersion: "artifact-v1",
        Detail: JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["grantId"] = row.GrantId.ToString(),
            ["commandId"] = row.CommandId,
            ["endpointId"] = row.GranteeEndpointId,
            ["artifactId"] = row.ArtifactId.ToString(),
            ["team"] = teamSlug,
        }, Json),
        CorrelationId: row.GrantId.ToString());
}
