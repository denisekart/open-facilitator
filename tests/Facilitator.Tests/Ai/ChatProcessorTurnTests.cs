using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Ai;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The chat turn engine's remaining branches, driven through the HTTP surface
/// with the paired fakes (the simulation rule: the two real gateways are always
/// swapped, so the canned shapes below stand in for the model). Covers: the
/// slash shortcut (zero model calls), the junk command-registry row, session
/// titling and its truncation, proposal rows + catalog summaries, the
/// whole-turn degradation when one proposal leaves the principal's slice, the
/// fenced-code-block answer, an unreadable answer, an empty answer, the gateway
/// outage, and the listing/history/action reads.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ChatProcessorTurnTests : IAsyncLifetime
{
    private readonly DispatchDbFixture _fixture;

    private AiFixtures _ai = null!;

    private KernelTestFactory _factory = null!;

    public ChatProcessorTurnTests(DispatchDbFixture fixture) => _fixture = fixture;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _ai = new AiFixtures();
        // useKestrel: the read phase executes loopback HTTP under the caller's
        // own bearer, which the in-proc TestServer cannot serve.
        _factory = new KernelTestFactory(
            _fixture, noDispatcher: true, useKestrel: true, aiFixtures: _ai);
        _factory.CreateClient().Dispose(); // force host start => migrations
        await _fixture.Db.AdminConfigPurge("llm");
        await _fixture.Db.AdminConfigPurge("commands");
        await _fixture.Db.SeedAdminConfig("llm", """{"enabled":true,"replyMode":"model","maxOutputTokens":600}""");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await _fixture.Db.AdminConfigPurge("llm");
        await _fixture.Db.AdminConfigPurge("commands");
    }

    [Fact]
    public async Task AKnownSlashCommandNeverAsksTheModelAndTitlesTheSession()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);

        var turn = await TurnAsync(alice, sessionId, "/status");

        Assert.Empty(_ai.Chat.Requests); // the model is never consulted for a known verb
        Assert.Contains("Available commands:", turn.GetProperty("reply").GetString());
        Assert.Contains("/work", turn.GetProperty("reply").GetString()); // the honest command list
        Assert.DoesNotContain("/run-ci-tests", turn.GetProperty("reply").GetString());

        // A recognized registry verb is ledgered as a slash turn, and the
        // session takes its title from the first user message.
        // A recognized registry verb is ledgered as a slash turn (never a model
        // one). Both rows of a turn carry one timestamp, so the endpoint's
        // CreatedAt ordering cannot settle within a turn: assert the pair.
        var history = await HistoryAsync(alice, sessionId);
        Assert.Equal(
            new string?[] { "fallback", "slash" },
            history.Select(m => m.GetProperty("basis").GetString()).OrderBy(b => b, StringComparer.Ordinal).ToArray());
        var session = await KernelOps.GetJsonAsync(alice, $"/api/chat/sessions/{sessionId}", HttpStatusCode.OK);
        Assert.Equal("/status", session.GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheFirstTurnTitlesTheSessionTruncatedToTheColumnLimit()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", """{"reply":"noted","actions":[]}""");

        await TurnAsync(alice, sessionId, new string('x', 200));

        var session = await KernelOps.GetJsonAsync(alice, $"/api/chat/sessions/{sessionId}", HttpStatusCode.OK);
        Assert.Equal(new string('x', ChatProcessor.TitleLimit), session.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AJunkCommandRegistryRowDegradesToBuiltInsNotToAnError()
    {
        await _fixture.Db.SeedAdminConfig("commands", """{"commands":"not-an-array"}""");
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", """{"reply":"noted","actions":[]}""");

        var turn = await TurnAsync(alice, sessionId, "hello there");

        Assert.Equal("noted", turn.GetProperty("reply").GetString());
        Assert.Equal("model", (await AssistantAsync(alice, sessionId)).GetProperty("basis").GetString());
    }

    [Fact]
    public async Task AModelTurnStoresProposalsWithTheirCatalogSummaryAndExpiry()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        // GET /api/works/{id} carries no operation binding, so the gate admits
        // any authenticated principal — the proposal is inside alice's slice.
        _ai.Chat.Enqueue("chat", "{\"reply\":\"looking it up\",\"actions\":[{\"method\":\"GET\",\"path\":\"/api/works/some-id\",\"description\":\"Read the work\"}]}");

        var turn = await TurnAsync(alice, sessionId, "look up the work");

        Assert.Equal("looking it up", turn.GetProperty("reply").GetString());
        var action = Assert.Single(turn.GetProperty("actions").EnumerateArray());
        Assert.Equal("GET", action.GetProperty("method").GetString());
        Assert.Equal("/api/works/some-id", action.GetProperty("path").GetString());
        Assert.Equal("pending", action.GetProperty("status").GetString());
        Assert.Equal("Work detail (state, version, timestamps).", action.GetProperty("summary").GetString()); // the catalog line, not the model's text
        Assert.Equal("Read the work", action.GetProperty("description").GetString());
        Assert.NotEqual(JsonValueKind.Null, action.GetProperty("expiresAt").ValueKind); // the TTL row
        Assert.Equal(JsonValueKind.Null, action.GetProperty("body").ValueKind);

        // Nothing ran: the proposal is an approval-pending row (the owner's own
        // view; a foreign principal would be refused the cross-principal read).
        var actionId = action.GetProperty("actionId").GetGuid();
        var stored = await KernelOps.GetJsonAsync(alice, $"/api/chat/actions/{actionId}", HttpStatusCode.OK);
        Assert.Equal("pending", stored.GetProperty("status").GetString());

        // The user + assistant rows are stored, oldest-first, and the proposal
        // rides the assistant row (history browsing shows the action lifecycle
        // without a per-action poll).
        var messages = await HistoryAsync(alice, sessionId);
        Assert.Equal(
            new string?[] { "assistant", "user" }, // one timestamp per turn ⇒ order-insensitive
            messages.Select(m => m.GetProperty("role").GetString()).OrderBy(r => r, StringComparer.Ordinal).ToArray());
        var assistant = messages.Single(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("model", assistant.GetProperty("basis").GetString());
        Assert.Equal("chat", assistant.GetProperty("templateId").GetString());
        var nested = Assert.Single(assistant.GetProperty("actions").EnumerateArray());
        Assert.Equal("pending", nested.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AProposalOutsideThePrincipalsOwnSliceVoidsWithEveryProposal()
    {
        using var minty = await UserAsync("minty"); // ai-operator only: no kernel-operator
        var sessionId = await SessionAsync(minty);
        // work.write binds POST /api/works to kernel-operator, so the second
        // proposal leaves her slice even though the first is inside it.
        _ai.Chat.Enqueue("chat", """
            {"reply":"two ideas","actions":[
              {"method":"GET","path":"/api/works","description":"List works"},
              {"method":"POST","path":"/api/works","description":"Create a work","body":{"objective":"nope"}}]}
            """);

        var turn = await TurnAsync(minty, sessionId, "list the works and open another");

        Assert.Contains("must match a known kernel endpoint", turn.GetProperty("reply").GetString());
        Assert.Empty(turn.GetProperty("actions").EnumerateArray()); // the whole turn, not half of it
        Assert.Equal("fallback", (await AssistantAsync(minty, sessionId)).GetProperty("basis").GetString());
    }

    [Fact]
    public async Task AFencedJsonAnswerParsesLikeAPlainOne()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", "```json\n{\"reply\":\"fenced\",\"actions\":[]}\n```");

        var turn = await TurnAsync(alice, sessionId, "answer from a fenced block");

        Assert.Equal("fenced", turn.GetProperty("reply").GetString());
        Assert.Equal("model", (await AssistantAsync(alice, sessionId)).GetProperty("basis").GetString());
    }

    [Fact]
    public async Task AnAnswerThatIsNotTheContractDegradesToTheCommandList()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", "I would probably use the slash commands here.");

        var turn = await TurnAsync(alice, sessionId, "what should I do");

        Assert.Contains("Available commands:", turn.GetProperty("reply").GetString());
        Assert.Empty(turn.GetProperty("actions").EnumerateArray());
        Assert.Equal("fallback", (await AssistantAsync(alice, sessionId)).GetProperty("basis").GetString());
    }

    [Fact]
    public async Task AnEmptyAnswerWithNoProposalsAnswersTheCatalogHint()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", """{"reply":"","actions":[]}""");

        var turn = await TurnAsync(alice, sessionId, "say nothing at all");

        Assert.Contains("must match a known kernel endpoint", turn.GetProperty("reply").GetString());
        Assert.Equal("model", (await AssistantAsync(alice, sessionId)).GetProperty("basis").GetString());
    }

    [Fact]
    public async Task AGatewayOutageKeepsTheChatPlaneUsable()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Unavailable = true;

        var turn = await TurnAsync(alice, sessionId, "anything at all");

        Assert.Contains("without it", turn.GetProperty("reply").GetString());
        Assert.Equal("fallback", (await AssistantAsync(alice, sessionId)).GetProperty("basis").GetString());
        _ai.Chat.Unavailable = false;
    }

    [Fact]
    public async Task SessionsListNarrowsToTheOwnerAndListingOthersNeedsTheGrantedRole()
    {
        using var alice = await UserAsync("alice");
        using var minty = await UserAsync("minty");
        var aliceSession = await SessionAsync(alice);
        var mintySession = await SessionAsync(minty);
        _ai.Chat.Enqueue("chat", """{"reply":"one","actions":[]}""");
        _ai.Chat.Enqueue("chat", """{"reply":"two","actions":[]}""");
        await TurnAsync(alice, aliceSession, "first");
        await TurnAsync(minty, mintySession, "second");

        // Own list: exactly the caller's session, newest-activity-first.
        var mine = (await KernelOps.GetJsonAsync(minty, "/api/chat/sessions", HttpStatusCode.OK))
            .EnumerateArray().Select(v => v.GetProperty("sessionId").GetGuid()).ToList();
        Assert.Contains(mintySession, mine);
        Assert.DoesNotContain(aliceSession, mine);

        // ?all=true is the chat.read-all power. The shipped dev config binds
        // that id to an operation, not to a granted role, so listing everyone
        // is refused rather than silently narrowed — and browsing another's
        // history needs the same grant.
        await KernelOps.GetJsonAsync(minty, "/api/chat/sessions?all=true", HttpStatusCode.Forbidden);
        await KernelOps.GetJsonAsync(minty, $"/api/chat/sessions/{aliceSession}/messages", HttpStatusCode.Forbidden);

        // The same query with the grant verified (what the endpoint does after
        // its own check) lists every owner.
        var processor = _factory.Services.GetRequiredService<ChatProcessor>();
        var everyone = await processor.ListSessionsAsync(
            $"oidc:{KeycloakFixture.Issuer}|nobody", all: true, CancellationToken.None);
        Assert.Contains(everyone, s => s.SessionId == aliceSession);
        Assert.Contains(everyone, s => s.SessionId == mintySession);
        var own = await processor.ListSessionsAsync(
            $"oidc:{KeycloakFixture.Issuer}|nobody", all: false, CancellationToken.None);
        Assert.Empty(own);

        // An action row loads with its owning session (the access check's input).
        _ai.Chat.Enqueue("chat", """{"reply":"ok","actions":[{"method":"GET","path":"/api/works/load-me","description":"Read"}]}""");
        var withAction = await TurnAsync(minty, mintySession, "another proposal");
        var loaded = await processor.LoadActionAsync(
            withAction.GetProperty("actions")[0].GetProperty("actionId").GetGuid(), CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(mintySession, loaded.Value.Session.SessionId);
        Assert.Equal("pending", loaded.Value.Action.Status);
        Assert.Null(await processor.LoadActionAsync(Guid.NewGuid(), CancellationToken.None));

        // Both rows of the session are there (within a turn the rows share a
        // CreatedAt and the endpoint breaks ties on the message guid, so the
        // pair — not the sequence — is what is observable).
        var roles = (await HistoryAsync(alice, aliceSession))
            .Select(m => m.GetProperty("role").GetString()).OrderBy(r => r, StringComparer.Ordinal).ToList();
        Assert.Equal(new string?[] { "assistant", "user" }, roles);
    }

    [Fact]
    public async Task AFencedReadAnswerRunsTheReadsAndAsksAgain()
    {
        using var alice = await UserAsync("alice"); // framework-admin: the admin roll-up is in her slice
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", """
            ```json
            {"reads":[{"method":"GET","path":"/api/admin/summary"}]}
            ```
            """);
        _ai.Chat.Enqueue("chat", """{"reply":"three works, none stuck","actions":[]}""");

        var turn = await TurnAsync(alice, sessionId, "what is stuck");

        // The read trail rides the assistant row: what was asked of the kernel
        // and what each read answered.
        Assert.Equal("three works, none stuck", turn.GetProperty("reply").GetString());
        var reads = turn.GetProperty("reads");
        var row = Assert.Single(reads.GetProperty("reads").EnumerateArray());
        Assert.Equal("GET", row.GetProperty("method").GetString());
        Assert.Equal("/api/admin/summary", row.GetProperty("path").GetString());
        Assert.Equal(200, row.GetProperty("status").GetInt32());

        // Two model calls, one ledger row carrying the summed token usage.
        // Two model calls, one ledger row (the answer), and the model basis.
        Assert.Equal(2, _ai.Chat.Requests.Count(r => r.TemplateId == "chat"));
        var assistant = await AssistantAsync(alice, sessionId);
        Assert.Equal("model", assistant.GetProperty("basis").GetString());
        Assert.Equal(1, await _fixture.Db.AiTurnCount(AiLedgerStore.ChatKey(sessionId), "model"));
    }

    [Fact]
    public async Task AReadTurnThatLosesTheModelOnTheSecondCallDegrades()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", """{"reads":[{"method":"GET","path":"/api/admin/summary"}]}""");
        _ai.Chat.FailAfterCalls["chat"] = 1; // the reads land, the second model call does not

        var turn = await TurnAsync(alice, sessionId, "what is stuck");

        // The reads ran, the second call died, and the answer is the honest
        // no-AI line. The read trail is not stored: no model answer ever
        // interpreted it, so the turn is recorded as the plain fallback it is.
        Assert.Equal(
            "AI unavailable — the chat plane stays usable: commands and the admin surface work without it.",
            turn.GetProperty("reply").GetString());
        Assert.Equal(JsonValueKind.Null, turn.GetProperty("reads").ValueKind);
        Assert.False(turn.GetProperty("actions").EnumerateArray().Any());
        var assistant = await AssistantAsync(alice, sessionId);
        Assert.Equal("fallback", assistant.GetProperty("basis").GetString());
        Assert.Equal(2, _ai.Chat.Requests.Count(r => r.TemplateId == "chat")); // the outage happened on the second
    }

    [Fact]
    public async Task AFenceWithNoLineBreakIsNotAnAnswer()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", "```no newline after the fence marker");

        var turn = await TurnAsync(alice, sessionId, "say something");

        Assert.Contains("Available commands:", turn.GetProperty("reply").GetString());
        Assert.Equal("fallback", (await AssistantAsync(alice, sessionId)).GetProperty("basis").GetString());
    }

    [Fact]
    public async Task AnAnswerWhoseActionsAreNotAnArrayIsNotAnAnswer()
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", """{"reply":"sure","actions":"all of them"}""");

        var turn = await TurnAsync(alice, sessionId, "do something");

        Assert.Contains("Available commands:", turn.GetProperty("reply").GetString());
        Assert.Equal("fallback", (await AssistantAsync(alice, sessionId)).GetProperty("basis").GetString());
    }

    [Theory]
    // A proposal the model worded but did not address is dropped, not half-
    // stored: no method, no path or no description all fail the same gate.
    [InlineData("""{"method":"GET","path":"/api/works/some-id"}""")] // no description
    [InlineData("""{"path":"/api/works/some-id","description":"Read"}""")] // no method
    [InlineData("""{"method":"GET","description":"Read"}""")] // no path
    [InlineData("""{"method":"GET","path":42,"description":"Read"}""")] // path of the wrong kind
    public async Task AProposalWithoutItsAddressOrItsWordingIsDropped(string action)
    {
        using var alice = await UserAsync("alice");
        var sessionId = await SessionAsync(alice);
        _ai.Chat.Enqueue("chat", $$"""{"reply":"one good, one junk","actions":[{{action}}]}""");

        var turn = await TurnAsync(alice, sessionId, "try to propose something");

        // An unaddressable proposal is not half-stored: the whole turn degrades
        // to the catalog line, with no rows at all.
        Assert.Contains("every proposal must match a known kernel endpoint", turn.GetProperty("reply").GetString());
        Assert.Empty(turn.GetProperty("actions").EnumerateArray());
        Assert.Equal("fallback", (await AssistantAsync(alice, sessionId)).GetProperty("basis").GetString());
    }

    [Fact]
    public async Task AReadPhaseThatFindsNothingUsableDegradesInsteadOfGuessing()
    {
        using var minty = await UserAsync("minty"); // no admin roll-up in her slice
        var sessionId = await SessionAsync(minty);
        _ai.Chat.Enqueue("chat", """{"reads":[{"method":"GET","path":"/api/admin/summary"}]}""");

        var turn = await TurnAsync(minty, sessionId, "what is stuck?");

        // The only read asked for is outside her slice, so nothing was
        // executed: the turn degrades to the fallback with zero actions.
        Assert.Empty(turn.GetProperty("actions").EnumerateArray());
        Assert.Single(_ai.Chat.Requests); // no second completion after a useless read phase
        Assert.Equal("fallback", (await AssistantAsync(minty, sessionId)).GetProperty("basis").GetString());
    }

    // ---- plumbing ------------------------------------------------------------

    private async Task<string> TokenFor(string user) => await KeycloakFixture.TokenForUserAsync(user, user);

    private async Task<HttpClient> UserAsync(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenFor(user));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<Guid> SessionAsync(HttpClient client) =>
        (await KernelOps.PostJsonAsync(client, "/api/chat/sessions", "{}", HttpStatusCode.OK))
            .GetProperty("sessionId").GetGuid();

    private static async Task<JsonElement> TurnAsync(HttpClient client, Guid sessionId, string text) =>
        await KernelOps.PostJsonAsync(
            client,
            $"/api/chat/sessions/{sessionId}/messages",
            JsonSerializer.Serialize(new { text }),
            HttpStatusCode.OK);

    private static async Task<List<JsonElement>> HistoryAsync(HttpClient client, Guid sessionId) =>
        (await KernelOps.GetJsonAsync(client, $"/api/chat/sessions/{sessionId}/messages", HttpStatusCode.OK))
            .EnumerateArray().ToList();

    private static async Task<JsonElement> AssistantAsync(HttpClient client, Guid sessionId) =>
        (await HistoryAsync(client, sessionId)).Single(m => m.GetProperty("role").GetString() == "assistant");
}
