using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// Reply composition law: verbosity is a per-actor preference
/// (<c>actor_preferences</c> > <c>llm.verbosityDefault</c>) enforced through
/// the <c>reply-composer</c> prompt; template mode never consults a model.
/// The react/silent suppression lives at the completion hook: a live policy
/// consults <c>CommunicationDecision</c> (decisions API, question
/// <c>action</c>) before the deterministic reply row is written.
/// As-built note: the verbosity vocabulary is terse|balanced|proactive
/// (no "verbose"), and replyMode is model|template (no "decide" mode) — the
/// react/silent choice is the decision point's, not a replyMode value.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ReplyPreferenceTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
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

    [Theory]
    [InlineData("terse")]
    [InlineData("balanced")]
    [InlineData("proactive")]
    public async Task ActorVerbosityDrivesTheComposerPrompt(string verbosity)
    {
        var (factory, chat) = Plane();
        await fixture.Db.SeedActorPreference($"oidc:pref-test|{verbosity}-guy", verbosity);
        var composer = factory.Services.GetRequiredService<ReplyComposer>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        chat.Enqueue("reply-composer", $"Reply under {verbosity} law.");

        var text = await composer.ComposeAsync(
            db, ModelPolicy, $"oidc:pref-test|{verbosity}-guy", "Work reached cancelled.\ndetail line one", ct: CancellationToken.None);

        // The scripted text comes back clamped, and the prompt carries the
        // actor's setting — the verbosity law is enforced prompt-side.
        Assert.Equal($"Reply under {verbosity} law.", text);
        var request = chat.Requests.Single();
        Assert.Equal("reply-composer", request.TemplateId);
        Assert.Contains($"the actor's setting is: {verbosity}", request.SystemPrompt + "\n" + request.UserPrompt);
    }

    [Fact]
    public async Task UnmappedActorFallsToPolicyDefault()
    {
        var (factory, chat) = Plane();
        var composer = factory.Services.GetRequiredService<ReplyComposer>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        chat.Enqueue("reply-composer", "ok");

        await composer.ComposeAsync(db, ModelPolicy, "oidc:pref-test|nobody-here", "Work reached cancelled.", ct: CancellationToken.None);

        // No preference row ⇒ the policy's verbosityDefault governs.
        var prompt = chat.Requests.Single();
        Assert.Contains("the actor's setting is: balanced", prompt.SystemPrompt + "\n" + prompt.UserPrompt);
    }

    [Fact]
    public async Task TemplateModeIsPureKernelTextWithZeroCalls()
    {
        var (factory, chat) = Plane();
        var composer = factory.Services.GetRequiredService<ReplyComposer>();
        var contextFactory = factory.Services.GetRequiredService<IDbContextFactory<FacilitatorDbContext>>();
        await using var db = await contextFactory.CreateDbContextAsync();
        var before = chat.Requests.Count;

        var text = await composer.ComposeAsync(
            db, TemplatePolicy, null, "Work reached cancelled.\ndetail line one", ct: CancellationToken.None);

        // Deterministic: the outcome summary stands verbatim, no model spoke.
        Assert.Equal("Work reached cancelled.", text);
        Assert.Equal(before, chat.Requests.Count);
    }

    [Fact]
    public async Task CompletionHookHonoursReactSilentReplyAnswers()
    {
        // Live policy + configured decision endpoint: the completion hook
        // consults the decisions API once per terminal transition, and the
        // answer chooses the envelope variant (react), suppresses it
        // (silent), or stands aside for the deterministic reply (refusal).
        var (factory, link, chat, ai) = PlaneCore(aiDecisionEndpoint: true);
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        await PutPolicyAsync(factory, """{"enabled":true,"verbosityDefault":"balanced","replyMode":"template","decisionEndpoint":"decision","decisionMode":"auto","maxOutputTokens":600}""");
        using var client = factory.CreateAuthedClient();

        var reactWork = await BoundWork(link, "/work wind it down");
        var silentWork = await BoundWork(link, "/work hush now");
        var standAsideWork = await BoundWork(link, "/work say something");
        ai.Decisions.EnqueueDecision("action", new DecisionAnswer("action", null, "react", 0.9));
        await KernelOps.TransitionAsync(client, reactWork, "cancelled", 1);
        ai.Decisions.EnqueueDecision("action", new DecisionAnswer("action", null, "silent", 0.9));
        await KernelOps.TransitionAsync(client, silentWork, "cancelled", 1);
        await KernelOps.TransitionAsync(client, standAsideWork, "cancelled", 1);

        // Three terminal transitions, three honest asks of the decision point.
        Assert.Equal(3, ai.Decisions.DecisionRequests.Count);
        Assert.All(ai.Decisions.DecisionRequests, request => Assert.Equal("action", Assert.Single(request.Questions).Name));
        Assert.Empty(chat.Requests);

        // "react" ⇒ the reaction envelope variant on the deterministic key.
        var react = Assert.Single(await fixture.Db.OutboxEnvelopes(reactWork), e => e.Contains("communication-reply"));
        Assert.Contains("command.communication.react.v1", react);
        Assert.Contains("eye", react);

        // "silent" ⇒ zero communication rows at all.
        Assert.DoesNotContain(await fixture.Db.OutboxEnvelopes(silentWork), e => e.Contains("communication-reply"));

        // Refusal (unmatched canned answer) ⇒ today's deterministic reply verbatim.
        var standAside = Assert.Single(await fixture.Db.OutboxEnvelopes(standAsideWork), e => e.Contains("communication-reply"));
        Assert.Contains("command.communication.reply.v1", standAside);
        Assert.Contains("Work reached cancelled.", standAside);
    }

    [Fact]
    public async Task ModelReplyModeComposesThroughTheGateway()
    {
        var (factory, link, chat, _) = PlaneCore(endpoints: true);
        await fixture.Db.SeedProviderIdentity("mattermost", "demo-team", "acct-1", "subject-1");
        await PutPolicyAsync(factory, """{"enabled":true,"verbosityDefault":"balanced","replyMode":"model","decisionMode":"auto","maxOutputTokens":600}""");
        chat.Enqueue("decode", """{"intent":"chat","command":null,"args":{},"reply":"Build is green.","confidence":0.8}""");
        chat.Enqueue("reply-composer", "All green, merged and deployed.");
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";

        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", "how is the build going")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await factory.Services.GetRequiredService<IEventDispatcher>().DrainOnceAsync(CancellationToken.None);

        // The reply on the wire is the composed model text, not the decode's
        // clarification field — and the composer really did call (2 calls).
        var reply = await fixture.Db.OutboxEnvelopeByCommand($"cmd:chat:{delivery}");
        Assert.NotNull(reply);
        Assert.Contains("All green, merged and deployed.", reply);
        Assert.Equal(2, chat.Requests.Count);
        Assert.Equal("reply-composer", chat.Requests[1].TemplateId);
    }

    // ---- plumbing ----------------------------------------------------------

    private static AiPolicy ModelPolicy => new(true, "balanced", "model", null, "auto", 600);

    private static AiPolicy TemplatePolicy => new(true, "balanced", "template", null, "auto", 600);

    private (KernelTestFactory Factory, FakeAiGateway Chat) Plane()
    {
        var (factory, _, chat, _) = PlaneCore(false);
        return (factory, chat);
    }

    private (KernelTestFactory Factory, HttpClient Link, FakeAiGateway Chat, AiFixtures Ai) PlaneCore(
        bool aiDecisionEndpoint = false, bool endpoints = false)
    {
        // Claim-window hygiene: the outbox claim is global and the sibling
        // classes in this collection leave undispatched pending rows behind;
        // settle the window so a drain only ever sees this test's rows.
        fixture.Db.SettlePendingOutboxRows().GetAwaiter().GetResult();
        var endpoint = "gw-rep-" + Guid.NewGuid().ToString("N")[..8];
        var ai = new AiFixtures();
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Mattermost:Enabled"] = "true",
            ["Providers:Mattermost:EndpointId"] = endpoint,
            ["Providers:Mattermost:Tenant"] = "demo-team",
            ["Providers:Mattermost:SiteUrl"] = "http://mattermost:8065",
        };
        if (endpoints || aiDecisionEndpoint)
        {
            settings["Ai:Endpoints:decoder:BaseUrl"] = "http://litellm.test:4000";
            settings["Ai:Endpoints:decoder:Model"] = "llm";
            settings["Ai:Endpoints:decoder:ApiKey"] = "k";
        }

        if (aiDecisionEndpoint)
        {
            settings["Ai:Endpoints:decision:BaseUrl"] = "http://litellm.test:4000";
            settings["Ai:Endpoints:decision:Model"] = "jev";
            settings["Ai:Endpoints:decision:ApiKey"] = "k";
        }

        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings, aiFixtures: ai);
        _factories.Add(factory);
        var link = factory.CreateLinkClient(LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint).GetAwaiter().GetResult());
        return (factory, link, ai.Chat, ai);
    }

    private static async Task PutPolicyAsync(KernelTestFactory host, string payload)
    {
        using var client = host.CreateAuthedClient();
        client.DefaultRequestHeaders.Add("X-Change-Reason", "reply preference test");
        client.DefaultRequestHeaders.IfMatch.Add(EntityTagHeaderValue.Any);
        var response = await client.PatchAsync(
            "/api/admin/config/llm", new StringContent(payload, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task<Guid> BoundWork(HttpClient link, string text)
    {
        var channel = $"chan-{Guid.NewGuid():N}";
        var root = $"root-{Guid.NewGuid():N}";
        var delivery = $"post-{Guid.NewGuid():N}";
        using var response = await link.PostAsync("/api/link/v1/events", Content(Mention(delivery, channel, root, "acct-1", text)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var workId = await fixture.Db.BindingWorkId(channel, root);
        Assert.NotNull(workId);
        return workId.Value;
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
