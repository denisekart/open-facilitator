using System.Text.Json;

namespace Facilitator.Tests.Link;

/// <summary>
/// Delivery flow: an outbox row addressed
/// <c>link.&lt;endpoint&gt;</c> reaches the live socket as a Deliver frame
/// whose envelope passes the shared validator, and an accepted receipt flips
/// outbox AND ledger in one transaction with the receipt stored verbatim.
/// Also proves the hub Envelope method answers the typed-ack equivalent of
/// POST /events (assign FROM a link is illegal). The dispatcher is off, so
/// the pump/ledger are the sole actors on link rows.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class DeliveryFlowTests(DispatchDbFixture fixture)
{
    [Fact]
    public async Task QueuedAssignReachesSocketAndReceiptFlipsLedgerAndOutbox()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, useKestrel: true);
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-df-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);

        var envelope = LinkTestHarness.AssignEnvelopeFor(endpoint);
        var commandId = JsonDocument.Parse(envelope).RootElement.GetProperty("data").GetProperty("commandId").GetString()!;
        await LinkTestHarness.QueueLinkOutboxAsync(kernel, endpoint, envelope);

        await using var client = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        await client.HelloAsync(LinkTestHarness.HelloFrame(endpoint));

        var delivery = await client.NextDeliverAsync();
        var deliveryId = delivery.GetProperty("deliveryId").GetGuid();
        Assert.True(delivery.GetProperty("deliveredAt").TryGetDateTime(out _), "deliveredAt is a timestamp");
        var pushed = delivery.GetProperty("envelope");
        KernelOps.AssertEnvelopesValid(new[] { pushed.GetRawText() });
        Assert.Equal(commandId, pushed.GetProperty("data").GetProperty("commandId").GetString());
        Assert.Equal(deliveryId, await fixture.Db.OutboxIdByCommand(commandId));

        await client.ReceiptAsync(LinkTestHarness.ReceiptFrame(deliveryId, "accepted"));

        Assert.True(await fixture.Db.OutboxDelivered(deliveryId));
        Assert.True(await fixture.Db.DeliveryFullyAcked(deliveryId));
    }

    [Fact]
    public async Task AssignOverHubEnvelopeIsRejectedTyped()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, useKestrel: true);
        var endpoint = "gw-df-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint);

        await using var client = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        await client.HelloAsync(LinkTestHarness.HelloFrame(endpoint));

        // kernel→link only: the hub answers the EnvelopeAck equivalent of the 422.
        var fixtureDoc = JsonDocument.Parse(PostgresFixture.ReadFixture("valid/command.work.assign.json")).RootElement;
        var ack = await client.EnvelopeAsync(fixtureDoc);

        Assert.Equal("rejected", ack.GetProperty("status").GetString());
        Assert.Equal(
            "https://errors.openfacilitator.org/v1/unsupported",
            ack.GetProperty("problem").GetProperty("type").GetString());
    }
}
