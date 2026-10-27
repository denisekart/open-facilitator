using System.Net;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Dispatch;

/// <summary>
/// §7.2 lease recovery: a row whose lease expired (crashed replica) is
/// reclaimed by <see cref="IEventDispatcher.ReclaimExpiredLeasesAsync"/> and
/// delivered by the next drain. The UPDATE here simulates exactly what a dead
/// lease looks like after the 30 s window.
/// </summary>
[Collection("postgres:dispatch-link")]
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
        await fixture.Db.MarkOutboxLeaseExpired(workId);
        Assert.Equal(1, await fixture.Db.OutboxCountBy(workId, "leased"));

        var reclaimed = await dispatcher.ReclaimExpiredLeasesAsync(CancellationToken.None);
        Assert.Equal(1, reclaimed);
        Assert.Equal(1, await fixture.Db.OutboxCountBy(workId, "pending", noLeaseOwner: true));
        // The drain is global (shared suite DB, parallel collections leave
        // their own pending rows), so assert OUR row via the probe and treat
        // the drain count / dev ledger as at-least-this-row, like the
        // sibling restart-survival poll does.
        Assert.True(await dispatcher.DrainOnceAsync(CancellationToken.None) >= 1);
        Assert.Equal(1, await fixture.Db.OutboxCountBy(workId, "delivered", attempts: 2));

        var ledger = await KernelOps.GetJsonAsync(client, "/api/dev/deliveries", HttpStatusCode.OK);
        Assert.True(ledger.GetArrayLength() >= 1);
    }
}
