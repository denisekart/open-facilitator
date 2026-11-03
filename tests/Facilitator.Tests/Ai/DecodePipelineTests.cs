using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The async decode plane end-to-end: a live-llm non-slash mention queues a
/// <c>decode.kernel</c> row (channel <c>decode</c>), the sink runs the fake
/// gateway's script, and the model may only SELECT a registry verb — executed
/// through the same CommandApplier as the slash plane, ledgered basis
/// <c>model</c>. Every non-selection degrades to the verbatim-Work fallback
/// (basis <c>fallback</c>); an AI outage never DLQs a human message. Manual
/// drains only (<c>noDispatcher</c> + <see cref="IEventDispatcher.DrainOnceAsync"/>).
/// As-built note: the provider_events judgment stays <c>decode:queued</c>
/// across the sink pass (the sink records the ledger, not the arm judgment).
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class DecodePipelineTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string WorkScript = """{"intent":"command","command":"work","args":{},"reply":null,"confidence":0.93}""";

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
    public async Task DecodedCommandRunsThroughTheApplierAndLedgersModel()
    {
        // The scripted intent selects /work: the model picked a registry
        // verb, so the Work lands exactly like the slash path, but the
        // ledger says 'model'.
        var (factory, link, chat, _) = await PlaneAsync(WorkScript);
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "status of the build")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Ingest queued the decode row + a provisional REACTION ack, and the
        // arm itself created no Work.
        var queued = await fixture.Db.OutboxEnvelopeByCommand($"decode:{delivery}");
        Assert.NotNull(queued);
        Assert.Contains(DecodeSink.RequestType, queued);
        var provisional = await fixture.Db.OutboxEnvelopeByCommand($"decode:{delivery}:ack");
        Assert.NotNull(provisional);
        Assert.Contains("command.communication.react.v1", provisional);
        Assert.Null(await fixture.Db.BindingWorkId(channel, root));

        await DrainAsync(factory);

        var workId = await fixture.Db.BindingWorkId(channel, root);
        Assert.NotNull(workId);

        // Effect row on the same verb-scoped key the slash plane uses.
        Assert.NotNull(await fixture.Db.OutboxEnvelopeByCommand($"cmd:work:{delivery}"));
        Assert.Equal("delivered", (await fixture.Db.OutboxStatusAttemptsByCommand($"decode:{delivery}"))!.Value.Status);

        // Ledger 'in' row carries the parsed contract as decoded jsonb, and
        // the whole pass is basis=model (in x2 + out — the applier stamps its
        // own in-row; zero slash/fallback rows).
        var key = $"mattermost|{channel}|{root}";
        var decodedJson = await fixture.Db.AiTurnDecoded(key);
        var decoded = JsonNode.Parse(decodedJson!)!.AsObject();
        Assert.Equal("command", decoded["intent"]!.GetValue<string>());
        Assert.Equal(3, await fixture.Db.AiTurnCount(key, "model"));
        Assert.Equal(0, await fixture.Db.AiTurnCount(key, "fallback"));

        // Exactly one gateway call — the decode itself (template reply mode).
        Assert.Single(chat.Requests);
        Assert.Equal("decode", chat.Requests[0].TemplateId);
    }

    [Fact]
    public async Task ChatIntentRepliesWithoutAnyWork()
    {
        var (factory, link, chat, _) = await PlaneAsync(
            """{"intent":"chat","command":null,"args":{},"reply":"Build is green.","confidence":0.8}""");
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "how is the build going")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await DrainAsync(factory);

        // Reply-only: cmd:chat row with the composed text, zero Work, no
        // verbatim fallback ack.
        var reply = await fixture.Db.OutboxEnvelopeByCommand($"cmd:chat:{delivery}");
        Assert.NotNull(reply);
        Assert.Contains("Build is green.", reply);
        Assert.Null(await fixture.Db.BindingWorkId(channel, root));
        Assert.False(await fixture.Db.OutboxRowByCommand($"mention:{delivery}:ack"));

        // Template reply mode: the composer never consulted the gateway —
        // the ONLY chat call is the decode itself.
        Assert.Single(chat.Requests);
        Assert.Equal("decode", chat.Requests[0].TemplateId);
        Assert.Equal(2, await fixture.Db.AiTurnCount($"mattermost|{channel}|{root}", "model"));
    }

    [Fact]
    public async Task GatewayOutageRetriesThenFallsBackToVerbatimWork()
    {
        var (factory, link, chat, _) = await PlaneAsync(script: null, unavailable: true);
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "ship the release")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Drain #1: first honest try — the sink requeues, never DLQs.
        await DrainAsync(factory);
        var after = await fixture.Db.OutboxStatusAttemptsByCommand($"decode:{delivery}");
        Assert.NotNull(after);
        Assert.Equal("pending", after.Value.Status);
        Assert.Equal(1, after.Value.Attempts);
        Assert.Null(await fixture.Db.BindingWorkId(channel, root));

        // Drain #2: second charged try hits the attempt budget ⇒ degraded
        // fallback: verbatim Work + reaction ack (default notification pref).
        await fixture.Db.ExpireOutboxBackoffByCommand($"decode:{delivery}");
        await DrainAsync(factory);

        var workId = await fixture.Db.BindingWorkId(channel, root);
        Assert.NotNull(workId);
        var ack = await fixture.Db.OutboxEnvelopeByCommand($"mention:{delivery}:ack");
        Assert.NotNull(ack);
        Assert.Contains("command.communication.react.v1", ack);
        Assert.Equal("delivered", (await fixture.Db.OutboxStatusAttemptsByCommand($"decode:{delivery}"))!.Value.Status);
        var key = $"mattermost|{channel}|{root}";
        Assert.Equal(2, await fixture.Db.AiTurnCount(key, "fallback"));
        // Both drained tries honestly attempted the gateway before degrading.
        Assert.Equal(2, chat.Requests.Count);
    }

    [Fact]
    public async Task PolicyDisabledAfterQueueingFallsBackWithZeroCalls()
    {
        var (factory, link, chat, _) = await PlaneAsync(script: null);
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "quiet now please")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(await fixture.Db.OutboxEnvelopeByCommand($"decode:{delivery}"));

        // Hot policy flip: llm row disabled between queue and delivery.
        await PutPolicyAsync(factory, enabled: false);
        await DrainAsync(factory);

        // Honest degradation: verbatim Work, no gateway consult at all.
        Assert.NotNull(await fixture.Db.BindingWorkId(channel, root));
        Assert.NotNull(await fixture.Db.OutboxEnvelopeByCommand($"mention:{delivery}:ack"));
        Assert.Empty(chat.Requests);
        Assert.Equal("delivered", (await fixture.Db.OutboxStatusAttemptsByCommand($"decode:{delivery}"))!.Value.Status);
    }

    [Fact]
    public async Task CrashReplayOfSettledDecodeCreatesNoSecondEffect()
    {
        var (factory, link, chat, _) = await PlaneAsync(WorkScript);
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "open a ticket please")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await DrainAsync(factory);
        var workId = await fixture.Db.BindingWorkId(channel, root);
        Assert.NotNull(workId);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId.Value, "created"));
        Assert.Equal(1, await fixture.Db.OutboxCountByCommand($"cmd:work:{delivery}"));
        var ledgerBefore = await fixture.Db.AiTurnCountTotal($"mattermost|{channel}|{root}");

        // Crash simulation: the pass committed but the decode row never
        // settled — replay it with the same script answer available again.
        chat.Enqueue("decode", WorkScript);
        await fixture.Db.ReplayOutboxRowByCommand($"decode:{delivery}");
        await DrainAsync(factory);

        // The reply-row replay guard held: one Work, one created event, one
        // effect row, no new ledger rows — the re-run answered "already done".
        Assert.Equal(workId, await fixture.Db.BindingWorkId(channel, root));
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId.Value, "created"));
        Assert.Equal(1, await fixture.Db.OutboxCountByCommand($"cmd:work:{delivery}"));
        Assert.Equal(ledgerBefore, await fixture.Db.AiTurnCountTotal($"mattermost|{channel}|{root}"));
        Assert.Equal("delivered", (await fixture.Db.OutboxStatusAttemptsByCommand($"decode:{delivery}"))!.Value.Status);
    }

    // ---- plumbing ----------------------------------------------------------

    private async Task<(KernelTestFactory Factory, HttpClient Link, FakeAiGateway Chat, AiFixtures Ai)> PlaneAsync(
        string? script, bool unavailable = false, string replyMode = "template")
    {
        // Claim-window hygiene: the dispatcher claims globally (oldest-first,
        // batch 20) and this collection's other classes leave undispatched
        // pending rows behind, so a fresh decode row can starve out of the
        // batch — or a foreign leftover row can be delivered against this
        // host's fake gateway. Settle the window before the plane goes live.
        await fixture.Db.SettlePendingOutboxRows();
        var endpoint = "gw-dec-" + Guid.NewGuid().ToString("N")[..8];

        var ai = new AiFixtures();
        ai.Chat.Unavailable = unavailable;
        if (script is not null)
        {
            ai.Chat.Enqueue("decode", script);
        }

        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = endpoint,
            ["Providers:Mattermost:Tenant"] = "demo-team",
            ["Providers:Mattermost:SiteUrl"] = "http://mattermost:8065",
            ["Ai:Endpoints:decoder:BaseUrl"] = "http://litellm.test:4000",
            ["Ai:Endpoints:decoder:Model"] = "llm",
            ["Ai:Endpoints:decoder:ApiKey"] = "k",
        }, aiFixtures: ai);
        _factories.Add(factory);
        await PutPolicyAsync(factory, enabled: true, replyMode: replyMode);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint));
        return (factory, link, ai.Chat, ai);
    }

    private static async Task PutPolicyAsync(KernelTestFactory host, bool enabled, string replyMode = "template")
    {
        using var client = host.CreateAuthedClient();
        client.DefaultRequestHeaders.Add("X-Change-Reason", "decode pipeline test");
        client.DefaultRequestHeaders.IfMatch.Add(EntityTagHeaderValue.Any);
        var response = await client.PatchAsync(
            "/api/admin/config/llm",
            new StringContent(
                $$"""{"enabled":{{(enabled ? "true" : "false")}},"verbosityDefault":"balanced","replyMode":"{{replyMode}}","decisionMode":"auto","maxOutputTokens":600}""",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task DrainAsync(KernelTestFactory factory) =>
        await factory.Services.GetRequiredService<IEventDispatcher>().DrainOnceAsync(CancellationToken.None);

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
