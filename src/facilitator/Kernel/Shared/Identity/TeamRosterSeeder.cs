using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Identity;

using LinkOptions = Facilitator.Kernel.Link.LinkOptions;

/// <summary>
/// Team roster seeder: <c>Link:Teams</c> slugs are inserted into
/// <c>facilitator.teams</c> at startup when absent (the RoleStore
/// "seed-when-row-absent" pattern — config bootstraps, DB is truth). Enrolling
/// with a team the roster lacks is a 403, never a silent new team. Idempotent under
/// concurrent replicas via ON CONFLICT DO NOTHING on the unique slug index.
/// </summary>
public sealed class TeamRosterSeeder(
    LinkOptions options,
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IConfiguration configuration,
    ILogger<TeamRosterSeeder> logger,
    TimeProvider timeProvider) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Teams.Count == 0)
        {
            return;
        }

        // Same gate as DatabaseInitializer: with AutoMigrate=false the schema
        // is not ours to touch (health-only tests boot without a database).
        if (configuration["Database:AutoMigrate"] == "false")
        {
            logger.LogInformation("Database:AutoMigrate=false — skipping team roster seeding");
            return;
        }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var values = string.Join(", ", options.Teams.Select((_, i) => $"(@p{i}::uuid, @s{i}::text, @n::timestamptz)"));
        var parameters = options.Teams
            .Select((_, i) => new Npgsql.NpgsqlParameter($"@p{i}", Guid.NewGuid()))
            .Concat(options.Teams.Select((slug, i) => new Npgsql.NpgsqlParameter($"@s{i}", slug)))
            .Append(new Npgsql.NpgsqlParameter("@n", timeProvider.GetUtcNow().UtcDateTime));
        var sql = $"INSERT INTO facilitator.teams (team_id, slug, created_at) VALUES {values} ON CONFLICT (slug) DO NOTHING";
        await db.Database.ExecuteSqlRawAsync(sql, [.. parameters], cancellationToken);
        await SeedMembersAsync(db, cancellationToken);
    }

    /// <summary>
    /// Roster bootstrap: <c>identity:roster</c> entries
    /// (<c>{ team, actor, capacity }</c>) insert a <c>team_members</c> row when
    /// the team has no row for that actor (ON CONFLICT DO NOTHING — a human
    /// edit always wins over config). Same DB-is-truth posture as the teams
    /// themselves and as the role store's config seed.
    /// </summary>
    private async Task SeedMembersAsync(FacilitatorDbContext db, CancellationToken cancellationToken)
    {
        var section = configuration.GetSection("identity:roster");
        var entries = section.Get<List<RosterSeedEntry>>() ?? [];
        if (entries.Count == 0)
        {
            return;
        }

        var bySlug = await db.Teams.AsNoTracking().ToDictionaryAsync(
            t => t.Slug, t => t.TeamId, StringComparer.Ordinal, cancellationToken);
        var seeded = 0;
        foreach (var entry in entries)
        {
            if (entry.Team is not { } teamSlug || entry.Actor is not { } actor)
            {
                logger.LogWarning("identity:roster entry skipped (team or actor missing)");
                continue;
            }

            if (!Facilitator.Kernel.Teams.TeamRosterStore.IsValidActor(actor)
                || !bySlug.TryGetValue(teamSlug, out var teamId))
            {
                logger.LogWarning("identity:roster entry for team '{Team}' skipped (unknown team or malformed actor)", teamSlug);
                continue;
            }

            var capacity = entry.Capacity == "operator" ? "operator" : "member";
            await SeedRowAsync(db, teamId, actor, capacity, cancellationToken);
            seeded++;
        }

        logger.LogInformation("team roster seeded from identity:roster ({Count} entries) — DB is truth thereafter", seeded);
    }

    /// <summary>Idempotent roster insert (ON CONFLICT DO NOTHING on the unique
    /// (team_id, actor) key — a human edit always wins over config). The raw SQL
    /// stays with the seeder, where the teams insert already lives.</summary>
    private async Task SeedRowAsync(
        FacilitatorDbContext db, Guid teamId, string actor, string capacity, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO facilitator.team_members (team_id, actor, kind, capacity, added_by, created_at)
             VALUES ({teamId}, {actor}, 'human', {capacity}, 'config', {timeProvider.GetUtcNow().UtcDateTime})
             ON CONFLICT (team_id, actor) DO NOTHING
             """, cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>One <c>identity:roster</c> entry.</summary>
    public sealed class RosterSeedEntry
    {
        /// <summary>Team slug.</summary>
        public string? Team { get; set; }

        /// <summary>Canonical actor (<c>oidc:&lt;iss&gt;|&lt;sub&gt;</c>).</summary>
        public string? Actor { get; set; }

        /// <summary><c>member</c> (default) or <c>operator</c>.</summary>
        public string? Capacity { get; set; }
    }
}
