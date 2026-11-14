using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Microsoft.Playwright;

using static Microsoft.Playwright.Assertions;

namespace Facilitator.E2E;

/// <summary>
/// Browser tier for the workflow plane's read surface (the flows console):
/// a stored definition fires through the sweep, and the console shows the
/// instance, its vertical checklist, and the lineage shell. Sequential on
/// the shared fixture pair, like the inspector walkthrough.
/// </summary>
[Collection("e2e")]
public sealed class FlowsConsoleTests(PostgresFixture fixture)
{
    private static string FlowDef(string id) => $$"""
        {
          "id": "{{id}}", "version": 1, "title": "e2e flow",
          "description": "one projection step, event-triggered",
          "enabled": true,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "note", "title": "project the input", "on": "always",
              "select": { "project": { "topic": "input.repo" } } }
          ]
        }
        """;

    [Fact]
    public async Task FlowsViewListsDefinitionsAndAnOpenedRunShowsItsChecklist()
    {
        using var host = new KernelWebHostFixture(fixture, noDispatcher: true);
        var defId = $"t.e2e-flows-{Guid.NewGuid():N}"[..40];

        // Store + enable the definition over the admin surface (change-reason law).
        using (var client = host.CreateSeedClient())
        {
            client.DefaultRequestHeaders.Add("X-Change-Reason", "e2e flows console");
            client.DefaultRequestHeaders.IfMatch.Add(EntityTagHeaderValue.Any);
            // (the same reason header covers the sweep below)
            var stored = await client.PutAsync(
                $"/api/admin/workflows/definitions/{defId}",
                new StringContent(FlowDef(defId), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, stored.StatusCode);

            // One issue event rides the plane's trigger row...
            var issue = JsonDocument.Parse(
                    """
                    {"specversion":"1.0","id":"e2e-evt-1","source":"/e2e","type":"org.facilitator.event.provider.issue.v1",
                     "time":"2026-11-14T09:00:00Z","datacontenttype":"application/json",
                     "data":{"repo":"demo/flows","issue":"1","state":"open","workTrigger":true}}
                    """)
                .RootElement.Clone();
            var posted = await client.PostAsync(
                "/api/ingress/events",
                new StringContent(issue.GetRawText(), Encoding.UTF8, "application/json"));
            Assert.True(
                posted.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK,
                $"ingress rejected: {(int)posted.StatusCode}");

            // ...and the manual sweep runs the instance to ground.
            var swept = await client.PostAsync("/api/admin/sweeps/workflows", new StringContent(""));
            swept.EnsureSuccessStatusCode();
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await NewLoginPageAsync(browser, host.ServerAddress);

        await page.GetByTestId("view-flows").ClickAsync();
        await Expect(page.GetByTestId("flows-view")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("flows-view")).ToContainTextAsync(defId);
        await Expect(page.GetByTestId("flows-view")).ToContainTextAsync("live");

        // The instance row: one run of this definition, completed at 'note'.
        var row = page.Locator("[data-testid='instance-list'] tbody tr", new() { HasText = defId });
        await Expect(row).ToBeVisibleAsync();
        await Expect(row).ToContainTextAsync("completed");
        await row.ClickAsync();

        await Expect(page.GetByTestId("flow-detail")).ToBeVisibleAsync();
        await Expect(page.GetByTestId("step-log")).ToContainTextAsync("note");
        await Expect(page.GetByTestId("step-log")).ToContainTextAsync("completed");

        // Deep links survive a reload: the route is the state.
        await page.ReloadAsync();
        await Expect(page.GetByTestId("flow-detail")).ToBeVisibleAsync();
    }

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
}
