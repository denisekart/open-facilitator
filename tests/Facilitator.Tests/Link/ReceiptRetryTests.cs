using System.Net;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Link;

/// <summary>
/// Retryable-rejection law: a <c>rejected</c> receipt naming a
/// transient endpoint condition (<c>child-busy</c> — the gateway's own
/// "honest, retryable upstream" answer when a child's quota is full) does NOT
/// terminally settle the delivery. The ledger keeps the receipt, holds the row
/// in_flight behind the redelivery backoff (which charges the window, i.e.
/// applies flow control), and the pump re-offers it until an accept lands or
/// the attempt budget settles it like any other receipt. A permanent
/// rejection (policy-denied) stays terminal. Pure HTTP fallback + fake clock,
/// same rig as <see cref="ReceiptLossTests"/>.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ReceiptRetryTests(DispatchDbFixture fixture)
{
    private static string RejectedBody(string reason) =>
        JsonSerializer.Serialize(new { status = "rejected", reason, processedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") });

    private async Task<(KernelTestFactory Factory, HttpClient Link, Guid DeliveryId)> RigAsync()
    {
        var factory = new KernelTestFactory(
            fixture, noDispatcher: true, fakeClock: true,
            settings: new Dictionary<string, string?> { ["Link:RedeliverBackoffSeconds"] = "1" });
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-rr-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);
        var link = factory.CreateLinkClient(token);
        var sessions = factory.Services.GetRequiredService<Facilitator.Kernel.Shared.Persistence.LinkService>();
        await sessions.OpenSessionAsync(endpoint, "http-1", null, 300, CancellationToken.None);
        await LinkTestHarness.QueueLinkOutboxAsync(kernel, endpoint, LinkTestHarness.AssignEnvelopeFor(endpoint));
        var first = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", HttpStatusCode.OK);
        var deliveryId = Guid.Parse(Assert.Single(first.EnumerateArray()).GetProperty("deliveryId").GetString()!);
        return (factory, link, deliveryId);
    }

    private static async Task PostReceiptAsync(HttpClient link, Guid deliveryId, string body)
    {
        using var receipt = await link.PostAsync(
            $"/api/link/v1/deliveries/{deliveryId}/receipt",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, receipt.StatusCode);
    }

    [Fact]
    public async Task ChildBusyRejectionReoffersAndTheLaterAcceptSettlesIt()
    {
        var (factory, link, deliveryId) = await RigAsync();

        // The busy rejection: 204 (the receipt is real) but NOT terminal —
        // receipted in_flight behind the backoff, outbox still undelivered.
        await PostReceiptAsync(link, deliveryId, RejectedBody("child-busy: gw.rr/alpha"));
        Assert.True(await fixture.Db.DeliveryReceiptStatus(deliveryId, "rejected", state: "in_flight"));
        Assert.False(await fixture.Db.OutboxDelivered(deliveryId));

        // The empty claim window: before the backoff lapses the row is not re-offered.
        Assert.Empty((await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", HttpStatusCode.OK)).EnumerateArray());

        // After the backoff the SAME deliveryId comes back, attempts incremented.
        factory.Clock!.Advance(TimeSpan.FromSeconds(2));
        var replay = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", HttpStatusCode.OK);
        Assert.Equal(deliveryId, Guid.Parse(Assert.Single(replay.EnumerateArray()).GetProperty("deliveryId").GetString()!));
        Assert.True(await fixture.Db.DeliveryState(deliveryId, attempts: 2));

        // The child is free now: the accept settles ledger + outbox in one go.
        await PostReceiptAsync(link, deliveryId, LinkTestHarness.ReceiptBody("accepted"));
        Assert.True(await fixture.Db.DeliveryFullyAcked(deliveryId));
        Assert.True(await fixture.Db.OutboxDelivered(deliveryId));
        factory.Dispose();
    }

    [Fact]
    public async Task RetryableRejectionSettlesAtTheAttemptBudget()
    {
        var (factory, link, deliveryId) = await RigAsync();

        // Every claim answers child-busy: the budget (5 offers) is terminal
        // like any other receipt so a poison delivery still drains.
        for (var offer = 1; offer <= LinkLedger.MaxReceiptAttempts; offer++)
        {
            await PostReceiptAsync(link, deliveryId, RejectedBody("child-busy: gw.rr/alpha"));
            factory.Clock!.Advance(TimeSpan.FromSeconds(2));
            var next = await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", HttpStatusCode.OK);
            if (offer < LinkLedger.MaxReceiptAttempts)
            {
                Assert.NotEmpty(next.EnumerateArray()); // re-offered
                Assert.False(await fixture.Db.OutboxDelivered(deliveryId));
            }
        }

        Assert.True(await fixture.Db.DeliveryReceiptStatus(deliveryId, "rejected")); // state = 'acked'
        Assert.True(await fixture.Db.OutboxDelivered(deliveryId));
        factory.Dispose();
    }

    [Fact]
    public async Task PermanentRejectionStaysTerminal()
    {
        var (factory, link, deliveryId) = await RigAsync();

        await PostReceiptAsync(link, deliveryId, RejectedBody("policy-denied: coding.implement"));
        Assert.True(await fixture.Db.DeliveryReceiptStatus(deliveryId, "rejected")); // state = 'acked'
        Assert.True(await fixture.Db.OutboxDelivered(deliveryId));
        factory.Clock!.Advance(TimeSpan.FromSeconds(2));
        Assert.Empty((await KernelOps.GetJsonAsync(link, "/api/link/v1/deliveries?limit=16", HttpStatusCode.OK)).EnumerateArray());
        factory.Dispose();
    }
}
