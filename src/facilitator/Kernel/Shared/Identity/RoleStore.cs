using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>Role-rule mutation outcome for the management API.</summary>
public enum RoleUpsertResult
{
    /// <summary>Stored (inserted or updated) and the local snapshot refreshed.</summary>
    Stored,

    /// <summary>rowVersion mismatch (or absence) against the stored row (409).</summary>
    Conflict,
}

/// <summary>
/// The runtime-mutable role rule store. Config <c>identity:roles</c> seeds
/// the table at first access <b>only when empty</b> (config bootstraps, DB is
/// truth thereafter); effective rules serve from an in-memory snapshot
/// refreshed at most every <c>identity:rolesRefreshSeconds</c> (the
/// cross-replica staleness bound) or immediately by the local writer. Local
/// writers see their own edit instantly.
/// </summary>
public sealed class RoleStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    IOptions<IdentityOptions> options,
    TimeProvider timeProvider,
    ILogger<RoleStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot _snapshot = new([], string.Empty, DateTime.MinValue);

    /// <summary>Effective rule sets (refreshed per TTL; seeds config on first access).</summary>
    public async Task<IReadOnlyList<RoleRuleSet>> CurrentRulesAsync(CancellationToken ct)
    {
        if (IsStale(_snapshot))
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (IsStale(_snapshot))
                {
                    _snapshot = await LoadAsync(ct);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        return _snapshot.Rules;
    }

    /// <summary>Policy version string served into audit rows (row-version epoch).</summary>
    public string PolicyVersion => _snapshot.PolicyVersion;

    /// <summary>Insert or version-checked update; refreshes the local snapshot and audits the edit.</summary>
    /// <param name="roleName">Role name.</param>
    /// <param name="rules">New OR-list of AND-rules.</param>
    /// <param name="expectedRowVersion">Stored row version the writer saw; null forces the write.</param>
    /// <param name="actor">Canonical actor for the audit row.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<RoleUpsertResult> UpsertAsync(string roleName, IReadOnlyList<RoleRule> rules, long? expectedRowVersion, string? actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var existing = await db.IdentityRoles.FirstOrDefaultAsync(r => r.RoleName == roleName, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (existing is null)
        {
            if (expectedRowVersion > 0)
            {
                return RoleUpsertResult.Conflict;
            }

            db.IdentityRoles.Add(new IdentityRoleEntity
            {
                RoleName = roleName,
                Rules = RoleRuleJson.Serialize(rules),
                RowVersion = 1,
                UpdatedAt = now,
            });
        }
        else if (expectedRowVersion is not null && existing.RowVersion != expectedRowVersion)
        {
            return RoleUpsertResult.Conflict;
        }
        else
        {
            existing.Rules = RoleRuleJson.Serialize(rules);
            existing.RowVersion += 1;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await RefreshNowAsync(ct);
        await audit.WriteAsync(RoleEditAudit("upsert", roleName, rules.Count, actor), ct);
        return RoleUpsertResult.Stored;
    }

    /// <summary>Remove a role (409 semantics on absence or rowVersion mismatch).</summary>
    public async Task<RoleUpsertResult> RemoveAsync(string roleName, long? expectedRowVersion, string? actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var existing = await db.IdentityRoles.FirstOrDefaultAsync(r => r.RoleName == roleName, ct);
        if (existing is null || (expectedRowVersion is not null && existing.RowVersion != expectedRowVersion))
        {
            return RoleUpsertResult.Conflict;
        }

        db.IdentityRoles.Remove(existing);
        await db.SaveChangesAsync(ct);
        await RefreshNowAsync(ct);
        await audit.WriteAsync(RoleEditAudit("remove", roleName, 0, actor), ct);
        return RoleUpsertResult.Stored;
    }

    /// <summary>All stored rows for the management API (name, rules, rowVersion).</summary>
    public async Task<List<(string RoleName, IReadOnlyList<RoleRule> Rules, long RowVersion)>> ListAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.IdentityRoles.AsNoTracking().OrderBy(r => r.RoleName).ToListAsync(ct);
        return rows.Select(r => (r.RoleName, RoleRuleJson.ParseRules(r.Rules) ?? [], r.RowVersion)).ToList();
    }

    private static AuditRecord RoleEditAudit(string verb, string roleName, int ruleCount, string? actor) =>
        new(actor, $"identity.role.{verb}", $"/api/identity/roles/{roleName}", "allow", null, null, null,
            JsonSerializer.Serialize(new { role = roleName, ruleCount }));

    private bool IsStale(Snapshot snapshot) =>
        snapshot.PolicyVersion.Length == 0 || timeProvider.GetUtcNow().UtcDateTime >= snapshot.NextRefresh;

    private async Task RefreshNowAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _snapshot = await LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.IdentityRoles.ToListAsync(ct);
        if (rows.Count == 0)
        {
            await SeedFromConfigAsync(db, ct);
            rows = await db.IdentityRoles.ToListAsync(ct);
        }

        var sets = new List<RoleRuleSet>(rows.Count);
        long epoch = 0;
        foreach (var row in rows)
        {
            epoch = Math.Max(epoch, row.RowVersion);
            var rules = RoleRuleJson.ParseRules(row.Rules);
            if (rules is null)
            {
                logger.LogWarning("identity_roles {Role}: malformed rules jsonb — rule set skipped (deny, never wildcard)", row.RoleName);
                continue;
            }

            sets.Add(new RoleRuleSet(row.RoleName, rules));
        }

        var ttl = Math.Max(1, options.Value.RolesRefreshSeconds);
        return new Snapshot(sets, $"rv{epoch}", timeProvider.GetUtcNow().UtcDateTime.AddSeconds(ttl));
    }

    private async Task SeedFromConfigAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var configRules = RoleRuleJson.FromConfig(options.Value);
        if (configRules.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var set in configRules)
        {
            db.IdentityRoles.Add(new IdentityRoleEntity
            {
                RoleName = set.Name,
                Rules = RoleRuleJson.Serialize(set.Rules),
                RowVersion = 1,
                UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("identity_roles seeded from identity:roles config ({Count} roles) — DB is truth thereafter", configRules.Count);
    }

    private sealed record Snapshot(IReadOnlyList<RoleRuleSet> Rules, string PolicyVersion, DateTime NextRefresh);
}
