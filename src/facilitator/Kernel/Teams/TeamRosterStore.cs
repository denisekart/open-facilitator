using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Teams;

/// <summary>One roster entry as served by the API.</summary>
/// <param name="TeamSlug">Team the row belongs to.</param>
/// <param name="Actor">Canonical actor (<c>oidc:iss|sub</c> or <c>contributor:handle</c>).</param>
/// <param name="Kind">human | contributor.</param>
/// <param name="Capacity">member | operator.</param>
/// <param name="AddedBy">Canonical actor of the adder ("config" when seeded).</param>
/// <param name="CreatedAt">When the row landed (UTC).</param>
public sealed record TeamMemberView(string TeamSlug, string Actor, string Kind, string Capacity, string? AddedBy, DateTime CreatedAt);

/// <summary>Roster mutation outcome for the API.</summary>
public enum TeamRosterOutcome
{
    /// <summary>The write landed.</summary>
    Stored,

    /// <summary>No team with that slug — 404.</summary>
    TeamUnknown,

    /// <summary>Actor string outside the canonical forms — 400.</summary>
    InvalidActor,
}

/// <summary>Result of a roster mutation.</summary>
/// <param name="Outcome">Terminal outcome.</param>
/// <param name="Detail">Validation detail when invalid.</param>
public sealed record TeamRosterMutation(TeamRosterOutcome Outcome, string? Detail)
{
    /// <summary>True when the caller may serve 200.</summary>
    public bool Stored => Outcome == TeamRosterOutcome.Stored;
}

/// <summary>
/// The <c>facilitator.team_members</c> roster: who belongs to a team and in
/// what capacity. Config bootstraps rows at startup, the DB is truth after the
/// first write. An empty roster for a team means <b>unconfigured</b> (the
/// team-scope resolver allows); once a team has rows, the roster gates.
/// </summary>
public sealed class TeamRosterStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>Canonical actor forms the roster accepts.</summary>
    public static bool IsValidActor(string actor) =>
        actor.StartsWith("oidc:", StringComparison.Ordinal) && actor.Contains('|', StringComparison.Ordinal)
        || actor.StartsWith("contributor:", StringComparison.Ordinal) && actor.Length > "contributor:".Length;

    /// <summary>Roster rows of one team (slug-keyed; empty when unconfigured).</summary>
    public async Task<List<TeamMemberView>> ListAsync(string teamSlug, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.TeamMembers.AsNoTracking()
            .Where(m => m.Team!.Slug == teamSlug)
            .OrderBy(m => m.Actor)
            .Select(m => new TeamMemberView(m.Team!.Slug, m.Actor, m.Kind, m.Capacity, m.AddedBy, m.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>True when the team has at least one roster row (configured).</summary>
    public async Task<bool> IsConfiguredAsync(string teamSlug, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.TeamMembers.AnyAsync(m => m.Team!.Slug == teamSlug, ct);
    }

    /// <summary>True when the actor holds a roster row of the team (any capacity).</summary>
    public async Task<bool> IsMemberAsync(string teamSlug, string actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.TeamMembers.AnyAsync(m => m.Team!.Slug == teamSlug && m.Actor == actor, ct);
    }

    /// <summary>True when the actor is a team-operator of the team (roster capacity row).</summary>
    public async Task<bool> IsTeamOperatorAsync(string teamSlug, string actor, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.TeamMembers.AnyAsync(
            m => m.Team!.Slug == teamSlug && m.Actor == actor && m.Capacity == "operator", ct);
    }

    /// <summary>Actors of the team that hold a capacity (audience fan-out, team operators).</summary>
    public async Task<List<string>> OperatorsOfAsync(string teamSlug, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.TeamMembers.AsNoTracking()
            .Where(m => m.Team!.Slug == teamSlug && m.Capacity == "operator")
            .Select(m => m.Actor)
            .OrderBy(static a => a)
            .ToListAsync(ct);
    }

    /// <summary>Upsert a roster row (idempotent on (team, actor); capacity/kind refreshed).</summary>
    public async Task<TeamRosterMutation> PutAsync(
        string teamSlug, string actor, string kind, string capacity, string addedBy, CancellationToken ct)
    {
        if (!IsValidActor(actor))
        {
            return new TeamRosterMutation(TeamRosterOutcome.InvalidActor,
                "actor must be 'oidc:<iss>|<sub>' or 'contributor:<handle>'");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Slug == teamSlug, ct);
        if (team is null)
        {
            return new TeamRosterMutation(TeamRosterOutcome.TeamUnknown, null);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await db.TeamMembers.FirstOrDefaultAsync(
            m => m.TeamId == team.TeamId && m.Actor == actor, ct);
        audit.Append(db, new AuditRecord(
            addedBy,
            "teams.roster.write",
            $"teams/{teamSlug}/members/{actor}",
            "allow",
            null,
            null,
            null,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["capacity"] = capacity,
                ["update"] = existing is not null,
            })));
        if (existing is null)
        {
            db.TeamMembers.Add(new TeamMemberEntity
            {
                TeamId = team.TeamId,
                Actor = actor,
                Kind = kind,
                Capacity = capacity,
                AddedBy = addedBy,
                CreatedAt = now,
            });
        }
        else
        {
            existing.Kind = kind;
            existing.Capacity = capacity;
        }

        await db.SaveChangesAsync(ct);
        return new TeamRosterMutation(TeamRosterOutcome.Stored, null);
    }

    /// <summary>Remove a roster row; absent rows are idempotent success.</summary>
    public async Task<TeamRosterMutation> RemoveAsync(string teamSlug, string actor, string removedBy, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var removed = await db.TeamMembers
            .Where(m => m.Team!.Slug == teamSlug && m.Actor == actor)
            .ExecuteDeleteAsync(ct);
        audit.Append(db, new AuditRecord(
            removedBy,
            "teams.roster.remove",
            $"teams/{teamSlug}/members/{actor}",
            "allow",
            null,
            null,
            null,
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["rows"] = removed })));
        await db.SaveChangesAsync(ct);
        return new TeamRosterMutation(TeamRosterOutcome.Stored, null);
    }

}

