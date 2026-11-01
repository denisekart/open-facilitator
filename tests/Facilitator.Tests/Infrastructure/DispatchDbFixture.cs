using Testcontainers.PostgreSql;

namespace Facilitator.Tests.Infrastructure;

/// <summary>
/// Dedicated Postgres container for the dispatch-link family. The dispatcher's
/// claim is global over the outbox table and the link pump sweeps every
/// expired lease, so any foreign replica sharing the DB can steal this
/// family's drain-the-whole-outbox assertions or delete its seeded sessions
/// under a foreign fake clock. Inside this family the collection serializes;
/// outside it, no foreign dispatcher or pump can reach these rows. The
/// container stops with the collection; other families never see it.
/// </summary>
public sealed class DispatchDbFixture : PostgresFixture
{
    private static readonly Task<PostgreSqlContainer> Dedicated =
        new Lazy<Task<PostgreSqlContainer>>(() => StartAsync(setMainEnv: false)).Value;

    /// <inheritdoc />
    protected override Task<PostgreSqlContainer> Container => Dedicated;

    /// <inheritdoc />
    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        var container = await Dedicated;
        await container.StopAsync();
        await container.DisposeAsync();
    }
}
