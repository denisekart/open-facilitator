using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Storage;

/// <summary>
/// Router-team minimalism is DATA, not code: nothing is special about a
/// team's slug — what makes a team a router is that it holds only
/// enrollments and flows. This boot check names the failure mode the
/// posture creates: a team that still receives enrollments but runs no
/// enabled flows has become a content master by neglect (inbound bytes
/// land on it and age there). One query, at most one warning per team,
/// never fatal, no state — a WARN the operator acts on, not a gate.
/// </summary>
public sealed class RouterTeamHygiene(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<RouterTeamHygiene> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Same gate as the gallery seeder: with AutoMigrate=false the
        // database is not ours to read at boot.
        if (configuration["Database:AutoMigrate"] == "false")
        {
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var neglects = await db.Teams.AsNoTracking()
            .Where(t => db.LinkEnrollments.Any(e => e.TeamId == t.TeamId)
                        && !db.WorkflowDefinitions.Any(d => d.Team == t.Slug && d.Enabled)
                        && db.Artifacts.Any(a => a.TeamSlug == t.Slug
                                                && a.WorkId == null
                                                && a.CreatedAt < now.AddHours(-1)))
            .Select(t => new
            {
                t.Slug,
                Count = db.Artifacts.Count(a => a.TeamSlug == t.Slug
                                                && a.WorkId == null
                                                && a.CreatedAt < now.AddHours(-1)),
            })
            .ToListAsync(cancellationToken);
        foreach (var team in neglects)
        {
            logger.LogWarning(
                "team '{Slug}' has enrollments and no enabled flows with {Count} aged artifacts — a router team by neglect",
                team.Slug, team.Count);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
