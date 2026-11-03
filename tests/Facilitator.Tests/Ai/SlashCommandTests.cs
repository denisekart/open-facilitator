using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The deterministic slash plane through the REAL ingest path (POST
/// /api/link/v1/events, a mention.v1 conformance envelope — ProviderIngressTests
/// is the template). Zero AI: every reply here is kernel text, ledgered with
/// basis <c>slash</c> (or <c>fallback</c> on the inert verbatim path). The
/// judgment asserted is the one stored on the provider_events ledger (the
/// <c>verified_reference</c> column — the arm decision the delivery settled
/// with). As-built vocabulary note: there is no flat <c>cmd:executed</c> /
/// <c>cmd:denied</c> set — judgments are per-verb (<c>cmd:status:nobound</c>,
/// <c>cmd:unknown</c>, <c>work/&lt;id&gt;</c>, <c>mention:unlinked</c>), the
/// identity gate runs before the parser, and CommandSpec carries no per-verb
/// role gate in this increment, so an admin-only verb outside the registry
/// degrades to the deterministic unknown-command answer.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class SlashCommandTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await fixture.Db.AdminConfigPurge("llm");
        await fixture.Db.AdminConfigPurge("commands");
        await fixture.Db.AdminConfigPurge("notifications");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task StatusCommandRepliesDeterministicallyAndLedgersSlash()
    {
        var (factory, link, tenant) = Plane();
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "/status")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Reply row on the verb-scoped idempotency key; the conversation has
        // no bound Work, so the stored judgment is cmd:status:nobound.
        var reply = await fixture.Db.OutboxEnvelopeByCommand($"cmd:status:{delivery}");
        Assert.NotNull(reply);
        Assert.Contains("command.communication.reply.v1", reply);
        Assert.Contains("No Work is bound", reply);
        Assert.Equal("cmd:status:nobound", await fixture.Db.ProviderEventJudgment(delivery));

        // Ledger: both directions, basis slash — the AI ledger of every slash touch.
        var key = $"mattermost|{channel}|{root}";

        // No decode row — the slash plane never reaches the AI.
        Assert.False(await fixture.Db.OutboxRowByCommand($"decode:{delivery}"));
    }

    [Fact]
    public async Task StatusFromUnlinkedAccountIsGatedBeforeTheSlashPlane()
    {
        var (_, link, _) = Plane();
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-unknown", "/status")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Identity gate first (UNCHANGED by the slash plane): an unlinked
        // account never reaches the parser. No LinkHintText configured on this
        // plane ⇒ audit-only, no reply row.
        Assert.Equal("mention:unlinked", await fixture.Db.ProviderEventJudgment(delivery));
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand($"cmd:status:{delivery}"));
        Assert.False(await fixture.Db.OutboxRowByCommand($"mention:{delivery}:unlinked"));
        Assert.Equal(0, await fixture.Db.AiTurnCountTotal($"mattermost|{channel}|{root}"));
    }

    [Fact]
    public async Task AdminOnlyVerbWithoutRoleGateDegradesToUnknown()
    {
        var (_, link, tenant) = Plane();
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "/deploy")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // As-built: CommandSpec carries no roles field, so "/deploy" is simply
        // outside the registry — the deterministic unknown-command answer, no
        // effect (cmd:denied exists only for permission-gated built-ins on
        // linked actors without the proven grant).
        Assert.Equal("cmd:unknown", await fixture.Db.ProviderEventJudgment(delivery));
        var reply = await fixture.Db.OutboxEnvelopeByCommand($"cmd:deploy:{delivery}");
        Assert.NotNull(reply);
        Assert.Contains("Unknown command /deploy", reply);
        Assert.Null(await fixture.Db.BindingWorkId(channel, root));
    }

    [Fact]
    public async Task UnknownVerbAnswersWithHelpPointer()
    {
        var (_, link, tenant) = Plane();
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "/frobnicate x")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal("cmd:unknown", await fixture.Db.ProviderEventJudgment(delivery));
        var reply = await fixture.Db.OutboxEnvelopeByCommand($"cmd:frobnicate:{delivery}");
        Assert.NotNull(reply);
        Assert.Contains("/help", reply);
        Assert.Null(await fixture.Db.BindingWorkId(channel, root));
    }

    [Fact]
    public async Task CustomVerbFromCommandsRowCreatesWorkWithCapabilitySeed()
    {
        var (factory, link, tenant) = Plane();
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        await PutConfigAsync(factory, "commands",
            """{"commands":[{"verb":"watch","description":"Track builds","capabilityId":"ci.run","args":[{"name":"repo","required":false}]}]}""");
        try
        {
            var channel = $"chan-{Guid.NewGuid():N}";
            var root = $"root-{Guid.NewGuid():N}";
            var delivery = $"post-{Guid.NewGuid():N}";

            using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "/watch org/repo")));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            var workId = await fixture.Db.BindingWorkId(channel, root);
            Assert.NotNull(workId);
            Assert.Equal($"work/{workId}", await fixture.Db.ProviderEventJudgment(delivery));

            // The registry entry's capabilityId seeds the routing intent row.
            var intent = await fixture.Db.OutboxEnvelopeByCommand($"routing:work-assign:{workId:N}");
            Assert.NotNull(intent);
            Assert.Contains("ci.run", intent);

            // Ack (default notifications preference = reaction) on the verb key.
            var ack = await fixture.Db.OutboxEnvelopeByCommand($"cmd:watch:{delivery}");
            Assert.NotNull(ack);
            Assert.Contains("command.communication.react.v1", ack);

            // Ledger basis=slash: the slash plane ran with zero AI.
            var key = $"mattermost|{channel}|{root}";
            Assert.Equal(2, await fixture.Db.AiTurnCount(key, "slash"));
            Assert.Equal(0, await fixture.Db.AiTurnCount(key, "model"));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge("commands");
        }
    }

    [Fact]
    public async Task InertPolicyKeepsVerbatimWorkWithSlashPlaneHint()
    {
        var (factory, link, tenant) = Plane();
        await fixture.Db.SeedProviderIdentity("mattermost", tenant, "acct-1", "subject-1");
        // Reply-mode acks so the degraded hint line is observable on the wire.
        await PutConfigAsync(factory, "notifications", """{"replyVsReaction":"reply","progressVerbosity":"normal"}""");
        try
        {
            var channel = $"chan-{Guid.NewGuid():N}";
            var root = $"root-{Guid.NewGuid():N}";
            var delivery = $"post-{Guid.NewGuid():N}";

            using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "status")));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

            // No llm row at all ⇒ AiDeck inert: the exact verbatim Work of
            // today, objective = the raw text; no decode row was queued.
            var workId = await fixture.Db.BindingWorkId(channel, root);
            Assert.NotNull(workId);
            Assert.Equal($"work/{workId}", await fixture.Db.ProviderEventJudgment(delivery));
            var created = Assert.Single(await fixture.Db.TimelineDetail(workId.Value, "created"));
            Assert.Contains("status", created);
            Assert.False(await fixture.Db.OutboxRowByCommand($"decode:{delivery}"));

            // Ack reply carries the no-AI hint line.
            var ack = await fixture.Db.OutboxEnvelopeByCommand($"mention:{delivery}:ack");
            Assert.NotNull(ack);
            Assert.Contains("command.communication.reply.v1", ack);
            Assert.Contains("/help", ack);

            Assert.Equal(2, await fixture.Db.AiTurnCount($"mattermost|{channel}|{root}", "fallback"));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge("notifications");
        }
    }

    // ---- plumbing ----------------------------------------------------------

    private (KernelTestFactory Factory, HttpClient Link, string Tenant) Plane()
    {
        var endpoint = "gw-slash-" + Guid.NewGuid().ToString("N")[..8];
        var tenant = "demo-team";
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = endpoint,
            ["Providers:Mattermost:Tenant"] = tenant,
            ["Providers:Mattermost:SiteUrl"] = "http://mattermost:8065",
        });
        _factories.Add(factory);
        var link = factory.CreateLinkClient(LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint).GetAwaiter().GetResult());
        return (factory, link, tenant);
    }

    private static async Task PutConfigAsync(KernelTestFactory host, string resource, string payload)
    {
        using var client = host.CreateAuthedClient();
        client.DefaultRequestHeaders.Add("X-Change-Reason", "slash-plane test");
        client.DefaultRequestHeaders.IfMatch.Add(EntityTagHeaderValue.Any);
        var response = await client.PatchAsync(
            $"/api/admin/config/{resource}",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    private static string Mention(string deliveryId, string channel, string root, string actor, string text)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.mention.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = channel;
        reference["rootThreadId"] = root;
        reference["messageId"] = deliveryId;
        reference["authorProviderAccountId"] = actor;
        data["text"] = text;
        return doc.ToJsonString();
    }
}
