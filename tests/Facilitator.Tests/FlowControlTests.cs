using System.Text.Json;

namespace Facilitator.Tests;

/// <summary>
/// spec S:304: with maxInFlight=1 the server pauses the client (FlowControl
/// maxInFlight:0, the frozen schema allows 0) once the window is full, and
/// resumes (FlowControl maxInFlight:configured) when a receipt frees
/// capacity — after which the next queued row is delivered.
/// </summary>
[Collection("postgres")]
public sealed class FlowControlTests(PostgresFixture fixture)
{
    [Fact]
    public async Task WindowFullPausesAndReceiptResumesAndDeliversNext()
    {
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, useKestrel: true,
            settings: new Dictionary<string, string?> { ["Link:MaxInFlight"] = "1" });
        using var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-fc-" + Guid.NewGuid().ToString("N")[..8];
        var token = await LinkTestHarness.EnrollAsync(kernel, endpoint);

        await LinkTestHarness.QueueLinkOutboxAsync(kernel, endpoint, LinkTestHarness.AssignEnvelopeFor(endpoint));
        await LinkTestHarness.QueueLinkOutboxAsync(kernel, endpoint, LinkTestHarness.AssignEnvelopeFor(endpoint));

        await using var client = await FakeLinkClient.ConnectAsync(factory.ServerAddress, token);
        await client.HelloAsync(LinkTestHarness.HelloFrame(endpoint));

        // claim 1 of 2; window now full ⇒ advisory pause.
        var first = await client.NextDeliverAsync();
        var firstId = first.GetProperty("deliveryId").GetGuid();
        var pause = await client.NextFlowAsync();
        Assert.Equal("flow_control", pause.GetProperty("kind").GetString());
        Assert.Equal(0, pause.GetProperty("maxInFlight").GetInt64());

        // the second row stays pending while paused-window is empty.
        await client.AssertNoDeliverAsync();

        await client.ReceiptAsync(LinkTestHarness.ReceiptFrame(firstId, "accepted"));

        // receipt frees capacity ⇒ resume hint + the second delivery.
        var resume = await client.NextFlowAsync();
        Assert.Equal(1, resume.GetProperty("maxInFlight").GetInt64());
        var second = await client.NextDeliverAsync();
        Assert.NotEqual(firstId, second.GetProperty("deliveryId").GetGuid());
    }
}
