using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;



namespace Facilitator.Tests.Integrations;

/// <summary>
/// Plan 22 provider-ingest plane over the link HTTP path: fail-closed
/// <c>unsupported</c> when disabled (failed audit row); linked-mention Work
/// creation + binding (same post twice ⇒ one Work, duplicate returns the saved
/// judgment); unlinked mention ⇒ reply-only, no Work; <c>/link</c> ⇒ challenge
/// + bot-DM reply and redemption (200/409/404/410) landing the identity row +
/// confirmation; reaction ⇒ timeline note + ack, never an approval transition;
/// echo ⇒ binding parent ledger only; terminal transition ⇒ exactly one
/// <c>communication.reply</c> row addressed to the adapter's link endpoint.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ProviderIngressTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string ProblemBase = "https://errors.openfacilitator.org/v1/";

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync() =>
        // A leaked live fleet would join a later test's candidate set.
        await fixture.Db.PurgeFleets("gw-assign-");

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task ProviderEventRefusedWhenFeatureDisabled()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), "gw-disabled"));

        var delivery = "post-" + Guid.NewGuid().ToString("N");
        var envelope = ProviderEvent("valid/event.provider.mention.json", delivery, "chan-x", "root-x", "acct-x", text: "hello");
        using var response = await link.PostAsync("/api/link/v1/events",
            new StringContent(envelope, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadJsonAsync();
        Assert.Equal(ProblemBase + "unsupported", problem.GetProperty("type").GetString());
        Assert.True(await fixture.Db.InboxFailedFor(JsonDocument.Parse(envelope).RootElement.GetProperty("id").GetString()!),
            "refusal persists the failed audit inbox row");
        Assert.Equal(0, await fixture.Db.ProviderEventCount("mattermost", delivery));
    }

    [Fact]
    public async Task LinkedMentionCreatesWorkOnceAndDuplicateReturnsJudgment()
    {
        var (factory, link, tenant, _) = await EnabledPlane();
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";
        var text = "summarize the retention alternatives";

        var first = await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", delivery, channel, root, "acct-1", text: text)));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var receipt = await first.Content.ReadJsonAsync();
        Assert.Equal("accepted", receipt.GetProperty("status").GetString());

        var workId = await BoundWork(channel, root);
        var work = await KernelOps.GetJsonAsync(factory.CreateAuthedClient(), $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal("open", work.GetProperty("state").GetString());
        Assert.Equal(1, await fixture.Db.ProviderEventCount("mattermost", delivery));
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "created"));
        Assert.Equal($"work/{workId}", await fixture.Db.ProviderEventJudgment(delivery));

        // ack (default notifications preference = reaction): react command to the adapter.
        var ack = await fixture.Db.OutboxEnvelopeByCommand($"mention:{delivery}:ack");
        Assert.NotNull(ack);
        Assert.Contains("command.communication.react.v1", ack);
        Assert.Contains("white_check_mark", ack);

        // Redelivery: duplicate, saved judgment, zero new arm effects.
        var second = await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", delivery, channel, root, "acct-1", text: text)));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var dup = await second.Content.ReadJsonAsync();
        Assert.Equal("duplicate", dup.GetProperty("status").GetString());
        Assert.Equal($"work/{workId}", dup.GetProperty("data").GetProperty("judgment").GetString());
        Assert.Equal(1, await fixture.Db.ProviderEventCount("mattermost", delivery));
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "created"));
    }

    [Fact]
    public async Task UnlinkedMentionRepliesWithoutWork()
    {
        var (_, link, _, _) = await EnabledPlane();
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", delivery, channel, root, "acct-unlinked", text: "do the thing")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var reply = await fixture.Db.OutboxEnvelopeByCommand($"mention:{delivery}:unlinked");
        Assert.NotNull(reply);
        Assert.Contains("/link", reply);
        Assert.Null(await fixture.Db.BindingWorkId(channel, root));
        Assert.Equal(1, await fixture.Db.ProviderEventCount("mattermost", delivery));
    }

    [Fact]
    public async Task LinkMintChallengeRedeemFlowWithFences()
    {
        var (factory, link, tenant, _) = await EnabledPlane();
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var account = $"acct-{Guid.NewGuid():N}";
        var delivery = "post-" + Guid.NewGuid().ToString("N");

        using var minted = await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", delivery, channel, root, account, text: "/link")));
        Assert.Equal(HttpStatusCode.Accepted, minted.StatusCode);
        Assert.Equal(1, await fixture.Db.ProviderChallengeCount(account));
        Assert.Null(await fixture.Db.BindingWorkId(channel, root));

        // The challenge code is human-surfaced in the bot-DM reply, not stored raw.
        var dm = await fixture.Db.OutboxEnvelopeByCommand($"link:{delivery}:dm");
        Assert.NotNull(dm);
        var code = Regex.Match(dm, @"link code: ([A-Z2-9]{6})").Groups[1].Value;
        Assert.Equal(6, code.Length);

        var human = factory.CreateAuthedClient();
        var redeemed = await KernelOps.PostJsonAsync(human, "/api/link-identities/redeem",
            JsonSerializer.Serialize(new { code }), HttpStatusCode.OK);
        Assert.Equal("mattermost", redeemed.GetProperty("provider").GetString());
        Assert.True(await fixture.Db.ProviderIdentityExists("mattermost", tenant, account));
        var challengeId = await fixture.Db.ProviderChallengeId(account);
        Assert.NotNull(await fixture.Db.OutboxEnvelopeByCommand($"link:{challengeId}:confirm"));

        // Single-use fence + unknown + expired.
        await KernelOps.PostJsonAsync(human, "/api/link-identities/redeem",
            JsonSerializer.Serialize(new { code }), HttpStatusCode.Conflict);
        await KernelOps.PostJsonAsync(human, "/api/link-identities/redeem",
            JsonSerializer.Serialize(new { code = "ZZZZZZ" }), HttpStatusCode.NotFound);

        var expiredDelivery = "post-" + Guid.NewGuid().ToString("N");
        await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", expiredDelivery, channel, root, account, text: "/link")));
        await fixture.Db.SetChallengeExpiry(account, expired: true);
        var expiredCode = Regex.Match((await fixture.Db.OutboxEnvelopeByCommand($"link:{expiredDelivery}:dm"))!,
            @"link code: ([A-Z2-9]{6})").Groups[1].Value;
        await KernelOps.PostJsonAsync(human, "/api/link-identities/redeem",
            JsonSerializer.Serialize(new { code = expiredCode }), HttpStatusCode.Gone);
    }

    [Fact]
    public async Task DmSlashLinkMintsChallengeWithoutMention()
    {
        // §11 delivers linking to UNLINKED accounts via DM, and a DM post
        // normalizes message.v1 (never mention.v1) — the mint must ride the
        // message arm or a fresh account can never start linking.
        var (factory, link, _, _) = await EnabledPlane();
        var dmChannel = $"dm-{Guid.NewGuid():N}";
        var account = $"acct-{Guid.NewGuid():N}";
        var delivery = "post-" + Guid.NewGuid().ToString("N");

        using var minted = await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.message.json", delivery, dmChannel, dmChannel, account, text: "/link")));
        Assert.Equal(HttpStatusCode.Accepted, minted.StatusCode);
        Assert.Equal(1, await fixture.Db.ProviderChallengeCount(account));
        var dm = await fixture.Db.OutboxEnvelopeByCommand($"link:{delivery}:dm");
        Assert.NotNull(dm);
        Assert.Matches(@"link code: [A-Z2-9]{6}", dm);
        Assert.Null(await fixture.Db.BindingWorkId(dmChannel, dmChannel));
    }

    [Fact]
    public async Task ReactionNotesAuditsAndNeverTransitions()
    {
        var (factory, link, tenant, _) = await EnabledPlane();
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", $"post-{Guid.NewGuid():N}", channel, root, "acct-1", text: "bind this thread")));
        var workId = await BoundWork(channel, root);

        var reaction = "post-" + Guid.NewGuid().ToString("N");
        using var response = await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.reaction.json", reaction, channel, root, "acct-1", emoji: "thumbsup")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "provider.reaction"));
        var after = await KernelOps.GetJsonAsync(factory.CreateAuthedClient(), $"/api/works/{workId}", HttpStatusCode.OK);
        Assert.Equal("open", after.GetProperty("state").GetString());
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "created"));
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId, "state.transition"));
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId, "approval.decided"));
        var ack = await fixture.Db.OutboxEnvelopeByCommand($"reaction:{reaction}:ack");
        Assert.NotNull(ack);
        Assert.Contains("eye", ack);
    }

    [Fact]
    public async Task BotEchoUpdatesBindingLedgerOnly()
    {
        var bot = "bot-" + Guid.NewGuid().ToString("N");
        var (_, link, tenant, _) = await EnabledPlane(botUserId: bot);
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", $"post-{Guid.NewGuid():N}", channel, root, "acct-1", text: "bind me")));
        var workId = await BoundWork(channel, root);

        var botPost = "post-" + Guid.NewGuid().ToString("N");
        using var response = await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.message.json", botPost, channel, root, bot)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await response.Content.ReadJsonAsync();
        Assert.Equal("echo:post-ledger", receipt.GetProperty("data").GetProperty("judgment").GetString());

        // Outbound-post ledger: the binding's parent moves to the bot's post id.
        var (bound, parent) = await fixture.Db.BindingFor(channel, root);
        Assert.Equal(workId, bound);
        Assert.Equal(botPost, parent);

        // No arm effects for the echo: no input note, no state noise.
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId, "provider.input"));
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "created"));
    }

    [Fact]
    public async Task TerminalTransitionAppendsOneReplyToBinding()
    {
        var (factory, link, tenant, endpoint) = await EnabledPlane();
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", $"post-{Guid.NewGuid():N}", channel, root, "acct-1", text: "wind down")));
        var workId = await BoundWork(channel, root);

        await KernelOps.TransitionAsync(factory.CreateAuthedClient(), workId, "planning", 1);
        await KernelOps.TransitionAsync(factory.CreateAuthedClient(), workId, "cancelled", 2);

        var envelopes = await fixture.Db.OutboxEnvelopes(workId);
        var replies = envelopes.Where(e => e.Contains("command.communication.reply.v1", StringComparison.Ordinal)).ToList();
        var single = Assert.Single(replies);
        Assert.Contains(channel, single);
        Assert.Contains(root, single);
        Assert.Contains($"link.{endpoint}", await fixture.Db.OutboxRecipients(workId));
    }

    [Fact]
    public async Task AssignInterimRidesConfiguredEndpoint()
    {
        var assignEndpoint = "gw-assign-" + Guid.NewGuid().ToString("N")[..6];
        var (factory, link, tenant, _) = await EnabledPlane(assignEndpointId: assignEndpoint);
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        await link.PostAsync("/api/link/v1/events", Content(ProviderEvent(
            "valid/event.provider.mention.json", $"post-{Guid.NewGuid():N}", channel, root, "acct-1", text: "routed work")));
        var workId = await BoundWork(channel, root);

        // The interim assign rides the routing funnel (35's seam): the lane
        // fleet is live, so drain until the preference-satisfied effect row
        // lands under the N-format key, recipient link.<assign endpoint>.
        await FleetHarness.SeedFleetAsync(fixture.Db, assignEndpoint, "team-alpha", "analysis.summarize");
        var envelope = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign:{workId:N}"),
            "interim assign effect");
        Assert.Contains("command.work.assign.v1", envelope);
        Assert.Contains($"link.{assignEndpoint}", await fixture.Db.OutboxRecipients(workId));
    }

    // ---- helpers ------------------------------------------------------------

    private async Task<Guid> BoundWork(string channel, string root)
    {
        var workId = await fixture.Db.BindingWorkId(channel, root);
        Assert.NotNull(workId);
        return workId.Value;
    }

    private async Task<(KernelTestFactory Factory, HttpClient Link, string Tenant, string Endpoint)> EnabledPlane(
        string? botUserId = null, string? assignEndpointId = null)
    {
        var endpoint = "gw-prov-" + Guid.NewGuid().ToString("N")[..8];
        var tenant = "demo-team";
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = endpoint,
            ["Providers:Mattermost:Tenant"] = tenant,
            ["Providers:Mattermost:SiteUrl"] = "http://mattermost:8065",
            ["Providers:Mattermost:LinkHintText"] = "Your Mattermost account is not linked to a Facilitator identity — send me a DM starting with /link to link it.",
        };
        if (botUserId is not null)
        {
            settings["Providers:Mattermost:BotUserId"] = botUserId;
        }

        if (assignEndpointId is not null)
        {
            settings["Providers:Mattermost:AssignEndpointId"] = assignEndpointId;
        }

        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        _factories.Add(factory);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint));
        return (factory, link, tenant, endpoint);
    }

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    private static string ProviderEvent(
        string fixtureName, string deliveryId, string channel, string root, string actor, string? text = null, string? emoji = null)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture(fixtureName))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = channel;
        reference["rootThreadId"] = root;
        reference["messageId"] = deliveryId;
        reference["authorProviderAccountId"] = actor;
        if (text is not null)
        {
            data["text"] = text;
        }

        if (emoji is not null)
        {
            data["emojiName"] = emoji;
        }

        return doc.ToJsonString();
    }
}
