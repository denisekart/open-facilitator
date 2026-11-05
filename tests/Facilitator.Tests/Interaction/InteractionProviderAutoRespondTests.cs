using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Facilitator.Tests.Interaction;

/// <summary>
/// The provider-plane auto-respond arm: a chat message landing on a
/// conversation bound (communication_bindings) to a work with a pending
/// <c>input.required</c> IS the answer — it settles the OLDEST unanswered
/// request (answer = message text, actorEvidenceRef = the provider delivery
/// evidence) instead of appending generic work input, unparks the assignment,
/// and routes the answer envelope to the bound execution endpoint. Once
/// nothing is pending, later messages keep the input-note behavior.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class InteractionProviderAutoRespondTests(DispatchDbFixture fixture) : IDisposable
{
    private readonly List<KernelTestFactory> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task BoundThreadMessageAnswersOldestPendingRequestOnce()
    {
        var endpoint = "gw-ix-" + Guid.NewGuid().ToString("N")[..8];
        var bound = "gw-run-" + Guid.NewGuid().ToString("N")[..8];
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = endpoint,
            ["Providers:Mattermost:Tenant"] = "demo-team",
            ["Providers:Mattermost:SiteUrl"] = "http://mattermost:8065",
        };
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        _factories.Add(factory);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";

        // A linked mention creates the work + binding (the originating integration).
        await link.PostAsync("/api/link/v1/events",
            new StringContent(ProviderEvent("valid/event.provider.mention.json", $"post-{Guid.NewGuid():N}",
                channel, root, "acct-1", "start the thing"), Encoding.UTF8, "application/json"));
        var workId = Assert.IsType<Guid>(await fixture.Db.BindingWorkId(channel, root));
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, bound);

        // Two questions park in sequence; the fan-out takes arm (a): the
        // reply lands on the binding's own thread through the provider endpoint.
        var first = InteractionWire.NewRequestId();
        var second = InteractionWire.NewRequestId();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, first, 1, "Include on-prem?", ["yes", "no"]),
            HttpStatusCode.Accepted);
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, second, 1, "Ship Friday?"),
            HttpStatusCode.Accepted);
        var fanout = await fixture.Db.OutboxEnvelopeByCommand("input-fanout:" + first);
        Assert.NotNull(fanout);
        Assert.Contains("command.communication.reply.v1", fanout, StringComparison.Ordinal);
        Assert.Contains("1) yes", fanout, StringComparison.Ordinal);
        Assert.Contains($"(request {first})", fanout, StringComparison.Ordinal);
        Assert.Equal([$"link.{endpoint}"], await fixture.Db.OutboxRecipientsByCommand("input-fanout:" + first));

        // The human's reply in the thread: the OLDEST pending request settles,
        // with the provider delivery as the actor evidence; nothing else answers.
        var delivery = $"post-{Guid.NewGuid():N}";
        var answered = await link.PostAsync("/api/link/v1/events",
            new StringContent(ProviderEvent("valid/event.provider.message.json", delivery,
                channel, root, "acct-1", "cloud only please"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);
        var receipt = await answered.Content.ReadJsonAsync();
        Assert.Equal($"input:{first}:auto-respond", receipt.GetProperty("data").GetProperty("judgment").GetString());

        Assert.Equal("running", Assert.Single(await fixture.Db.AssignmentRowsForWork(workId)).Status);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "input.answered"));
        var detail = Assert.Single(await fixture.Db.TimelineDetail(workId, "input.answered"));
        Assert.Contains(first, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(second, detail, StringComparison.Ordinal);
        Assert.Contains("cloud only please", detail, StringComparison.Ordinal);

        var response = await fixture.Db.OutboxEnvelopeByCommand("input:" + first);
        Assert.NotNull(response);
        Assert.Contains($"provider:mattermost:{delivery}", response, StringComparison.Ordinal);
        Assert.Equal([$"link.{bound}"], await fixture.Db.OutboxRecipientsByCommand("input:" + first));
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand("input:" + second));

        // Nothing pending for the SECOND question yet? It still is — a second
        // message answers it too (sequential law: one settle per message).
        var delivery2 = $"post-{Guid.NewGuid():N}";
        var next = await link.PostAsync("/api/link/v1/events",
            new StringContent(ProviderEvent("valid/event.provider.message.json", delivery2,
                channel, root, "acct-1", "friday"), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
        Assert.Equal($"input:{second}:auto-respond",
            (await next.Content.ReadJsonAsync()).GetProperty("data").GetProperty("judgment").GetString());

        // With everything answered, a third message falls through to plain work input.
        var delivery3 = $"post-{Guid.NewGuid():N}";
        var third = await link.PostAsync("/api/link/v1/events",
            new StringContent(ProviderEvent("valid/event.provider.message.json", delivery3,
                channel, root, "acct-1", "thanks"), Encoding.UTF8, "application/json"));
        Assert.Equal($"work/{workId}:input",
            (await third.Content.ReadJsonAsync()).GetProperty("data").GetProperty("judgment").GetString());
        Assert.Equal(2, await fixture.Db.TimelineEventCount(workId, "input.answered"));
    }

    private static string ProviderEvent(
        string fixtureName, string deliveryId, string channel, string root, string actor, string text)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture(fixtureName))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        data["text"] = text;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = channel;
        reference["rootThreadId"] = root;
        reference["conversationKind"] = "thread";
        reference["messageId"] = deliveryId;
        reference["authorProviderAccountId"] = actor;
        return doc.ToJsonString();
    }
}
