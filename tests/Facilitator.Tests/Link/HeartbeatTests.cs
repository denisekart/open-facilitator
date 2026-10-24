using Facilitator.Kernel.Link;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Link;

/// <summary>
/// §7.3: the DB lease — not TCP — ends a session. Heartbeat refreshes
/// lease_until for a matching epoch only; a stale epoch gets false + a typed
/// Disconnect; the pump's sweeper closes lease-expired sessions (fake clock:
/// no wall-clock sleeps beyond the pump's own 2 s tick).
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class HeartbeatTests(PostgresFixture fixture)
{
    private static readonly IReadOnlyDictionary<string, string?> Fast =
        new Dictionary<string, string?> { ["Link:HeartbeatSeconds"] = "5" };

    [Fact]
    public async Task HeartbeatRefreshesLeaseAndStaleEpochIsRefused()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, fakeClock: true, useKestrel: true, settings: Fast);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-hbt-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var sessions = factory.Services.GetRequiredService<LinkService>();

        await using var first = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        var welcome = await first.HelloAsync(LinkTestHarness.HelloFrame(endpoint));
        var sessionId = welcome.GetProperty("sessionId").GetString()!;
        var epoch = welcome.GetProperty("leaseEpoch").GetInt64();

        // OpenSession: lease = fakeNow + max(15, 5*3) = +15 s.
        Assert.True(await LeaseBeyond(factory, endpoint, factory.Clock!.GetUtcNow().UtcDateTime.AddSeconds(14)));

        factory.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await first.HeartbeatAsync(LinkTestHarness.HeartbeatFrame(sessionId, epoch)));
        Assert.True(await LeaseBeyond(factory, endpoint, factory.Clock.GetUtcNow().UtcDateTime.AddSeconds(14)));

        // a second hello bumps the epoch; the OLD epoch's heartbeat is refused
        // and answered with a typed goodbye on the old socket.
        await using var fresh = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        await fresh.HelloAsync(LinkTestHarness.HelloFrame(endpoint));

        Assert.False(await first.HeartbeatAsync(LinkTestHarness.HeartbeatFrame(sessionId, epoch)));
        var goodbye = await first.NextDisconnectAsync();
        Assert.Equal("session-superseded", goodbye.GetProperty("reason").GetString());
        var live = await sessions.TryGetLiveAsync(endpoint, CancellationToken.None);
        Assert.Equal(epoch + 1, live!.SessionEpoch);
    }

    [Fact]
    public async Task SweeperClosesLeaseExpiredSession()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, fakeClock: true, useKestrel: true, settings: Fast);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-hbt-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var sessions = factory.Services.GetRequiredService<LinkService>();

        await using var client = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        await client.HelloAsync(LinkTestHarness.HelloFrame(endpoint));

        // lease = fakeNow + 15 s; jump past it. The pump sweeps on its 2 s
        // wall-time tick — that wait is the pump's cadence, not a lease sleep.
        factory.Clock!.Advance(TimeSpan.FromSeconds(16));

        var goodbye = await client.NextDisconnectAsync(8_000);
        Assert.Equal("lease-expired", goodbye.GetProperty("reason").GetString());
        Assert.Null(await sessions.TryGetLiveAsync(endpoint, CancellationToken.None));
    }

    private async Task<bool> LeaseBeyond(KernelTestFactory factory, string endpoint, DateTime instant) =>
        await fixture.Db.SessionLeaseBeyond(endpoint, instant);
}
