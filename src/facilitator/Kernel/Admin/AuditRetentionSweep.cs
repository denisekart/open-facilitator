using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Admin;

/// <summary>
/// The audit retention law (H3/N15): the audit trail is pruned to the window
/// the operator sets, never silently unbounded. The policy is the
/// <c>admin_config</c> resource <c>audit-policy</c>
/// (<c>{"retentionDays": 365}</c>); no row means the standing default (730
/// days — two years of decisions). Pruning is a DELETE of aged rows only:
/// an export taken inside the window is complete, an aged row is honestly
/// gone (a pruned lookup answers "not found" and never fabricates).
/// </summary>
public static class AuditRetentionSweep
{
    /// <summary>The admin_config resource carrying the policy.</summary>
    public const string PolicyResource = "audit-policy";

    /// <summary>Days retained when no policy row exists.</summary>
    public const int DefaultRetentionDays = 730;

    /// <summary>One retention pass: delete audit rows older than the window,
    /// return the number pruned (0 when nothing aged out).</summary>
    public static async Task<int> PruneAsync(
        IDbContextFactory<FacilitatorDbContext> contextFactory, TimeProvider timeProvider, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var days = await RetentionDaysAsync(db, ct);
        var floor = timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
        return await db.Audit
            .Where(a => a.OccurredAt < floor)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>The configured window (a malformed or absent policy is the
    /// default — an operator typo must never mean "delete everything").</summary>
    public static async Task<int> RetentionDaysAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == PolicyResource)
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (payload is null)
        {
            return DefaultRetentionDays;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("retentionDays", out var days)
                && days.TryGetInt32(out var parsed) && parsed > 0
                ? parsed
                : DefaultRetentionDays;
        }
        catch (JsonException)
        {
            return DefaultRetentionDays;
        }
    }
}
