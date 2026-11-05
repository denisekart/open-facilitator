using Testcontainers.PostgreSql;

namespace Facilitator.Tests.Infrastructure;

/// <summary>
/// Dedicated Postgres container for the fake-clock retention-GC family.
/// A GC pass driven on a fake clock advanced past the artifact allocation
/// TTL (the permanent/archive never-expire proof runs at now + 100 days)
/// treats every foreign collection's fresh <c>pending</c> slot as expired
/// and tombstones it in the shared DB — a parallel upload vertical then
/// PUTs into a 404. Inside this family the collection serializes; outside
/// it, no foreign fake-clock sweep can reach these rows and none of these
/// passes can reach foreign rows.
/// </summary>
public sealed class GcDbFixture : PostgresFixture
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
