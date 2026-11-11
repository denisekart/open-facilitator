using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Teams;

/// <summary>A team as served by <c>GET /api/teams</c>.</summary>
/// <param name="Slug">Team slug.</param>
/// <param name="RosterConfigured">False when the team has no roster rows (membership unasserted).</param>
/// <param name="Members">Roster size.</param>
/// <param name="CallerCapacity">member | operator | null when the caller is not on the roster.</param>
public sealed record TeamView(string Slug, bool RosterConfigured, int Members, string? CallerCapacity);

/// <summary>Team directory reads (the caller's own teams, with their standing).</summary>
public sealed class TeamDirectory(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    TeamRosterStore roster)
{
    /// <summary>Every team the caller may see: operators/admins see all; a team-scoped
    /// caller sees the teams its claims name.</summary>
    public async Task<List<TeamView>> ListAsync(string actor, FacilitatorPrincipal? principal, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var teams = await db.Teams.AsNoTracking().OrderBy(static t => t.Slug).Select(t => t.Slug).ToListAsync(ct);
        var scoped = principal is null ? [] : TeamScope.TeamsOf(principal);
        var privileged = principal is null
            || principal.Roles.Contains("kernel-operator")
            || principal.Roles.Contains("framework-admin")
            || scoped.Count == 0;

        var views = new List<TeamView>();
        foreach (var slug in teams)
        {
            if (!privileged && !scoped.Contains(slug, StringComparer.Ordinal))
            {
                continue;
            }

            var members = await roster.ListAsync(slug, ct);
            views.Add(new TeamView(
                slug,
                members.Count > 0,
                members.Count,
                members.FirstOrDefault(m => m.Actor == actor)?.Capacity));
        }

        return views;
    }

    /// <summary>One roster row after a write (404-safe: null when the row is gone).</summary>
    public async Task<TeamMemberView?> MemberViewAsync(string slug, string actor, CancellationToken ct)
    {
        var rows = await roster.ListAsync(slug, ct);
        return rows.FirstOrDefault(r => r.Actor == actor);
    }
}

/// <summary>Switch-row mutation outcome.</summary>
public enum TeamSettingOutcome
{
    /// <summary>The write landed.</summary>
    Stored,

    /// <summary>Unknown team — 404.</summary>
    TeamUnknown,

    /// <summary>Payload shape violation — 400.</summary>
    Invalid,

    /// <summary>If-Match row-version mismatch — 412.</summary>
    Conflict,
}

/// <summary>The stored revision a refused write collided with (0 = row absent).</summary>

/// <summary>Stored per-scope switch view (the API shape).</summary>
public sealed record TeamSettingView(string TeamSlug, string ScopeKind, string ScopeId, string Resource, JsonElement Payload, long RowVersion, string Author, string Reason, DateTime UpdatedAt);

/// <summary>Result of a switch write.</summary>
public sealed record TeamSettingMutation(TeamSettingOutcome Outcome, TeamSettingView? View, string? Detail, long StoredVersion = 0)
{
    /// <summary>True when the caller may serve 200.</summary>
    public bool Stored => Outcome == TeamSettingOutcome.Stored;
}

