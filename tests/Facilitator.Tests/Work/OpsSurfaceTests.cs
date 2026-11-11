using System.Net;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Work;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Work;

/// <summary>
/// Operating the machine (step 6): schedules fire work + a team-visible notice
/// (A10, never a silent item); the queue read ages pending intents (D2/N28);
/// the execution read discloses the endpoint and its liveness on the work
/// itself (F1); the third failed attempt earns exactly one notice (F3); a
/// cancel discloses the effects it cannot undo (F4); the admin surface can
/// force every sweeper (I2) and export the audit trail (H3).
/// Joins <c>postgres:work</c>: schema-bound rows on the shared DB.
/// </summary>
[Collection("postgres:work")]
public sealed class OpsSurfaceTests(PostgresFixture fixture)
{
    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<string> FreshTeamAsync()
    {
        var slug = $"ops-w-{Guid.NewGuid():N}"[..24];
        await fixture.Db.SeedTeam(slug);
        return slug;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, string? body, string? reason)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = Json(body);
        }

        if (reason is not null)
        {
            request.Headers.Add("X-Change-Reason", reason);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task ScheduleFiringCreatesWorkAndAnnouncesItToTheTeam()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, fakeClock: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var scheduleId = Guid.NewGuid();
        var response = await SendAsync(
            kernel, HttpMethod.Put, $"/api/teams/{team}/schedules/{scheduleId}",
            JsonSerializer.Serialize(new
            {
                objective = "weekly dependency sweep",
                cadence = "interval:30m",
                ownerActor = "oidc:seed|owner",
            }),
            "planned for the test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Not yet due: a pass before the horizon fires nothing.
        var sweeper = factory.Services.GetRequiredService<ScheduleSweeper>();
        Assert.Equal(0, await sweeper.SweepOnceAsync(CancellationToken.None));

        factory.Clock!.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None));

        // A10: a work AND a team-visible notice naming the accountable owner.
        var works = await KernelOps.GetJsonAsync(
            kernel, $"/api/works?team={team}", HttpStatusCode.OK);
        var work = works.EnumerateArray().Single();
        Assert.Equal("weekly dependency sweep", work.GetProperty("objective").GetString());
        var workId = Guid.Parse(work.GetProperty("workId").GetString()!);
        Assert.Equal("oidc:seed|owner", work.GetProperty("ownerActor").GetString());
        // The ledger row exists with a team audience (in this test env no
        // team channel is configured, so its state honestly says why the
        // notice could not ride; what it cannot be is absent).
        var notices = await fixture.Db.NotificationsFor(workId);
        Assert.Contains(notices, n => n.Audience == "team");
        Assert.True(await fixture.Db.NotificationWithReason("weekly dependency sweep"));
        Assert.True(await fixture.Db.NotificationWithReason("oidc:seed|owner"));

        // The row advanced to the next slot and fired exactly once for the old one.
        Assert.Equal(0, await sweeper.SweepOnceAsync(CancellationToken.None));

        // A10 end of the silent-item law: disable it, the clock moves, nothing grows.
        await SendAsync(kernel, HttpMethod.Post, $"/api/teams/{team}/schedules/{scheduleId}/disable", "{}", "done");
        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(0, await sweeper.SweepOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MalformedCadenceIsRefusedAtTheSurface()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var response = await SendAsync(
            kernel, HttpMethod.Put, $"/api/teams/{team}/schedules/{Guid.NewGuid()}",
            JsonSerializer.Serialize(new { objective = "x", cadence = "whenever" }), "typo");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task QueueReadAgesThePendingIntents()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var projectId = await fixture.Db.SeedProject(team);
        var workId = await fixture.Db.SeedWorkOwned(projectId, null, "planning");
        await KernelOps.PostJsonAsync(kernel, $"/api/works/{workId}/redispatch",
            JsonSerializer.Serialize(new
            {
                capabilityId = "ops.ghost",
                commandId = "ops-q-" + Guid.NewGuid().ToString("N"),
            }),
            HttpStatusCode.Accepted);

        var queue = await KernelOps.GetJsonAsync(kernel, $"/api/queue?team={team}", HttpStatusCode.OK);
        var item = queue.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("workId").GetString() == workId.ToString("D"));
        Assert.True(item.GetProperty("ageSeconds").GetInt64() >= 0);
        Assert.True(queue.GetProperty("oldestWaitSeconds").GetDouble() >= 0);

        // The filter is honest: another team's view never shows this work.
        var other = await FreshTeamAsync();
        var filtered = await KernelOps.GetJsonAsync(kernel, $"/api/queue?team={other}", HttpStatusCode.OK);
        Assert.DoesNotContain(filtered.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("workId").GetString() == workId.ToString("D"));
    }

    [Fact]
    public async Task ExecutionReadDisclosesEndpointAndLiveness()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var projectId = await fixture.Db.SeedProject(team);
        var workId = await fixture.Db.SeedWorkOwned(projectId, null, "active");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "routed", capabilityId: "ops.render");

        // The funnel's own timeline row is the source: it names the endpoint.
        await fixture.Db.SeedTimelineRow(workId, "assignment.transition", JsonSerializer.Serialize(new
        {
            assignmentId = assignmentId.ToString(),
            endpointId = "wkr-ops-1",
            from = "proposed",
            to = "routed",
        }));
        await fixture.Db.SeedAttemptRow(Guid.NewGuid(), assignmentId, 1, "working", DateTime.UtcNow);

        var lines = await KernelOps.GetJsonAsync(kernel, $"/api/works/{workId}/executions", HttpStatusCode.OK);
        var line = Assert.Single(lines.EnumerateArray());
        Assert.Equal("wkr-ops-1", line.GetProperty("endpointId").GetString());
        Assert.Equal(1, line.GetProperty("attempts").GetInt32());

        // F1: the endpoint has no live lease -> the work itself says offline.
        Assert.False(line.GetProperty("endpointOnline").GetBoolean());

        await fixture.Db.SeedGatewayEnrollment("wkr-ops-1", team, "isolated-executor");
        await fixture.Db.SeedLinkSession("wkr-ops-1", 1, DateTime.UtcNow.AddMinutes(15));
        lines = await KernelOps.GetJsonAsync(kernel, $"/api/works/{workId}/executions", HttpStatusCode.OK);
        Assert.True(Assert.Single(lines.EnumerateArray()).GetProperty("endpointOnline").GetBoolean());
    }

    [Fact]
    public async Task CancelDisclosesIrreversibleEffects()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var projectId = await fixture.Db.SeedProject(team);
        var workId = await fixture.Db.SeedWorkOwned(projectId, null, "active");
        var otherWork = await fixture.Db.SeedWorkOwned(projectId, null, "completed");
        await fixture.Db.SeedWorkLink(workId, otherWork, "successor");

        await KernelOps.PostJsonAsync(kernel, $"/api/works/{workId}/transitions",
            JsonSerializer.Serialize(new { to = "cancelled", expectedVersion = 1, actor = "test", reason = "abandoned" }),
            HttpStatusCode.OK);
        var details = await fixture.Db.TimelineDetail(workId, "effects-disclosed");
        var disclosure = Assert.Single(details.Select(d => JsonDocument.Parse(d).RootElement.Clone()));
        Assert.Equal("successor", disclosure.GetProperty("links")[0].GetProperty("kind").GetString());

        // A cancel with nothing produced appends nothing (no noise law).
        var quiet = await fixture.Db.SeedWorkOwned(projectId, null, "active");
        await KernelOps.PostJsonAsync(kernel, $"/api/works/{quiet}/transitions",
            JsonSerializer.Serialize(new { to = "cancelled", expectedVersion = 1, actor = "test", reason = "quiet" }),
            HttpStatusCode.OK);
        Assert.Empty(await fixture.Db.TimelineDetail(quiet, "effects-disclosed"));
    }

    [Fact]
    public async Task GatewayQueueReasonRendersOnTheExecutionRead()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var projectId = await fixture.Db.SeedProject(team);
        var workId = await fixture.Db.SeedWorkOwned(projectId, null, "active");
        var assignmentId = await fixture.Db.SeedTeamAssignment(workId, team, "proposed");

        var token = await LinkTestHarness.EnrollAsync(kernel, "gw.ops-report-" + Guid.NewGuid().ToString("N")[..8], team: team);
        using var gw = factory.CreateLinkClient(token);
        using var reported = await gw.PostAsync(
            $"/api/link/v1/assignments/{assignmentId}/queue-reason",
            Json(JsonSerializer.Serialize(new { queueReason = "waiting-for-capacity" })));
        Assert.Equal(HttpStatusCode.NoContent, reported.StatusCode);

        var lines = await KernelOps.GetJsonAsync(kernel, $"/api/works/{workId}/executions", HttpStatusCode.OK);
        var line = Assert.Single(lines.EnumerateArray());

        // D2/P13: the advisory reason renders distinctly - the status stays
        // honest (`proposed`), the observation rides beside it.
        Assert.Equal("waiting-for-capacity", line.GetProperty("queueReason").GetString());
        Assert.Equal("proposed", line.GetProperty("status").GetString());
    }

    [Fact]
    public async Task EverySweeperAnswersToTheAdminTrigger()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        foreach (var name in new[] { "nudge", "approvals", "schedules", "session-loss" })
        {
            await KernelOps.PostJsonAsync(kernel, $"/api/admin/sweeps/{name}", "{}", HttpStatusCode.OK);
        }

        using var unknown = await kernel.PostAsync("/api/admin/sweeps/whatever", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task AuditRetentionPrunesOnlyAgedRows()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        await fixture.Db.AdminConfigPurge("audit-policy");
        await fixture.Db.SeedAdminConfig("audit-policy", "{\"retentionDays\": 30}");

        // Test-only actions: aging them never disturbs a suite reading real
        // gate decisions in parallel. One row inside, one outside the window.
        var aged = "ops.retention." + Guid.NewGuid().ToString("N");
        var fresh = "ops.retention." + Guid.NewGuid().ToString("N");
        await fixture.Db.SeedAuditRow(aged, ageDays: 60);
        await fixture.Db.SeedAuditRow(fresh, ageDays: 0);
        var pruned = await KernelOps.PostJsonAsync(kernel, "/api/admin/sweeps/audit-retention", "{}", HttpStatusCode.OK);
        Assert.True(pruned.GetProperty("effects").GetInt32() >= 1);
        Assert.True(await fixture.Db.AuditActionMissing(aged));
        Assert.False(await fixture.Db.AuditActionMissing(fresh));
        await fixture.Db.AdminConfigPurge("audit-policy");
    }

    [Fact]
    public async Task AuditExportCoversTheWindowInBothFormats()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();

        // One audit row of a known action: a schedule write.
        await SendAsync(kernel, HttpMethod.Put, $"/api/teams/{team}/schedules/{Guid.NewGuid()}",
            JsonSerializer.Serialize(new { objective = "export me", cadence = "daily:07:00" }), "export test");

        using var export = await kernel.GetAsync(
            $"/api/admin/audit/export?from={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-5).ToString("s")) + "Z"}");
        if (export.StatusCode != HttpStatusCode.OK)
        {
            Assert.Fail(await export.Content.ReadAsStringAsync());
        }

        var json = JsonDocument.Parse(await export.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(json.EnumerateArray(),
            r => r.GetProperty("action").GetString()!.StartsWith("authorization.evaluated", StringComparison.Ordinal));

        using var csv = await kernel.GetAsync("/api/admin/audit/export?format=csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var body = await csv.Content.ReadAsStringAsync();
        Assert.StartsWith("occurred_at,actor,action,resource,decision,reason", body);
    }

    private sealed class RefusingKernelSink : ISink
    {
        public string Channel => "kernel";

        public Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task ThirdFailureSaysSoExactlyOnce()
    {
        using var factory = new KernelTestFactory(
            fixture, noDispatcher: true, _ => new RefusingKernelSink());
        using var kernel = factory.CreateAuthedClient();
        await fixture.Db.ExpireLinkLeases();
        var team = await FreshTeamAsync();
        var projectId = await fixture.Db.SeedProject(team);

        // Every work write appends a kernel.log progress row: the poison
        // burns its charged attempts and the third one speaks up.
        var created = await KernelOps.CreateWorkAsync(kernel, "f3 subject", projectId);
        var workId = created.WorkId;

        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            await fixture.Db.ExpireOutboxBackoff(workId);
        }

        // F3: exactly one notice at the third charged attempt, audience team
        // (the work's own team carries it).
        var rows = await fixture.Db.NotificationsWithText("has now failed 3 times", workId);
        Assert.Single(rows);
        Assert.Equal("team", Assert.Single(rows).Audience);

        // The once-law: the deterministic command id fences any later pass.
        await dispatcher.DrainOnceAsync(CancellationToken.None);
        await fixture.Db.ExpireOutboxBackoff(workId);
        await dispatcher.DrainOnceAsync(CancellationToken.None);
        Assert.Single(await fixture.Db.NotificationsWithText("has now failed 3 times", workId));
    }
}
