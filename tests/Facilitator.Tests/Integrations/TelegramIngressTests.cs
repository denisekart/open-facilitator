using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;



namespace Facilitator.Tests.Integrations;

/// <summary>
/// Chat-plane label routing + inbound media on the provider ingest arms
/// (opt-in <c>ChatLabelRouting</c>): a mention event whose
/// adapter-normalized <c>labels</c> match an <c>AssignRules</c> row creates
/// the Work on the rule's team and contributes a <c>coding.implement</c>
/// intent keyed <c>work-assign-coding:</c> carrying chat-domain inputs and
/// the event's <c>sourceRefs</c> as <c>inputs.sourceRefs</c>; an unlabeled
/// (or non-routing provider) mention keeps the global
/// <c>AssignCapabilityId</c>/<c>AssignEndpointId</c> pair under the plain
/// <c>work-assign:</c> command id — byte-comparable to the pre-chat-routing
/// mattermost behavior. Effects land asynchronously (shared-database
/// dispatchers race), so every assertion polls drains via
/// <see cref="RoutingHarness"/>; per-test fleets ride unique endpoint names.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class TelegramIngressTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    // Per-test unique chat id: the conversation binding is keyed
    // (provider, channel, root), so a shared chat would make the second test
    // a bound-work input, not a fresh Work creation.
    private readonly string _chat = "-100" + Random.Shared.Next(1000000000, 1999999999);
    private const string Account = "555000111";
    private const string Tenant = "api.telegram.org";
    private const string ArtifactId = "01JB0X9Z3KQ8W7M5N2P4R6S8TA";

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        // A leaked live fleet would join a later test's candidate set.
        await fixture.Db.PurgeFleets("tgc-");
        await fixture.Db.PurgeFleets("tgg-");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task LabeledMentionRoutesWorkToRuleTeamAndCarriesMediaIntoAssign()
    {
        var codingEndpoint = "tgc-" + Guid.NewGuid().ToString("N")[..6];
        var globalEndpoint = "tgg-" + Guid.NewGuid().ToString("N")[..6];
        var (link, factory) = await PlaneWithRulesAsync(globalEndpoint, codingEndpoint);
        await fixture.Db.SeedTeam("team-coding");
        // The coding fleet lives on the RULE team (the work moves there, and
        // the funnel's team scope excludes foreign-team candidates); the
        // global fleet stays on the adapter team, so it cannot be chosen.
        await FleetHarness.SeedFleetAsync(fixture.Db, codingEndpoint, "team-coding", "coding.implement");
        await FleetHarness.SeedFleetAsync(fixture.Db, globalEndpoint, "team-alpha", "research.analyze");
        await fixture.Db.SeedProviderIdentity("telegram", Tenant, Account, "subject-" + Account);
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var posted = await link.PostAsync("/api/link/v1/events", Content(MentionEvent(delivery, label: true, media: true)));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("telegram", _chat, "4242");
        Assert.NotNull(workId);

        var assign = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId.Value:N}"),
            "chat-rule coding assign effect");
        using var doc = JsonDocument.Parse(assign);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("coding.implement", data.GetProperty("capabilityId").GetString());
        var inputs = data.GetProperty("inputs");
        Assert.Equal(_chat, inputs.GetProperty("chat").GetString());
        Assert.Equal("4242", inputs.GetProperty("topic").GetString());
        var refs = inputs.GetProperty("sourceRefs");
        Assert.Equal(1, refs.GetArrayLength());
        Assert.Equal(ArtifactId, refs[0].GetProperty("artifactId").GetString());
        Assert.Equal("chat.attachment", refs[0].GetProperty("kind").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId.Value);
        Assert.Contains($"link.{codingEndpoint}", recipients);
        Assert.DoesNotContain($"link.{globalEndpoint}", recipients);

        // Media also lands on the timeline as its own auditable row.
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId.Value, "provider.attachment"));
    }

    [Fact]
    public async Task UnlabeledMentionKeepsGlobalCapabilityIntent()
    {
        var globalEndpoint = "tgg-" + Guid.NewGuid().ToString("N")[..6];
        var (link, factory) = await PlaneWithRulesAsync(globalEndpoint, codingEndpointId: "tgc-none-" + Guid.NewGuid().ToString("N")[..4]);
        await FleetHarness.SeedFleetAsync(fixture.Db, globalEndpoint, "team-alpha", "research.analyze");
        await fixture.Db.SeedProviderIdentity("telegram", Tenant, Account, "subject-" + Account);
        var delivery = $"dlv-{Guid.NewGuid():N}";

        // Same event WITHOUT the label: the global pair wins even though the
        // media refs still ride along (22/23 regression shape).
        var posted = await link.PostAsync("/api/link/v1/events", Content(MentionEvent(delivery, label: false, media: true)));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("telegram", _chat, "4242");
        Assert.NotNull(workId);

        var assign = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId.Value:N}"),
            "global mention assign effect");
        using var doc = JsonDocument.Parse(assign);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("research.analyze", data.GetProperty("capabilityId").GetString());
        var recipients = await fixture.Db.OutboxRecipients(workId.Value);
        Assert.Contains($"link.{globalEndpoint}", recipients);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{workId.Value:N}"));
        // Media rides the global path too: timeline row + assign inputs.
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId.Value, "provider.attachment"));
        using var inputs = JsonDocument.Parse(data.GetProperty("inputs").GetRawText());
        Assert.Equal(ArtifactId,
            inputs.RootElement.GetProperty("sourceRefs")[0].GetProperty("artifactId").GetString());
    }

    [Fact]
    public async Task PlainMentionMatchesTodayMattermostShape()
    {
        var globalEndpoint = "tgg-" + Guid.NewGuid().ToString("N")[..6];
        var (link, factory) = await PlaneWithRulesAsync(globalEndpoint, codingEndpointId: "tgc-none-" + Guid.NewGuid().ToString("N")[..4]);
        await FleetHarness.SeedFleetAsync(fixture.Db, globalEndpoint, "team-alpha", "research.analyze");
        await fixture.Db.SeedProviderIdentity("telegram", Tenant, Account, "subject-" + Account);
        var delivery = $"dlv-{Guid.NewGuid():N}";

        var posted = await link.PostAsync("/api/link/v1/events", Content(MentionEvent(delivery, label: false, media: false)));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        var workId = await fixture.Db.BindingWorkId("telegram", _chat, "4242");
        Assert.NotNull(workId);

        var assign = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId.Value:N}"),
            "plain mention global assign effect");
        using var doc = JsonDocument.Parse(assign);
        Assert.Equal(
            "research.analyze",
            doc.RootElement.GetProperty("data").GetProperty("capabilityId").GetString());

        // One binding, one low-noise ack reaction, no attachment rows.
        var ack = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"mention:{delivery}:ack"),
            "plain mention ack effect");
        using var ackDoc = JsonDocument.Parse(ack);
        Assert.Equal(
            "white_check_mark",
            ackDoc.RootElement.GetProperty("data").GetProperty("emojiName").GetString());
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId.Value, "provider.attachment"));
    }

    // ---- plane ------------------------------------------------------------

    private async Task<(HttpClient Link, KernelTestFactory Factory)> PlaneWithRulesAsync(
        string globalAssignEndpoint, string codingEndpointId)
    {
        var endpoint = "tga-" + Guid.NewGuid().ToString("N")[..8];
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Telegram:Enabled"] = "true",
            ["Providers:Telegram:EndpointId"] = endpoint,
            ["Providers:Telegram:Tenant"] = Tenant,
            ["Providers:Telegram:BotUserId"] = "777000",
            ["Providers:Telegram:ChatLabelRouting"] = "true",
            ["Providers:Telegram:AssignEndpointId"] = globalAssignEndpoint,
            ["Providers:Telegram:AssignCapabilityId"] = "research.analyze",
            ["Providers:Telegram:AssignRules:0:Label"] = "ofac:coding",
            ["Providers:Telegram:AssignRules:0:CapabilityId"] = "coding.implement",
            ["Providers:Telegram:AssignRules:0:EndpointId"] = codingEndpointId,
            ["Providers:Telegram:AssignRules:0:Team"] = "team-coding",
        });
        _factories.Add(factory);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint));
        return (link, factory);
    }

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    /// <summary>The telegram mention-with-media fixture under a fresh delivery
    /// identity; <c>label</c> toggles the normalized <c>labels</c> array,
    /// <c>media</c> toggles the <c>sourceRefs</c> array (both omitted, never
    /// empty, per the adapter law).</summary>
    private string MentionEvent(string deliveryId, bool label, bool media)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.mention.with-media.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["reference"]!["channelOrResourceId"] = _chat;
        if (!label)
        {
            data.Remove("labels");
        }

        if (!media)
        {
            data.Remove("sourceRefs");
        }

        return doc.ToJsonString();
    }
}
