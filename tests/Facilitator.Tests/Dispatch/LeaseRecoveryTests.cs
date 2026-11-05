using System.Net;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Dispatch;

/// <summary>
/// Lease recovery: a row whose lease expired (crashed replica) is
/// reclaimed by <see cref="IEventDispatcher.ReclaimExpiredLeasesAsync"/> and
/// delivered by the next drain. The UPDATE here simulates exactly what a dead
/// lease looks like after the 30 s window.
/// </summary>
[Collection("postgres:dispatch-link")]
public class LeaseRecoveryTests(DispatchDbFixture fixture)
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

        // Reclaim is global over the table, and the family's own hosts can
        // leave dead-lease debris — assert OUR row via the probe below and
        // treat the reclaim count as at-least-this-row, like the drain below.
        Assert.True(await dispatcher.ReclaimExpiredLeasesAsync(CancellationToken.None) >= 1);
        Assert.Equal(1, await fixture.Db.OutboxCountBy(workId, "pending", noLeaseOwner: true));
        // The drain is global (shared suite DB, parallel classes leave
        // their own pending rows), so the drain count / dev ledger are
        // at-least-this-row, like the sibling restart-survival poll.
        Assert.True(await dispatcher.DrainOnceAsync(CancellationToken.None) >= 1);
        Assert.Equal(1, await fixture.Db.OutboxCountBy(workId, "delivered", attempts: 2));

        var deliveries = await KernelOps.GetJsonAsync(client, "/api/admin/deliveries?state=delivered", HttpStatusCode.OK);
        Assert.True(deliveries.GetProperty("items").GetArrayLength() >= 1);
    }
}
