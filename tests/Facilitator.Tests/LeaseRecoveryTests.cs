using System.Net;

using Facilitator.Kernel.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests;

/// <summary>
/// §7.2 lease recovery: a row whose lease expired (crashed replica) is
/// reclaimed by <see cref="IEventDispatcher.ReclaimExpiredLeasesAsync"/> and
/// delivered by the next drain. The UPDATE here simulates exactly what a dead
/// lease looks like after the 30 s window.
/// </summary>
[Collection("postgres")]
public class LeaseRecoveryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ExpiredLease_ReclaimedAndDelivered()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "lease recovery");

        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();

        // Simulate a crashed claim: the row is leased, lease long expired,
        // attempt already charged, nothing delivered.
        await fixture.ExecuteAsync(
            $"UPDATE facilitator.outbox SET status = 'leased', lease_owner = 'dead-replica', " +
            "leased_until = now() - interval '1 second', attempts = 1 " +
            $"WHERE work_id = '{workId}'");
        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM facilitator.outbox WHERE work_id = '{workId}' AND status = 'leased'"));

        var reclaimed = await dispatcher.ReclaimExpiredLeasesAsync(CancellationToken.None);
        Assert.Equal(1, reclaimed);
        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM facilitator.outbox WHERE work_id = '{workId}' AND status = 'pending' AND lease_owner IS NULL"));
        Assert.Equal(1, await dispatcher.DrainOnceAsync(CancellationToken.None));
        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM facilitator.outbox WHERE work_id = '{workId}' AND status = 'delivered' AND attempts = 2"));

        var ledger = await KernelOps.GetJsonAsync(client, "/api/dev/deliveries", HttpStatusCode.OK);
        Assert.Equal(1, ledger.GetArrayLength());
    }
}
