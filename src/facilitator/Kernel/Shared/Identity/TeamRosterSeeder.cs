using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Identity;

using LinkOptions = Facilitator.Kernel.Link.LinkOptions;

/// <summary>
/// Team roster seeder (Plan 10): <c>Link:Teams</c> slugs are inserted into
/// <c>facilitator.teams</c> at startup when absent (the RoleStore
/// "seed-when-row-absent" pattern — config bootstraps, DB is truth). Replaces
/// the Plan 03 on-demand <c>default</c>-team seeding: enrolling with a team
/// the roster lacks is a 403, never a silent new team. Idempotent under
/// concurrent replicas via ON CONFLICT DO NOTHING on the unique slug index.
/// </summary>
public sealed class TeamRosterSeeder(LinkOptions options, IDbContextFactory<FacilitatorDbContext> contextFactory, TimeProvider timeProvider) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Teams.Count == 0)
        {
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
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
