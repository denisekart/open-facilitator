using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Facilitator.Tests.Ai;

/// <summary>
/// The bounded read phase (7c): a model turn may first ask for GETs drawn
/// from the caller's own catalog slice; they run as loopback calls under the
/// operator's token, the assistant row records the trail, and the second
/// completion answers with prose. A read outside the caller's slice (or no
/// usable read at all) degrades the turn to the canned fallback with zero
/// action rows — never a guess.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class ChatReadLoopTests(DispatchDbFixture fixture) : IAsyncLifetime
{
    private KernelTestFactory _factory = null!;
    private AiFixtures _ai = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _ai = new AiFixtures();
        _factory = new KernelTestFactory(
            fixture,
            noDispatcher: true,
            useKestrel: true,
            aiFixtures: _ai);
        _factory.CreateClient().Dispose(); // force host start => migrations
        await fixture.Db.AdminConfigPurge("llm");
        await fixture.Db.SeedAdminConfig("llm", """{"enabled":true,"replyMode":"model","maxOutputTokens":600}""");
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _factory.Dispose();
        await fixture.Db.AdminConfigPurge("llm");
    }

    [Fact]
    public async Task ReadsRunUnderTheCallerAndTheTurnRecordsTheTrail()
    {
        // alice: framework-admin + ai-operator — the admin roll-up is inside
        // her slice, so the read is admitted and answers 200.
        using var alice = await UserAsync("alice");
        var sessionId = (await KernelOps.PostJsonAsync(alice, "/api/chat/sessions", "{}", HttpStatusCode.OK))
            .GetProperty("sessionId").GetGuid();
        _ai.Chat.Enqueue("chat", """{"reads":[{"method":"GET","path":"/api/admin/summary"}]}""");
        _ai.Chat.Enqueue("chat", """{"reply":"Nothing is stuck.","actions":[]}""");

        var turn = await KernelOps.PostJsonAsync(
            alice, $"/api/chat/sessions/{sessionId}/messages", """{"text":"what's stuck right now?"}""",
            HttpStatusCode.OK);

        // The loopback read ran; the trail is the stored document shape
        // {"reads":[...]} naming the path and its 200.
        var read = Assert.Single(turn.GetProperty("reads").GetProperty("reads").EnumerateArray());
        Assert.Equal("/api/admin/summary", read.GetProperty("path").GetString());
        Assert.Equal(200, read.GetProperty("status").GetInt32());
        Assert.Equal("Nothing is stuck.", turn.GetProperty("reply").GetString());

        // The stored assistant row carries the same trail and the model basis.
        var messages = await KernelOps.GetJsonAsync(
            alice, $"/api/chat/sessions/{sessionId}/messages", HttpStatusCode.OK);
        var assistant = messages.EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("model", assistant.GetProperty("basis").GetString());
        Assert.Equal(
            "/api/admin/summary",
            assistant.GetProperty("reads").GetProperty("reads").EnumerateArray().Single().GetProperty("path").GetString());
    }

    [Fact]
    public async Task OutOfSliceReadDegradesToFallbackWithNoActions()
    {
        // minty holds no admin roles: an admin sweep is outside the slice she
        // was given, so the read is dropped, no usable read survives, and the
        // whole turn falls back (no second call, no actions).
        using var minty = await UserAsync("minty");
        var sessionId = (await KernelOps.PostJsonAsync(minty, "/api/chat/sessions", "{}", HttpStatusCode.OK))
            .GetProperty("sessionId").GetGuid();
        _ai.Chat.Enqueue("chat", """{"reads":[{"method":"POST","path":"/api/admin/sweeps/nudge"}]}""");

        var turn = await KernelOps.PostJsonAsync(
            minty, $"/api/chat/sessions/{sessionId}/messages", """{"text":"sweep it"}""",
            HttpStatusCode.OK);
        Assert.False(turn.TryGetProperty("reads", out var trail) && trail.ValueKind == JsonValueKind.Array);

        var messages = await KernelOps.GetJsonAsync(
            minty, $"/api/chat/sessions/{sessionId}/messages", HttpStatusCode.OK);
        var assistant = messages.EnumerateArray()
            .Single(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("fallback", assistant.GetProperty("basis").GetString());
        Assert.Empty(assistant.GetProperty("actions").EnumerateArray());
    }

    private async Task<HttpClient> UserAsync(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await KeycloakFixture.TokenForUserAsync(user, user));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
