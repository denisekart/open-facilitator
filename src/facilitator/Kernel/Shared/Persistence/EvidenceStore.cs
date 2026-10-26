using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>A stored kernel: evidence row (validated at admission; minted/consumed by Plan 11).</summary>
/// <param name="EvidenceId">The id after <c>kernel:</c>.</param>
/// <param name="Subject">Canonical subject minted for.</param>
/// <param name="Claims">jsonb claim snapshot.</param>
/// <param name="ExpiresAt">Null = never expires.</param>
/// <param name="ConsumedAt">Non-null = already consumed (replay ⇒ invalid).</param>
public sealed record EvidenceRecord(string EvidenceId, string Subject, string Claims, DateTime? ExpiresAt, DateTime? ConsumedAt);

/// <summary>Read side of <c>facilitator.evidence</c> for the admission-time validator.</summary>
public interface IEvidenceStore
{
    /// <summary>Fetch one evidence row by id; null when the id was never minted.</summary>
    Task<EvidenceRecord?> FindAsync(string id, CancellationToken ct);
}

/// <summary>Store-backed <see cref="IEvidenceStore"/> (single lookup, no caching: evidence rows are consumed once).</summary>
public sealed class EvidenceStore(IDbContextFactory<FacilitatorDbContext> contextFactory) : IEvidenceStore
{
    /// <inheritdoc />
    public async Task<EvidenceRecord?> FindAsync(string id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Evidence.AsNoTracking()
            .Where(e => e.EvidenceId == id)
            .Select(e => new EvidenceRecord(e.EvidenceId, e.Subject, e.Claims, e.ExpiresAt, e.ConsumedAt))
            .FirstOrDefaultAsync(ct);
    }
}
