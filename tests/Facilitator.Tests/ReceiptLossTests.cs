using System.Diagnostics;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests;

/// <summary>
/// spec S:302 + §26.1 item 3 (lost-receipt loop): a delivery that is never
/// receipted becomes re-claimable once its in-flight lease passes, the replay
/// is the SAME deliveryId (ledger identity), attempts increments, and a second
/// receipt is 204-idempotent — the dropped-receipt → redelivery → duplicate
/// loop terminates. Pure HTTP fallback (no socket): claims run through
/// GET /deliveries. Fake clock: the in-flight lease lapses by advancing the
/// clock — no wall-clock sleeps.
/// </summary>
[Collection("postgres")]
public sealed class ReceiptLossTests(PostgresFixture fixture)
{
    [Fact]
    public async Task UnreceiptedDeliveryReOffersWithSameIdAndSecondReceiptIsNoop()
    {
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, fakeClock: true, settings: new Dictionary<string, string?> { ["Link:InFlightLeaseSeconds"] = "1" });
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-rl-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);

        // a live session row is the delivery-read precondition (locked rule):
        // a hub handshake would insert it; an HTTP-only principal refreshes it
        // implicitly — open one directly through the session service.
        var sessions = factory.Services.GetRequiredService<Facilitator.Kernel.Link.LinkService>();
        await sessions.OpenSessionAsync(endpoint, "http-1", null, 300, CancellationToken.None);

        await LinkTestHarness.QueueLinkOutboxAsync(kernel, endpoint, LinkTestHarness.AssignEnvelopeFor(endpoint));

        var first = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?wait=2&limit=16", System.Net.HttpStatusCode.OK);
        var deliveryId = Guid.Parse(first[0].GetProperty("deliveryId").GetString()!);
        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM facilitator.link_deliveries WHERE delivery_id = '{deliveryId}' AND state = 'in_flight' AND attempts = 1"));

        // no receipt; advance past the 1 s in-flight lease (fake clock, no sleep).
        factory.Clock!.Advance(TimeSpan.FromSeconds(2));
        var replay = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", System.Net.HttpStatusCode.OK);
        Assert.Equal(
            deliveryId,
            Guid.Parse(Assert.Single(replay.EnumerateArray()).GetProperty("deliveryId").GetString()!));
        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM facilitator.link_deliveries WHERE delivery_id = '{deliveryId}' AND attempts = 2"));

        using var receipt = await link.PostAsync(
            $"/api/link/v1/deliveries/{deliveryId}/receipt",
            new StringContent(LinkTestHarness.ReceiptBody("accepted"), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, receipt.StatusCode);

        // second receipt for the same deliveryId: 204 again, no double-flip.
        using var second = await link.PostAsync(
            $"/api/link/v1/deliveries/{deliveryId}/receipt",
            new StringContent(LinkTestHarness.ReceiptBody("duplicate"), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, second.StatusCode);

        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM facilitator.link_deliveries WHERE delivery_id = '{deliveryId}' AND state = 'acked' AND receipt->>'status' = 'duplicate'"));
        Assert.Equal(1, await fixture.CountAsync(
            $"SELECT count(*) FROM facilitator.outbox WHERE outbox_id = '{deliveryId}' AND status = 'delivered'"));

        // the ledger is drained: the next claim answers [].
        var after = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", System.Net.HttpStatusCode.OK);
        Assert.Empty(after.EnumerateArray());
    }

    [Fact]
    public async Task LongPollWaitsAtLeastTheRequestedSecondsWhenEmpty()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-rl-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);
        var sessions = factory.Services.GetRequiredService<Facilitator.Kernel.Link.LinkService>();
        await sessions.OpenSessionAsync(endpoint, "http-1", null, 300, CancellationToken.None);

        var started = Stopwatch.GetTimestamp();
        var empty = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?wait=2&limit=16", System.Net.HttpStatusCode.OK);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Empty(empty.EnumerateArray());
        Assert.True(elapsed >= TimeSpan.FromSeconds(1.9), $"long-poll returned after {elapsed}");
    }
}