/// <summary>
/// Writes to <c>facilitator.team_settings</c>: version-fenced, attributed,
/// reasoned — the same posture as the system-wide admin-config plane, one
/// scope narrower. Payload shape is validated by the reading feature's rules
/// (an unread switch is inert, so garbage stays out at the edge).
/// </summary>
public sealed class TeamSettingsStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>Payload cap in bytes (jsonb, verbatim).</summary>
    public const int MaxPayloadBytes = 16 * 1024;

    /// <summary>Insert or version-fenced update; same-tx audit row.</summary>
    /// <param name="teamSlug">Team the row belongs to.</param>
    /// <param name="scopeKind">team | channel.</param>
    /// <param name="scopeId">Team slug or <c>&lt;provider&gt;/&lt;channel&gt;</c>.</param>
    /// <param name="resource">Resource name.</param>
    /// <param name="payload">Proposed document.</param>
    /// <param name="reason">Change reason (required).</param>
    /// <param name="author">Canonical actor of the writer.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="expectedRowVersion">If-Match version; null = force.</param>
    public async Task<TeamSettingMutation> PutAsync(
        string teamSlug,
        string scopeKind,
        string scopeId,
        string resource,
        JsonElement payload,
        string reason,
        string author,
        CancellationToken ct,
        long? expectedRowVersion = null)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return new TeamSettingMutation(TeamSettingOutcome.Invalid, null, "payload must be a JSON object");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) > MaxPayloadBytes)
        {
            return new TeamSettingMutation(TeamSettingOutcome.Invalid, null, $"payload exceeds the {MaxPayloadBytes} byte cap");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var team = await db.Teams.FirstOrDefaultAsync(t => t.Slug == teamSlug, ct);
        if (team is null)
        {
            return new TeamSettingMutation(TeamSettingOutcome.TeamUnknown, null, null);
        }

        var existing = await db.TeamSettings.FirstOrDefaultAsync(
            s => s.TeamId == team.TeamId && s.ScopeKind == scopeKind && s.ScopeId == scopeId && s.Resource == resource, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        long storedVersion;
        if (existing is null)
        {
            if (expectedRowVersion > 0)
            {
                return new TeamSettingMutation(TeamSettingOutcome.Conflict, null, null, 0);
            }

            storedVersion = 1;
            db.TeamSettings.Add(new TeamSettingEntity
            {
                TeamId = team.TeamId,
                ScopeKind = scopeKind,
                ScopeId = scopeId,
                Resource = resource,
                Payload = payload.GetRawText(),
                RowVersion = storedVersion,
                Author = author,
                Reason = reason,
                UpdatedAt = now,
            });
        }
        else if (expectedRowVersion is not null && existing.RowVersion != expectedRowVersion)
        {
            return new TeamSettingMutation(TeamSettingOutcome.Conflict, null, null, existing.RowVersion);
        }
        else
        {
            storedVersion = existing.RowVersion + 1;
            existing.Payload = payload.GetRawText();
            existing.RowVersion = storedVersion;
            existing.Author = author;
            existing.Reason = reason;
            existing.UpdatedAt = now;
        }

        audit.Append(db, new AuditRecord(
            author,
            "teams.setting.write",
            $"teams/{teamSlug}/{scopeKind}/{scopeId}/{resource}",
            "allow",
            null,
            null,
            null,
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["version"] = storedVersion,
            })));
        await db.SaveChangesAsync(ct);
        return new TeamSettingMutation(
            TeamSettingOutcome.Stored,
            new TeamSettingView(teamSlug, scopeKind, scopeId, resource, payload.Clone(), storedVersion, author, reason, now),
            null);
    }

    /// <summary>Delete a switch row (absent rows are idempotent success).</summary>
    public async Task RemoveAsync(string teamSlug, string scopeKind, string scopeId, string resource, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.TeamSettings
            .Where(s => s.Team!.Slug == teamSlug && s.ScopeKind == scopeKind && s.ScopeId == scopeId && s.Resource == resource)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Stored switch rows of a team (management view).</summary>
    public async Task<List<TeamSettingView>> ListAsync(string teamSlug, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.TeamSettings.AsNoTracking()
            .Where(s => s.Team!.Slug == teamSlug)
            .OrderBy(static s => s.Resource).ThenBy(static s => s.ScopeKind).ThenBy(static s => s.ScopeId)
            .ToListAsync(ct);
        return rows
            .Select(r => new TeamSettingView(
                teamSlug, r.ScopeKind, r.ScopeId, r.Resource,
                JsonDocument.Parse(r.Payload).RootElement.Clone(), r.RowVersion, r.Author, r.Reason, r.UpdatedAt))
            .ToList();
    }
}
