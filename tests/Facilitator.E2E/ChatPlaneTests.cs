using System.Net;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Facilitator.E2E;

/// <summary>
/// Browser tier, AiDeck walkthrough: alice (realm role <c>ai-operator</c>)
/// opens the chat panel; the handler-backed chat gateway (real
/// <c>OpenAiChatCompletionsGateway</c> wire path, canned LiteLLM response)
/// proposes a dead-letter replay; Confirm executes it under alice's own
/// bearer — executed badge + <c>aiInitiated</c> audit. Negative gate: the
/// kernel-operator CLI token is refused by the chat bindings.
/// </summary>
[Collection("e2e")]
public sealed class ChatPlaneTests(PostgresFixture fixture)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ChatProposesReplayAndConfirmExecutesWithAiAttribution()
    {
        var handler = new ScriptedLlmHandler();
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true, _ => new PoisonSink(), handler);
        var objective = $"e2e-chat-{Guid.NewGuid():N}";

        // A dead letter to point the model at: poison-drain a kernel.log row.
        var (workId, _) = await CreateWorkAsync(host, objective);
        var dispatcher = host.Services.GetRequiredService<IEventDispatcher>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            await fixture.Db.ExpireOutboxBackoff(workId);
        }

        var deadLetterId = Guid.Parse((await fixture.Db.DeadLetterIdsForWork(workId)).First());

        // Live policy through the admin surface (If-Match/X-Change-Reason law).
        await PutLlmPolicyAsync(host);
        try
        {
            var content = """
                {"reply":"Queued the replay.","actions":[{"method":"POST","path":"/api/admin/dead-letters/DLID/replay","body":{},"description":"Replay the stuck dead letter"}]}
                """.Replace("DLID", deadLetterId.ToString()).Replace("\"", "\\\"");
            handler.Enqueue("/v1/chat/completions",
                """
                {"choices":[{"message":{"content":"CONTENT"}}],"usage":{"prompt_tokens":5,"completion_tokens":10}}
                """.Replace("CONTENT", content));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            var page = await NewLoginPageAsync(browser, host.ServerAddress);
            await Expect(page.GetByTestId("view-chat")).ToBeVisibleAsync();
            await page.GetByTestId("view-chat").ClickAsync();

            await page.GetByTestId("chat-input").FillAsync("replay the stuck dead letter");
            await page.GetByTestId("chat-send").ClickAsync();

            await Expect(page.GetByTestId("chat-action-post")).ToBeVisibleAsync();
            // The actionId is server-generated; the card is the only one, so
            // the confirm/status testids are matched by prefix.
            var confirm = page.GetByTestId(new System.Text.RegularExpressions.Regex(@"^chat-action-confirm-"));
            await Expect(confirm).ToBeVisibleAsync();
            await confirm.ClickAsync();
            await Expect(page.GetByTestId(new System.Text.RegularExpressions.Regex(@"^chat-action-status-"))).ToContainTextAsync("executed");

            // The replay rode the kernel's own gate: a fresh pending command row.
            Assert.True(await fixture.Db.OutboxRowByCommand($"replay:{deadLetterId}", "pending"),
                "confirm executes through the kernel's own gate");

            // Wire proof: the outbound request carried a bearer + model body.
            var (path, body, auth) = handler.Captured.Single(c => c.Path.Contains("/v1/chat/completions", StringComparison.Ordinal));
            Assert.Contains("fake-decoder", body);
            Assert.NotNull(auth);
            Assert.StartsWith("Bearer ", auth);

            // Attribution: audit row carries aiInitiated + alice's canonical
            // actor (oidc:iss|sub — display names never enter audit).
            var executed = (await fixture.Db.AuditRows(decision: "allow"))
                .Where(r => r.Action == "chat.action.executed" && r.Detail.Contains("aiInitiated", StringComparison.Ordinal))
                .ToList();
            Assert.NotEmpty(executed);
            var aliceToken = await KeycloakFixture.TokenForUserAsync("alice", "alice");
            var claims = JsonDocument.Parse(
                System.Text.Encoding.UTF8.GetString(Base64UrlDecode(aliceToken.Split('.')[1]))).RootElement;
            Assert.Equal(
                $"oidc:{KeycloakFixture.Issuer}|{claims.GetProperty("sub").GetString()}",
                executed[0].Actor);
        }
        finally
        {
            await fixture.Db.AdminConfigPurge("llm");
        }
    }

    [Fact]
    public async Task ChatPanelIsUsableOnAMobileViewport()
    {
        var handler = new ScriptedLlmHandler();
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true, _ => new PoisonSink(), handler);
        await PutLlmPolicyAsync(host);
        try
        {
            var content = """
                {"reply":"Noted. **No** fences needed.","actions":[]}
                """.Replace("\"", "\\\"");
            handler.Enqueue("/v1/chat/completions",
                """
                {"choices":[{"message":{"content":"CONTENT"}}],"usage":{"prompt_tokens":5,"completion_tokens":4}}
                """.Replace("CONTENT", content));

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync();
            // iPhone-14-class viewport: everything the operator needs stays
            // reachable, nothing overflows the screen sideways.
            var page = await NewLoginPageAsync(browser, host.ServerAddress, new ViewportSize { Width = 390, Height = 844 });
            await page.GetByTestId("view-chat").ClickAsync();
            await page.GetByTestId("chat-input").FillAsync("hello deck");
            await page.GetByTestId("chat-send").ClickAsync();

            var md = page.GetByTestId("chat-md");
            await Expect(md).ToBeVisibleAsync();
            await Expect(md.Locator("strong")).ToHaveTextAsync("No");
            await Expect(page.GetByTestId("chat-input")).ToBeVisibleAsync();
            var overflow = await page.EvaluateAsync<int>(
                "document.documentElement.scrollWidth - document.documentElement.clientWidth");
            Assert.True(overflow <= 0, $"horizontal overflow of {overflow}px at 390px width");
        }
        finally
        {
            await fixture.Db.AdminConfigPurge("llm");
        }
    }

    [Fact]
    public async Task NonOperatorTokenIsRefusedByTheChatPlane()
    {
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(browser, host.ServerAddress);

        // alice (ai-operator in the realm) sees the panel...
        await Expect(page.GetByTestId("view-chat")).ToBeVisibleAsync();

        // ...the kernel-operator CLI token does not hold chat.use: the gate refuses.
        using var cli = host.CreateSeedClient();
        var denied = await cli.PostAsync(
            "/api/chat/sessions", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    // ---- plumbing ----------------------------------------------------------

    /// <summary>PUT the llm policy row via the admin config surface, enabling
    /// model replies (the sanctioned writer — change-reason law).</summary>
    private static async Task PutLlmPolicyAsync(KernelWebHostFixture host)
    {
        using var client = host.CreateSeedClient();
        client.DefaultRequestHeaders.Add("X-Change-Reason", "e2e chat plane");
        client.DefaultRequestHeaders.IfMatch.Add(System.Net.Http.Headers.EntityTagHeaderValue.Any);
        var response = await client.PatchAsync(
            "/api/admin/config/llm",
            new StringContent(
                """{"enabled":true,"verbosityDefault":"balanced","replyMode":"model","decisionMode":"auto","maxOutputTokens":600}""",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Hosted-Login flow as alice (the ai-operator test subject).</summary>
    private static async Task<IPage> NewLoginPageAsync(
        IBrowser browser, string baseUrl, ViewportSize? viewport = null)
    {
        var context = await browser.NewContextAsync(viewport is null ? null : new BrowserNewContextOptions { ViewportSize = viewport });
        var page = await context.NewPageAsync();
        await page.GotoAsync(baseUrl);
        await page.GetByTestId("login").ClickAsync();
        await page.WaitForURLAsync("**/realms/facilitator/**");
        await page.FillAsync("input[name=\"username\"]", "alice");
        await page.FillAsync("input[name=\"password\"]", "alice");
        await page.ClickAsync("#kc-login");
        await page.WaitForURLAsync($"{baseUrl}/**");
        await Expect(page.GetByTestId("identity-chip")).ToBeVisibleAsync();
        return page;
    }

    private static async Task<(Guid WorkId, long Version)> CreateWorkAsync(KernelWebHostFixture host, string objective)
    {
        using var client = host.CreateSeedClient();
        var created = await client.PostAsync(
            "/api/works",
            new StringContent(JsonSerializer.Serialize(new { objective }, Web), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("workId").GetGuid(), doc.RootElement.GetProperty("version").GetInt64());
    }

    private static byte[] Base64UrlDecode(string segment) =>
        Convert.FromBase64String(segment.Replace('-', '+').Replace('_', '/').PadRight(segment.Length + (4 - segment.Length % 4) % 4, '='));
}