/// <summary>One effective-config answer with the level it came from (audited).</summary>
/// <param name="Level">system | team | channel.</param>
/// <param name="ScopeId">The winning scope id (team slug or provider/channel); empty for system.</param>
/// <param name="Payload">The winning document (jsonb text).</param>
public sealed record EffectiveConfigAnswer(string Level, string ScopeId, string Payload);

/// <summary>
/// The one cross-scope switch resolver: system (<c>admin_config</c>) → team →
/// channel, most specific wins, every answer names its level. Team/channel
/// layers read <c>facilitator.team_settings</c>; a missing layer falls through.
/// </summary>
public sealed class EffectiveConfigResolver(IDbContextFactory<FacilitatorDbContext> contextFactory)
{
    /// <summary>Resolve a resource for (optionally) a team and channel scope.</summary>
    public async Task<EffectiveConfigAnswer?> ResolveAsync(
        string? teamSlug, string? channel, string resource, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await ResolveInTransactionAsync(db, teamSlug, channel, resource, ct);
    }

    /// <summary>The same resolution inside a caller's transaction/context —
    /// the one truth every cadence reader uses (D1).</summary>
    public static async Task<EffectiveConfigAnswer?> ResolveInTransactionAsync(
        FacilitatorDbContext db, string? teamSlug, string? channel, string resource, CancellationToken ct)
    {
        if (channel is not null && teamSlug is not null)
        {
            var channelRow = await db.TeamSettings.AsNoTracking().FirstOrDefaultAsync(
                s => s.Resource == resource && s.ScopeKind == "channel" && s.ScopeId == channel && s.Team!.Slug == teamSlug, ct);
            if (channelRow is not null)
            {
                return new EffectiveConfigAnswer("channel", channel, channelRow.Payload);
            }
        }

        if (teamSlug is not null)
        {
            var teamRow = await db.TeamSettings.AsNoTracking().FirstOrDefaultAsync(
                s => s.Resource == resource && s.ScopeKind == "team" && s.ScopeId == teamSlug && s.Team!.Slug == teamSlug, ct);
            if (teamRow is not null)
            {
                return new EffectiveConfigAnswer("team", teamSlug, teamRow.Payload);
            }
        }

        var system = await db.AdminConfig.AsNoTracking().FirstOrDefaultAsync(c => c.Resource == resource, ct);
        return system is null ? null : new EffectiveConfigAnswer("system", string.Empty, system.Payload);
    }
}
