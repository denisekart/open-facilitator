using System.Text.Json;

namespace Facilitator.Tests.Link;

/// <summary>
/// spec S:291 + §26.1 item 2 server half: after the socket dies WITHOUT a
/// receipt, a fresh hello (new epoch) replays exactly the un-acked delivery —
/// the resume cursor (<c>lastKnownAck</c>) is a hint, the receipt ledger is
/// truth. An acked delivery never replays. Real wire: SignalR over WebSockets.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ReplayOnReconnectTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DroppedSocketReplaysOnlyTheUnackedDeliveryOnReconnect()
    {
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, fakeClock: true, useKestrel: true,
            settings: new Dictionary<string, string?> { ["Link:InFlightLeaseSeconds"] = "5" });
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-rr-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);

        // two queued assignments; the first gets receipted, the second does not.
        var ackedEnvelope = LinkTestHarness.AssignEnvelopeFor(endpoint);
        var lostEnvelope = LinkTestHarness.AssignEnvelopeFor(endpoint);
        await LinkTestHarness.QueueLinkOutboxAsync(kernel, endpoint, ackedEnvelope);
        await LinkTestHarness.QueueLinkOutboxAsync(kernel, endpoint, lostEnvelope);

        await using var first = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        await first.HelloAsync(LinkTestHarness.HelloFrame(endpoint));
        Guid ackedId;
        Guid lostId;

        // deterministic claim order: delivery_id ordering only groups after
        // delivered_at; both share one pump pass, so identify by envelope.
        var got = new Dictionary<Guid, string>();
        for (var i = 0; i < 2; i++)
        {
            var frame = await first.NextDeliverAsync();
            got[frame.GetProperty("deliveryId").GetGuid()] = frame.GetProperty("envelope").GetProperty("data").GetProperty("commandId").GetString()!;
        }

        var ackedCommandId = JsonDocument.Parse(ackedEnvelope).RootElement.GetProperty("data").GetProperty("commandId").GetString()!;
        var lostCommandId = JsonDocument.Parse(lostEnvelope).RootElement.GetProperty("data").GetProperty("commandId").GetString()!;
        ackedId = got.First(kv => kv.Value == ackedCommandId).Key;
        lostId = got.First(kv => kv.Value == lostCommandId).Key;

        await first.ReceiptAsync(LinkTestHarness.ReceiptFrame(ackedId, "accepted"));

        // the socket dies with the second delivery in flight, no receipt.
        await first.Connection.DisposeAsync();

        // the in-flight lease must lapse before the row is re-claimable (§5.4).
        factory.Clock!.Advance(TimeSpan.FromSeconds(6));

        await using var second = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        var welcome = await second.HelloAsync(LinkTestHarness.HelloFrame(endpoint, "v1"));
        Assert.Equal(2, welcome.GetProperty("leaseEpoch").GetInt64());

        var replay = await second.NextDeliverAsync();
        Assert.Equal(lostId, replay.GetProperty("deliveryId").GetGuid());
        await second.AssertNoDeliverAsync(500); // the acked one never comes back

        // stale lastKnownAck is a hint, ignored: replay happened anyway while
        // the ledger keeps the acked row terminal.
        Assert.True(await fixture.Db.DeliveryState(ackedId, "acked"));
        Assert.True(await fixture.Db.DeliveryState(lostId, "in_flight", attempts: 2));
    }

    [Fact]
    public async Task GoodbyeRevokesTheSessionAndAcksNothing()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, useKestrel: true);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-rr-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);

        await using var client = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        var welcome = await client.HelloAsync(LinkTestHarness.HelloFrame(endpoint));
        var sessionId = welcome.GetProperty("sessionId").GetString()!;

        await client.GoodbyeAsync(LinkTestHarness.GoodbyeFrame(sessionId));

        Assert.Equal(0, await fixture.Db.SessionCount(endpoint));

        // an unacked delivery survives the goodbye (it acks nothing) — the
        // next hello re-claims it once the in-flight lease lapses; here the
        // row never left the ledger because nothing was claimed.
        var link = factory.CreateLinkClient(token);
        using var probe = await link.GetAsync("/api/link/v1/deliveries?limit=1");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, probe.StatusCode); // session-not-found: revoked
    }
}
