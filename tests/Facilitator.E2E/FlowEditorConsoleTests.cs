using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Facilitator.Tests.Infrastructure;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Facilitator.E2E;

/// <summary>
/// Browser tier for the flow editor: clone a gallery entry through the UI,
/// edit it, turn it on, and watch the version law hold — a stored doc is
/// immutable, every save lands a new version, and running instances keep
/// the version they started on. The second leg proves the YAML face
/// round-trips an edited doc through the server's own renderer.
/// Sequential on the shared fixture pair, like the read-side walkthrough.
/// </summary>
[Collection("e2e")]
public sealed class FlowEditorConsoleTests(PostgresFixture fixture)
{
    // Set OFAC_E2E_SHOTS=<dir> to photograph the editor states into
    // <dir>/console-editor.png and <dir>/console-instance.png — the site's
    // screenshots stay honest, produced by this very walkthrough.
    private static string? ShotDir => Environment.GetEnvironmentVariable("OFAC_E2E_SHOTS");

    private static async Task ShootAsync(IPage page, string name)
    {
        if (ShotDir is { } dir)
        {
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name) });
        }
    }

    [Fact]
    public async Task CloneEditAndEnableBumpsVersions_AndRunsPinTheirVersion()
    {
        using var host = new KernelWebHostFixture(
            fixture,
            settings: new Dictionary<string, string?> { ["Facilitator:Workflows:Enabled"] = "true" });

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(
            browser,
            host.ServerAddress,
            ShotDir is null ? null : new ViewportSize { Width = 1440, Height = 900 });

        // Clone the nudge-escalation gallery entry through the flows table.
        await page.GetByTestId("view-flows").ClickAsync();
        await Expect(page.GetByTestId("flows-view")).ToBeVisibleAsync();
        var row = page.Locator("[data-testid='flows-view'] tbody tr", new() { HasText = "nudge-escalation" });
        await Expect(row).ToBeVisibleAsync();
        await row.GetByText("clone", new() { Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("flow-editor")).ToBeVisibleAsync();

        // The clone renames to -copy and the picture drew both timers.
        var idField = page.GetByTestId("editor-id");
        await Expect(idField).ToHaveValueAsync("nudge-escalation-copy");
        var title = page.GetByTestId("editor-title");
        await Expect(title).ToHaveValueAsync("Nudge escalation ladder (documentation entry) (copy)");

        // Edit the title and the first pause's timer, then store v1.
        await title.FillAsync("Nudge escalation (tuned)");
        await page.GetByTestId("flow-graph").IsVisibleAsync();
        await Expect(page.GetByTestId("editor-diff")).ToContainTextAsync("title");
        await ShootAsync(page, "console-editor.png");
        await page.GetByTestId("editor-save").ClickAsync();
        await Expect(page.GetByTestId("editor-notice")).ToContainTextAsync("version 1");

        // The copy now lives under its own editor route; enable it.
        await Expect(page.GetByTestId("editor-enable")).ToBeVisibleAsync();
        await page.GetByTestId("editor-enable").ClickAsync();
        await Expect(page.GetByTestId("editor-notice")).ToContainTextAsync("enabled");
        await page.GoBackAsync();
        await Expect(page.GetByTestId("flows-view")).ToBeVisibleAsync();
        var copyRow = page.Locator("[data-testid='flows-view'] tbody tr", new() { HasText = "nudge-escalation-copy1" });
        await Expect(copyRow).ToContainTextAsync("live");

        // One work.progress event: an instance of v1, parked on a timer.
        await PostWorkProgressAsync(host, "e2e-editor-a");
        // The trigger row lands in its own transaction and the instance is
        // born on the dispatch path: refresh until the run shows, not sleep.
        var parked = page.Locator("[data-testid='instance-list'] tbody tr", new() { HasText = "nudge-escalation-copy@1" });
        await page.GetByTestId("flows-refresh").ClickAsync();
        await Expect(parked).ToBeVisibleAsync(new() { Timeout = 15_000 });
        if (ShotDir is not null)
        {
            // Photograph the run as a live walk: the graph wears the
            // instance's own state, so shoot the detail open.
            await parked.ClickAsync();
            await Expect(page.GetByTestId("flow-detail")).ToBeVisibleAsync();
            await Expect(page.GetByTestId("flow-graph")).ToBeVisibleAsync();
            await ShootAsync(page, "console-instance.png");
            await page.GoBackAsync();
        }

        // Edit again through the UI: v2 lands, and the OLD instance keeps
        // its v1 pin — a stored version is immutable under a running run.
        await copyRow.GetByText("edit", new() { Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("flow-editor")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("editor-title")).ToHaveValueAsync("Nudge escalation (tuned)");
        await page.GetByTestId("editor-title").FillAsync("Nudge escalation (retuned)");
        await page.GetByTestId("editor-save").ClickAsync();
        await Expect(page.GetByTestId("editor-notice")).ToContainTextAsync("version 2");
        await page.GoBackAsync();
        await Expect(
            page.Locator("[data-testid='flows-view'] tbody tr", new() { HasText = "nudge-escalation-copy2" }))
            .ToContainTextAsync("live");

        await PostWorkProgressAsync(host, "e2e-editor-b");
        await page.ReloadAsync();
        await page.GetByTestId("flows-refresh").ClickAsync();
        await Expect(
            page.Locator("[data-testid='instance-list'] tbody tr", new() { HasText = "nudge-escalation-copy@2" }))
            .ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(
            page.Locator("[data-testid='instance-list'] tbody tr", new() { HasText = "nudge-escalation-copy@1" }))
            .ToBeVisibleAsync();

        // Audit law: both stores and the enable are on the record.
        using (var client = host.CreateSeedClient())
        {
            var audit = await client.GetStringAsync("/api/admin/audit?action=admin.workflows.put");
            using var doc = JsonDocument.Parse(audit);
            var resources = doc.RootElement.EnumerateArray()
                .Select(e => e.GetProperty("resource").GetString() ?? "")
                .ToList();
            Assert.Contains("/api/admin/workflows/definitions/nudge-escalation-copy", resources);
            var toggles = await client.GetStringAsync("/api/admin/audit?action=admin.workflows.toggle");
            using var toggleDoc = JsonDocument.Parse(toggles);
            Assert.NotEmpty(toggleDoc.RootElement.EnumerateArray());
        }
    }

    [Fact]
    public async Task InvokeStepEdit_RoundTripsThroughYaml()
    {
        using var host = new KernelWebHostFixture(
            fixture,
            settings: new Dictionary<string, string?> { ["Facilitator:Workflows:Enabled"] = "true" });

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(browser, host.ServerAddress);

        // Open the agentic-research gallery entry (its 'research' step is
        // an invoke with a PT20M child timeout) and go straight to YAML.
        await page.GotoAsync($"{host.ServerAddress}/#/flowdef/agentic-research");
        await Expect(page.GetByTestId("flow-editor")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "YAML" }).ClickAsync();
        var yaml = page.GetByTestId("editor-yaml");
        // A bound textarea speaks through its value, not its text content.
        await Expect(yaml).ToHaveValueAsync(new Regex("PT20M"));

        // Edit the child timeout in YAML, validate, save.
        var text = await yaml.InputValueAsync();
        await yaml.FillAsync(text.Replace("PT20M", "PT25M"));
        await page.GetByTestId("yaml-validate").ClickAsync();
        await Expect(page.GetByTestId("yaml-validation")).ToContainTextAsync("valid");
        await page.GetByTestId("yaml-save").ClickAsync();
        await Expect(page.GetByTestId("editor-notice")).ToContainTextAsync("version 2");

        // The server's own YAML face now speaks PT25M: the edited doc
        // round-tripped YAML -> store -> YAML.
        await Expect(page.GetByTestId("editor-yaml")).ToHaveValueAsync(new Regex("PT25M"));

        using var client = host.CreateSeedClient();
        var stored = await client.GetStringAsync("/api/admin/workflows/definitions/agentic-research");
        Assert.Contains("PT25M", stored);
    }

    private static async Task<string> PostWorkProgressAsync(KernelWebHostFixture host, string tag)
    {
        using var client = host.CreateSeedClient();
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.work.progress.json"))!.AsObject();
        var id = $"e2e-editor-{tag}-{Guid.NewGuid():N}";
        envelope["id"] = id;
        var posted = await client.PostAsync(
            "/api/ingress/events",
            new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.True(
            posted.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
            $"ingress rejected: {(int)posted.StatusCode}");
        return id;
    }

    private static async Task<IPage> NewLoginPageAsync(
        IBrowser browser, string baseUrl, ViewportSize? viewport = null)
    {
        var context = await browser.NewContextAsync(
            viewport is null ? null : new BrowserNewContextOptions { ViewportSize = viewport });
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
}
