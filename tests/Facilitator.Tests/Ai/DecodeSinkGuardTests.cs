using System.Text.Json;

using Facilitator.Kernel;
using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The decode sink's guard and routing paths, driven straight at
/// <see cref="DecodeSink.DeliverAsync"/> with a hand-built <see cref="OutboxRow"/>
/// (the <c>DecodePipelineTests</c> plane: host-provided sink, paired fakes, manual
/// delivery only). Covers the poison-row guards (a non-contract row, a null member,
/// a junk team id and an unknown provider are kernel bugs, never AI outages), the
/// inert-policy degradation, the gateway-outage law (requeue, then the verbatim
/// fallback at <see cref="DecodeSink.FallbackAfterAttempts"/>), the junk-answer,
/// unknown-intent and below-the-confidence-floor fallbacks, the chat-reply path and
/// its composer outage, both crash-replay guards, and the strict contract parser
/// through the answers it accepts versus the ones it throws away.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class DecodeSinkGuardTests : IAsyncLifetime, IDisposable
{
    private const string WorkIntent = """{"intent":"command","command":"work","args":{},"reply":null,"confidence":0.93}""";

    private readonly DispatchDbFixture _fixture;

    private readonly List<KernelTestFactory> _factories = [];

    public DecodeSinkGuardTests(DispatchDbFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    // ---- poison rows ---------------------------------------------------------

    [Fact]
    public async Task ARowThatIsNotTheContractAtAllThrowsInsteadOfDegrading()
    {
        var plane = await PlaneAsync();

        // No `data` member: this is not a decode row, and the sink cannot even
        // name the conversation. The dispatcher's exception path retries it and
        // then dead-letters it — a kernel bug, never dressed up as an AI outage.
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => plane.DeliverAsync(plane.Row("""{"envelope":"not-a-decode-row"}""")));
        Assert.Empty(plane.Ai.Chat.Requests);
    }

    [Theory]
    // The members the sink cannot write a single row without, in the shapes that
    // actually arrive: an explicit null, a team id that is not a guid, and a
    // reference that is not an object.
    [InlineData("""{"data":{"text":"hi","providerDeliveryId":null,"actorProviderAccountId":"a","teamId":"TEAM","reference":{"provider":"mattermost","channelOrResourceId":"c","rootThreadId":"r"}}}""")]
    [InlineData("""{"data":{"text":"hi","providerDeliveryId":"post-1","actorProviderAccountId":"a","teamId":"not-a-guid","reference":{"provider":"mattermost","channelOrResourceId":"c","rootThreadId":"r"}}}""")]
    [InlineData("""{"data":{"text":"hi","providerDeliveryId":"post-1","actorProviderAccountId":"a","teamId":"TEAM","reference":"mattermost"}}""")]
    public async Task ADecodeRowMissingARequiredMemberDeadLetters(string envelope)
    {
        var plane = await PlaneAsync();

        Assert.False(await plane.DeliverAsync(plane.Row(envelope.Replace("TEAM", plane.TeamId.ToString(), StringComparison.Ordinal))));
        Assert.Empty(plane.Ai.Chat.Requests);
    }

    [Fact]
    public async Task ARowNamingAnUnknownProviderDeadLetters()
    {
        var plane = await PlaneAsync();

        // The provider is how the sink addresses its reply rows; an unknown one
        // has no endpoint to send anything to.
        Assert.False(await plane.DeliverAsync(plane.Row(
            plane.BodyAsync("hello?", provider: "carrier-pigeon"))));
        Assert.Empty(plane.Ai.Chat.Requests);
    }

    [Fact]
    public async Task AnInertPolicyDegradesWithoutConsultingTheModel()
    {
        var plane = await PlaneAsync(policyEnabled: false);

        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("build the thing", cadence: "reply"))));

        // Honest degradation: today's verbatim Work, the no-AI hint, and the
        // model never asked.
        Assert.Empty(plane.Ai.Chat.Requests);
        var workId = await _fixture.Db.BindingWorkId(plane.Channel, plane.Root);
        Assert.NotNull(workId);
        var ack = await _fixture.Db.OutboxEnvelopeByCommand($"mention:{plane.DeliveryId}:ack");
        Assert.NotNull(ack);
        Assert.Contains("On it.", ack);
        Assert.Contains("works without AI", ack);
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
    }

    // ---- the outage law ------------------------------------------------------

    [Fact]
    public async Task ATransientOutageRequeuesBeforeItFallsBack()
    {
        var plane = await PlaneAsync();
        var row = plane.Row(plane.BodyAsync("please build the thing"));
        plane.Ai.Chat.Unavailable = true;

        // One honest requeue: never a silent drop of a human message, never a
        // DLQ, and no Work yet.
        Assert.False(await plane.DeliverAsync(row));
        Assert.Null(await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        Assert.Equal(0, await _fixture.Db.OutboxCountByCommand($"mention:{plane.DeliveryId}:ack"));

        // Once the attempt budget is spent, the no-AI fallback answers the human.
        plane.Ai.Chat.Unavailable = false;
        Assert.True(await plane.DeliverAsync(row with { Attempts = DecodeSink.FallbackAfterAttempts }));
        Assert.NotNull(await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
    }

    // ---- the answer shapes the sink must not act on --------------------------

    [Fact]
    public async Task AJunkAnswerFallsBackToVerbatimWork()
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode", "the model rambled instead of answering");

        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("just do it already", cadence: "reply"))));

        var workId = await _fixture.Db.BindingWorkId(plane.Channel, plane.Root);
        Assert.NotNull(workId);
        Assert.Equal(("open", 1L), await _fixture.Db.WorkStateRow(workId.Value));
        Assert.Equal(0, await _fixture.Db.OutboxCountByCommand($"cmd:work:{plane.DeliveryId}")); // not the model plane
        var ack = await _fixture.Db.OutboxEnvelopeByCommand($"mention:{plane.DeliveryId}:ack");
        Assert.NotNull(ack);
        Assert.Contains("On it.", ack);
        Assert.Contains("works without AI", ack); // the no-AI slash-plane hint
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
    }

    [Fact]
    public async Task AnIntentOutsideTheContractFallsBackToo()
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode", """{"intent":"hallucinate","confidence":0.99}""");

        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("what even was this"))));

        Assert.NotNull(await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        Assert.Equal(0, await _fixture.Db.OutboxCountByCommand($"cmd:chat:{plane.DeliveryId}"));
    }

    [Fact]
    public async Task ASelectionBelowTheConfidenceFloorIsTreatedAsAGuess()
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode", """{"intent":"command","command":"work","args":{},"reply":null,"confidence":0.49}""");

        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("maybe work on it"))));

        // At or above 0.5 the sink acts on the selection; below it the Work that
        // lands is the fallback's — the fallback's ledger basis, not a model one.
        Assert.NotNull(await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        Assert.Equal(0, await _fixture.Db.OutboxCountByCommand($"cmd:work:{plane.DeliveryId}"));
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
    }

    [Fact]
    public async Task ACommandVerbOutsideTheRegistryIsNotActedOn()
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode", """{"intent":"command","command":"defenestrate","args":{},"reply":null,"confidence":0.99}""");

        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("defenestrate the widget"))));

        // A confident hallucinated verb is still not a command the kernel owns.
        Assert.NotNull(await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        Assert.Equal(0, await _fixture.Db.OutboxCountByCommand($"cmd:defenestrate:{plane.DeliveryId}"));
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
    }

    // ---- the chat path -------------------------------------------------------

    [Fact]
    public async Task AChatIntentAnswersWithoutCreatingWork()
    {
        var plane = await PlaneAsync(); // policy replyMode = template: the composer is deterministic
        plane.Ai.Chat.Enqueue("decode", """{"intent":"chat","args":{},"reply":null,"confidence":0.9}""");

        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("hey there"))));

        // No model reply ⇒ the composer's deterministic facts name the message.
        var reply = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:chat:{plane.DeliveryId}");
        Assert.NotNull(reply);
        Assert.Contains("Someone said: hey there", reply);
        Assert.Equal(0, await _fixture.Db.OutboxCountByCommand($"mention:{plane.DeliveryId}:ack")); // a chat turn creates no Work
        Assert.Null(await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        Assert.Equal(0, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "model")); // the message in, the answer out
    }

    [Fact]
    public async Task AChatIntentAnswersItsOwnFallbackWhenTheComposerLegIsDown()
    {
        var plane = await PlaneAsync(policyReplyMode: "model", replyComposerDown: true);
        plane.Ai.Chat.Enqueue("decode", """{"intent":"chat","args":{},"reply":"the model's own line","confidence":0.9}""");

        // Model reply mode means the composer really does call the gateway: the
        // decode call landed and only the composer leg fails. Degraded — the
        // decode answer's own text stands — but never silent.
        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("how is it going"))));

        var reply = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:chat:{plane.DeliveryId}");
        Assert.NotNull(reply);
        Assert.Contains("the model's own line", reply);
        Assert.Equal(1, plane.Ai.Chat.Requests.Count(r => r.TemplateId == "decode"));
        Assert.Equal(0, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
    }

    [Theory]
    // The parser slices the outermost braces, so a fenced or array-wrapped
    // contract is still one contract object; these are the near-misses it must
    // throw away rather than half-read: no braces at all, invalid JSON inside
    // them, an unterminated object, and a non-string intent.
    [InlineData("```json\n{\"intent\":\"chat\",\"args\":{},\"reply\":\"fenced and fine\",\"confidence\":0.8}\n```", "fenced and fine")]
    [InlineData("I am not sure what that means", null)]
    [InlineData("{ \"intent\" }", null)]
    [InlineData("[{\"intent\":\"chat\",\"reply\":\"array-wrapped\"}]", "array-wrapped")]
    [InlineData("{\"intent\":42,\"reply\":\"numeric intent\"}", null)]
    [InlineData("{\"intent\":\"chat\",\"reply\":\"no closing brace\"", null)]
    public async Task OnlyAFullyFormedContractObjectIsActedOn(string modelText, string? expectedReply)
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode", modelText);

        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("a message to decode"))));

        var reply = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:chat:{plane.DeliveryId}");
        if (expectedReply is null)
        {
            Assert.Null(reply);
            Assert.NotNull(await _fixture.Db.BindingWorkId(plane.Channel, plane.Root)); // the fallback answered instead
            Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
        }
        else
        {
            Assert.NotNull(reply);
            Assert.Contains(expectedReply, reply);
            Assert.Equal(0, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
        }
    }

    [Fact]
    public async Task ADecodedCommandCarriesItsStringArguments()
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode",
            """{"intent":"command","command":"work","args":{"objective":"from the model","note":7},"reply":null,"confidence":0.95}""");

        Assert.True(await plane.DeliverAsync(plane.Row(plane.BodyAsync("open a work please", cadence: "reply"))));

        // The string args ride through to the Work (rendered onto the
        // objective); the numeric one is dropped, never stringified.
        var workId = await _fixture.Db.BindingWorkId(plane.Channel, plane.Root);
        Assert.NotNull(workId);
        Assert.Equal(workId, await _fixture.Db.WorkIdByObjectiveFragment("objective=from the model"));
        Assert.Null(await _fixture.Db.WorkIdByObjectiveFragment("note=7"));
        var ack = await _fixture.Db.OutboxEnvelopeByCommand($"cmd:work:{plane.DeliveryId}");
        Assert.NotNull(ack);
        Assert.Equal(0, await _fixture.Db.OutboxCountByCommand($"mention:{plane.DeliveryId}:ack")); // the model plane answered
        Assert.Equal(0, await _fixture.Db.AiTurnCount(plane.Key, "fallback"));
        // The audited model pass: the decode "in" row plus the command's own
        // in/out pair, all model-basis (a fallback would be basis "fallback").
        Assert.Equal(3, await _fixture.Db.AiTurnCount(plane.Key, "model"));
    }

    [Fact]
    public async Task AReplayedChatReplyRowAnswersTrueWithoutASecondReply()
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode", """{"intent":"chat","args":{},"reply":"one answer only","confidence":0.9}""");
        var row = plane.Row(plane.BodyAsync("a question"));

        Assert.True(await plane.DeliverAsync(row));
        Assert.Equal(1, await _fixture.Db.OutboxCountByCommand($"cmd:chat:{plane.DeliveryId}"));

        // Crash replay: the reply row already stands, so the pass is a no-op.
        plane.Ai.Chat.Enqueue("decode", """{"intent":"chat","args":{},"reply":"one answer only","confidence":0.9}""");
        Assert.True(await plane.DeliverAsync(row with { OutboxId = Guid.NewGuid() }));

        Assert.Equal(1, await _fixture.Db.OutboxCountByCommand($"cmd:chat:{plane.DeliveryId}"));
    }

    // ---- crash replay --------------------------------------------------------

    [Fact]
    public async Task AReplayedRowWithACommittedEffectRedoesNothing()
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode", WorkIntent);
        var row = plane.Row(plane.BodyAsync("open a work for this"));

        Assert.True(await plane.DeliverAsync(row));
        Assert.Equal(1, await _fixture.Db.OutboxCountByCommand($"cmd:work:{plane.DeliveryId}"));
        var ledger = await _fixture.Db.AiTurnCountTotal(plane.Key);

        // Crash simulation: the pass committed, the decode row never settled.
        plane.Ai.Chat.Enqueue("decode", WorkIntent);
        Assert.True(await plane.DeliverAsync(row with { OutboxId = Guid.NewGuid() }));

        Assert.Equal(1, await _fixture.Db.OutboxCountByCommand($"cmd:work:{plane.DeliveryId}"));
        Assert.Equal(ledger, await _fixture.Db.AiTurnCountTotal(plane.Key));
    }

    [Fact]
    public async Task AReplayedFallbackRowDoesNotCreateASecondWork()
    {
        var plane = await PlaneAsync();
        plane.Ai.Chat.Enqueue("decode", "rambling answer, so fallback");
        var row = plane.Row(plane.BodyAsync("do the thing"));

        Assert.True(await plane.DeliverAsync(row));
        var workId = await _fixture.Db.BindingWorkId(plane.Channel, plane.Root);
        Assert.NotNull(workId);

        plane.Ai.Chat.Enqueue("decode", "rambling answer, so fallback");
        Assert.True(await plane.DeliverAsync(row with { OutboxId = Guid.NewGuid() }));

        Assert.Equal(workId, await _fixture.Db.BindingWorkId(plane.Channel, plane.Root));
        Assert.Equal(2, await _fixture.Db.AiTurnCount(plane.Key, "fallback")); // the first pass only
    }

    // ---- plumbing ------------------------------------------------------------

    private sealed class Plane(KernelTestFactory factory, AiFixtures ai, Guid teamId)
    {
        public AiFixtures Ai { get; } = ai;

        private DecodeSink Sink { get; } = factory.Services.GetServices<ISink>().OfType<DecodeSink>().Single();

        public Guid TeamId { get; } = teamId;

        public string Channel { get; } = $"chan-{Guid.NewGuid():N}";

        public string Root { get; } = $"root-{Guid.NewGuid():N}";

        public string DeliveryId { get; } = $"post-{Guid.NewGuid():N}";

        public string Key => AiLedgerStore.ProviderKey("mattermost", Channel, Root);

        /// <summary>The decode row's inner payload, as the provider plane writes it.</summary>
        /// <summary>Body of a decode row as the provider plane writes it.</summary>
        public string BodyAsync(string text, string provider = "mattermost", string cadence = "reaction") =>
            JsonSerializer.Serialize(new
            {
                data = new
                {
                    text,
                    providerDeliveryId = DeliveryId,
                    actorProviderAccountId = "acct-1",
                    teamId = TeamId,
                    replyVsReaction = cadence,
                    reference = new
                    {
                        provider,
                        tenantOrOrganizationId = "demo-team",
                        channelOrResourceId = Channel,
                        rootThreadId = Root,
                        messageId = DeliveryId,
                        conversationKind = "thread",
                    },
                },
            });

        public OutboxRow Row(string envelopeJson) => new(
            Guid.NewGuid(), "decode.local", DecodeSink.RequestType, $"decode:{DeliveryId}",
            JsonDocument.Parse(envelopeJson).RootElement.Clone(), 0);

        public Task<bool> DeliverAsync(OutboxRow row) => Sink.DeliverAsync(row, CancellationToken.None);
    }

    private async Task<Plane> PlaneAsync(
        bool policyEnabled = true, string policyReplyMode = "template", bool replyComposerDown = false)
    {
        var ai = new AiFixtures();
        if (replyComposerDown)
        {
            ai.Chat.AlwaysFails.Add("reply-composer");
        }

        var factory = new KernelTestFactory(
            _fixture,
            noDispatcher: true,
            settings: new Dictionary<string, string?>
            {
                ["Providers:Mattermost:Enabled"] = "true",
                ["Providers:Mattermost:EndpointId"] = "gw-dec-" + Guid.NewGuid().ToString("N")[..8],
                ["Providers:Mattermost:Tenant"] = "demo-team",
                ["Providers:Mattermost:SiteUrl"] = "http://mattermost:8065",
                // The sink answers at all only when the deck has an endpoint;
                // the endpoint's own port stays the fake (the AiFixtures swap).
                ["Ai:Endpoints:decoder:BaseUrl"] = "http://litellm.test:4000",
                ["Ai:Endpoints:decoder:Model"] = "llm",
                ["Ai:Endpoints:decoder:ApiKey"] = "k",
            },
            aiFixtures: ai);
        _factories.Add(factory);
        factory.CreateClient().Dispose(); // force host start => migrations
        await _fixture.Db.SettlePendingOutboxRows();

        var slug = "decguard-" + Guid.NewGuid().ToString("N")[..10];
        await _fixture.Db.SeedTeam(slug);
        await _fixture.Db.AdminConfigPurge("llm");
        await _fixture.Db.SeedAdminConfig(
            "llm",
            $$"""{"enabled":{{(policyEnabled ? "true" : "false")}},"replyMode":"{{policyReplyMode}}","maxOutputTokens":600}""");
        await _fixture.Db.SeedTeamSetting(
            slug, "team", slug, "notifications", """{"default":{"replyVsReaction":"reply"}}"""); // team-scoped: no global row touched

        Guid teamId;
        await using (var db = await factory.Services
                         .GetRequiredService<IDbContextFactory<FacilitatorDbContext>>().CreateDbContextAsync())
        {
            teamId = await db.Teams.AsNoTracking().Where(t => t.Slug == slug).Select(t => t.TeamId).SingleAsync();
        }

        return new Plane(factory, ai, teamId);
    }
}
