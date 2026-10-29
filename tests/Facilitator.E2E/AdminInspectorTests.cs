using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Facilitator.E2E;

/// <summary>
/// Plan 21 browser tier: the embedded SPA against a real Kestrel kernel +
/// hosted Keycloak login, driven end-to-end (spec §17.6 walkthrough, UI
/// edition): login gate, search→inspector→approval, links, dead-letter
/// replay with the replay-once toast, config PATCH with the 412 conflict
/// path, and the anonymous gate. Sequential on the shared fixture pair;
/// every test logs in fresh (Keycloak sessions are per-context).
/// </summary>
[Collection("e2e")]
public sealed class AdminInspectorTests(PostgresFixture fixture)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- scenarios ---------------------------------------------------------

    [Fact]
    public async Task LoginShowsTheIdentityChip()
    {
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(browser, host.ServerAddress);
        Assert.Contains("alice", await page.GetByTestId("identity-chip").TextContentAsync());
        await Expect(page.GetByTestId("health-strip")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task SearchOpensInspectorAndApproveFlipsState()
    {
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true);
        var objective = $"e2e-approve-{Guid.NewGuid():N}";
        var (workId, approvalId) = await SeedWorkWithApprovalAsync(host, objective);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(browser, host.ServerAddress);
        await page.GetByTestId("search-input").FillAsync(objective);
        await page.GetByTestId("search-go").ClickAsync();
        await page.GetByTestId($"hit-works-{workId.ToString()[..8]}").ClickAsync();

        await Expect(page.GetByTestId("work-inspector")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("timeline-created")).ToBeVisibleAsync();
        var shortId = approvalId.ToString()[..8];
        await Expect(page.GetByTestId($"approval-state-{shortId}")).ToHaveTextAsync("pending");

        await page.GetByTestId($"approve-{shortId}").ClickAsync();
        await Expect(page.GetByTestId("inspector-notice")).ToHaveTextAsync("approval approved");
        await Expect(page.GetByTestId($"approval-state-{shortId}")).ToHaveTextAsync("approved");
        var ledger = await fixture.Db.Approval(approvalId);
        Assert.Equal("approved", ledger!.State);
    }

    [Fact]
    public async Task LinksViewShowsOnlineTrustAndOffline()
    {
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true);
        // Boot first (migrations) so seeding never races schema creation.
        var baseUrl = host.ServerAddress;
        var live = $"gw.e2e-live-{Guid.NewGuid():N}";
        var dark = $"gw.e2e-dark-{Guid.NewGuid():N}";
        await fixture.Db.SeedGatewayEnrollment(live, "team-alpha", "shared-runtime");
        await fixture.Db.SeedLinkSession(live, 3, DateTime.UtcNow.AddMinutes(5));
        await fixture.Db.SeedGatewayEnrollment(dark, "team-beta", "isolated-executor");
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(browser, baseUrl);
        await page.GetByTestId("view-links").ClickAsync();

        await Expect(page.GetByTestId($"gateway-{live}")).ToBeVisibleAsync();
        await Expect(page.GetByTestId($"online-{live}")).ToHaveTextAsync("online");
        await Expect(page.GetByTestId($"trust-{live}")).ToHaveTextAsync("shared-runtime");
        await Expect(page.GetByTestId($"offline-{dark}")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task DeadLetterReplayNoticesOnceAndSecondReplayReportsLedger()
    {
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true, _ => new PoisonSink());
        var objective = $"e2e-deadletter-{Guid.NewGuid():N}";

        // Create a Work over the API (kernel.log outbox rows), then drain the
        // poisoned sink 5x to dead-letter the rows.
        var (workId, _) = await CreateWorkAsync(host, objective);
        var dispatcher = host.Services.GetRequiredService<IEventDispatcher>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            await fixture.Db.ExpireOutboxBackoff(workId);
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(browser, host.ServerAddress);
        await page.GetByTestId("view-deliveries").ClickAsync();
        await page.GetByTestId("filter-dead_letter").ClickAsync();

        var deadLetterId = Guid.Parse((await fixture.Db.DeadLetterIdsForWork(workId)).First());
        var shortId = deadLetterId.ToString()[..8];
        await Expect(page.GetByTestId($"delivery-{shortId}")).ToBeVisibleAsync();

        await page.GetByTestId($"replay-{shortId}").ClickAsync();
        await page.GetByTestId("replay-reason").FillAsync("e2e replay");
        await page.GetByTestId($"replay-confirm-{shortId}").ClickAsync();
        await Expect(page.GetByTestId("replay-notice")).ToContainTextAsync($"replayed {shortId}");
        Assert.True(await fixture.Db.OutboxRowByCommand($"replay:{deadLetterId}", "pending"),
            "replay lands a fresh pending dispatch");

        // Replay-once law surfaces as the explanation toast, not a stack trace.
        await page.GetByTestId($"replay-{shortId}").ClickAsync();
        await page.GetByTestId("replay-reason").FillAsync("e2e replay again");
        await page.GetByTestId($"replay-confirm-{shortId}").ClickAsync();
        await Expect(page.GetByTestId("replay-notice")).ToContainTextAsync("already replayed");
    }

    [Fact]
    public async Task ConfigPatchStoresAndConflictExplainsTheOtherWriter()
    {
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true);
        // Boot first (migrations), then isolate the resource on the shared DB.
        var baseUrl = host.ServerAddress;
        await fixture.Db.AdminConfigPurge("nudge-policy");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(browser, baseUrl);
        await page.GetByTestId("view-config").ClickAsync();
        await page.GetByTestId("config-open-nudge-policy").ClickAsync();

        await page.GetByTestId("field-staleAfterMinutes").FillAsync("30");
        await page.GetByTestId("config-reason").FillAsync("e2e first write");
        await page.GetByTestId("config-save").ClickAsync();
        await Expect(page.GetByTestId("config-notice")).ToHaveTextAsync("stored v1");
        await Expect(page.GetByTestId("history-v1")).ToBeVisibleAsync();
        Assert.Equal(1L, (await fixture.Db.AdminConfigRow("nudge-policy"))!.Value.RowVersion);

        // A second writer lands while this form still holds ETag "1".
        await fixture.Db.AdminConfigConcurrentWrite(
            "nudge-policy", "{\"staleAfterMinutes\": 60}", "oidc:seed-bot", "concurrent tune");

        await page.GetByTestId("field-staleAfterMinutes").FillAsync("45");
        await page.GetByTestId("config-reason").FillAsync("e2e re-apply");
        await page.GetByTestId("config-save").ClickAsync();
        await Expect(page.GetByTestId("config-conflict")).ToContainTextAsync("changed by");

        // The reload-after-412 re-applies against the fresh ETag.
        await page.GetByTestId("config-save").ClickAsync();
        await Expect(page.GetByTestId("config-notice")).ToHaveTextAsync("stored v3");
        await Expect(page.GetByTestId("history-v3")).ToBeVisibleAsync();
        var stored = (await fixture.Db.AdminConfigRow("nudge-policy"))!.Value;
        Assert.Equal(3L, stored.RowVersion);
        using var doc = JsonDocument.Parse(stored.Payload);
        Assert.Equal(45, doc.RootElement.GetProperty("staleAfterMinutes").GetInt32());
    }

    [Fact]
    public async Task AnonymousVisitorSeesTheLoginGateOnly()
    {
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.ServerAddress);
        await Expect(page.GetByTestId("login-gate")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("login")).ToBeVisibleAsync();

        // The bootstrap document is anonymous (the SPA needs it to sign in).
        using var probe = new HttpClient();
        var config = await probe.GetFromJsonAsync<JsonElement>($"{host.ServerAddress}/ui-config");
        Assert.Equal(KeycloakFixture.Issuer, config.GetProperty("oidcAuthority").GetString());
    }

    // ---- plumbing ----------------------------------------------------------

    /// <summary>Hosted-Login flow: gate → Keycloak form (alice) → back to the SPA shell.</summary>
    private static async Task<IPage> NewLoginPageAsync(IBrowser browser, string baseUrl)
    {
        var context = await browser.NewContextAsync();
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

    /// <summary>Create a Work over the public API as the CLI principal (returns workId + version).</summary>
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

    /// <summary>Work + a pending approval alice is eligible for (display-name match).</summary>
    private async Task<(Guid WorkId, Guid ApprovalId)> SeedWorkWithApprovalAsync(KernelWebHostFixture host, string objective)
    {
        var (workId, _) = await CreateWorkAsync(host, objective);
        var approvalId = Guid.NewGuid();
        await fixture.Db.SeedApproval(approvalId, workId, "[\"user:alice\"]");
        return (workId, approvalId);
    }
}
