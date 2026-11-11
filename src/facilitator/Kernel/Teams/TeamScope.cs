using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Teams;

/// <summary>
/// Team-scope authority: the single place that decides whether a caller may
/// see a team's private resource. A caller with no team claim (operators,
/// workloads acting alone) is unscoped and sees everything; a caller scoped to
/// a team sees its own teams, and a foreign team's resource answers
/// <b>not-found</b> — never a 403 that confirms existence. A team whose roster
/// has no rows is unconfigured: membership is not asserted, so a team-scoped
/// caller that names the team through its group claim still sees it (the roster
/// gates the moment it exists).
/// </summary>
public sealed class TeamScope(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    TeamRosterStore roster)
{
    /// <summary>The caller's team slugs, from <c>/teams/&lt;slug&gt;</c> groups.</summary>
    public static IReadOnlyList<string> TeamsOf(FacilitatorPrincipal principal) =>
        principal.Groups
            .Where(static g => g.StartsWith("/teams/", StringComparison.Ordinal) && g.Length > "/teams/".Length)
            .Select(static g => g["/teams/".Length..])
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>True when the caller may read a resource owned by <paramref name="teamSlug"/>.</summary>
    /// <param name="principal">The verified principal (null: an unscoped internal caller).</param>
    /// <param name="teamSlug">Owning team slug (null: an unowned resource, visible to any authenticated caller).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> MayReadAsync(FacilitatorPrincipal? principal, string? teamSlug, CancellationToken ct)
    {
        if (principal is null || teamSlug is null)
        {
            return true;
        }

        if (principal.Roles.Contains("kernel-operator") || principal.Roles.Contains("framework-admin"))
        {
            return true;
        }

        var teams = TeamsOf(principal);
        if (teams.Count == 0)
        {
            // No team claim: unscoped (the posture every pre-roster caller had).
            return true;
        }

        if (!teams.Contains(teamSlug, StringComparer.Ordinal))
        {
            return false;
        }

        return !await roster.IsConfiguredAsync(teamSlug, ct) || await roster.IsMemberAsync(teamSlug, principal.Actor, ct);
    }

    /// <summary>Team slug of a work: its own <c>team_id</c> when set, else its project's team.</summary>
    public async Task<string?[]> TeamOfWorkAsync(Guid workId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => new[] { w.Team != null ? w.Team.Slug : (w.Project == null ? null : w.Project.Team!.Slug) })
            .FirstOrDefaultAsync(ct) ?? [];
    }
}
