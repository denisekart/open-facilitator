using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Admin;

/// <summary>Manifest re-mark outcome.</summary>
public enum ReMarkOutcome
{
    /// <summary>The <c>result_state</c> column flipped, audit appended.</summary>
    Stored,

    /// <summary>No such manifest row (404).</summary>
    NotFound,
}

/// <summary>
/// The admin mutation seam behind <c>/api/admin/*</c> POSTs (Plan 20):
/// manifest re-mark touches <c>result_state</c> ONLY (Plan 15 immutability
/// law: an UPDATE may touch nothing but <c>state</c> + <c>result_state</c>,
/// and the chain law owns <c>state</c>), with a same-tx audit row. Dead-letter
/// replay lives in <see cref="OutboxRepository"/> beside <c>AppendAsync</c>
/// (the NOTIFY + commandId-uniqueness law).
/// </summary>
public sealed class AdminMutationStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit)
{
    /// <summary>Human attestation re-mark: any→any of the three writable result states.</summary>
    public async Task<ReMarkOutcome> ReMarkManifestAsync(string manifestId, string resultState, string? actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.ResultManifests.FirstOrDefaultAsync(m => m.ManifestId == manifestId, ct);
        if (row is null)
        {
            return ReMarkOutcome.NotFound;
        }

        row.ResultState = resultState;
        audit.Append(db, new AuditRecord(actor, "admin.manifest.re-mark", $"/api/admin/manifests/{manifestId}/re-mark", "allow", null, null, null,
            System.Text.Json.JsonSerializer.Serialize(new { manifestId, resultState })));
        await db.SaveChangesAsync(ct);
        return ReMarkOutcome.Stored;
    }
}
