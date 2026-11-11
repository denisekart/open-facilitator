using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// N29 self-write across two adapters: the same provider event arrives as
/// a raw webhook at the kernel sink (queued exactly once — the replay
/// dedupe law) AND as the bot's own echoed provider message on the chat
/// plane (the echo law: a ledger move, zero arm noise). With the exchange
/// cap set, the echo row IS the count on its conversation, and the next
/// human reply stops at the budget — the self-write was caught on one
/// leg, the cap counted the other.
/// Segments: WebhookSinkTests.RawDeliveryQueuesWebhookCommandOnceAndReplayDedupes
/// + ProviderIngressTests (self-write detection + shared exchange cap).
/// </summary>
[Collection("postgres:walks")]
public sealed class SelfWriteTwoAdaptersWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SameEventTwoAdaptersCaughtOnceCappedAcrossLegs()
    {
        var bot = "bot-" + WalkKit.Leaf();
        var endpoint = "gw-n29-" + WalkKit.Leaf();
        var settings = new Dictionary<string, string?>(WalkKit.MattermostPlane(endpoint, bot))
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = bot,
        };
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        var kernel = factory.CreateAuthedClient();
        var http = factory.CreateClient();
        var link = factory.LinkClient(await WalkKit.EnrollAsync(kernel, endpoint));
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        var channel = $"chan-{WalkKit.Leaf()}";
        var root = $"root-{WalkKit.Leaf()}";

        // Leg one (provider A, the raw sink): verbatim bytes queue the webhook
        // command exactly once; the provider's replay dedupes by body hash.
        var raw = """{"action":"opened","number":77001}""";
        var commandId = "webhook:forgejo:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var posted = await KernelOps.PostJsonAsync(http, "/api/ingress/webhooks/forgejo", raw,
            HttpStatusCode.Accepted);
        Assert.Equal("queued", posted.GetProperty("status").GetString());
        Assert.Equal(commandId, posted.GetProperty("commandId").GetString());
        using var replay = await http.PostAsync("/api/ingress/webhooks/forgejo",
            new StringContent(raw, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal("duplicate",
            (await replay.Content.ReadJsonAsync()).GetProperty("status").GetString());
        Assert.Equal(1, await fixture.Db.OutboxCountByCommand(commandId));
        var envelopeJson = await fixture.Db.OutboxEnvelopeByCommand(commandId);
        Assert.NotNull(envelopeJson);
        Assert.Equal("org.facilitator.command.provider.webhook.v1",
            JsonDocument.Parse(envelopeJson).RootElement.GetProperty("type").GetString());

        // The conversation forms on leg two's plane (provider B).
        await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.mention.json", "mattermost",
                $"post-{WalkKit.Leaf()}", channel, root, "acct-1", "the forge event is about this"),
            HttpStatusCode.Accepted);
        var workId = await fixture.Db.BindingWorkId(channel, root);
        Assert.NotNull(workId);

        // Leg two (provider B, the echo): the facilitator's own post, echoed
        // back by the provider — the self-write detector books the ledger
        // row and nothing else.
        var botPost = $"post-{WalkKit.Leaf()}";
        var echo = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.message.json", "mattermost",
                botPost, channel, root, bot),
            HttpStatusCode.Accepted);
        Assert.Equal("echo:post-ledger",
            echo.GetProperty("data").GetProperty("judgment").GetString());
        var (bound, parent) = await fixture.Db.BindingFor(channel, root);
        Assert.Equal(workId, bound);
        Assert.Equal(botPost, parent);
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId.Value, "provider.input"));

        // The cap counts across both legs of the loop: the echo row is the
        // facilitator's exchange, so the next human reply stops at the
        // budget — the conversation is muted, not reopened.
        await fixture.Db.SeedAdminConfig("loop-budget", """{"maxExchangesPerConversation": 1, "windowMinutes": 30}""");
        var reply = await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
            WalkKit.ProviderEvent("valid/event.provider.mention.json", "mattermost",
                $"post-{WalkKit.Leaf()}", channel, root, "acct-1", "but look at the forge side"),
            HttpStatusCode.Accepted);
        Assert.Equal("loop-budget:stop:mattermost",
            reply.GetProperty("data").GetProperty("judgment").GetString());
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId.Value, "provider.input"));
    }
}
