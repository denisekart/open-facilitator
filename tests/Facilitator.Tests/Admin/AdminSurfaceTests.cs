using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Admin;

/// <summary>
/// Plan 20 (spec §17): the <c>/api/admin/*</c> operator surface. Read models
/// over seeded ledgers, the dead-letter replay round-trip (byte-identical
/// envelope re-dispatch + replay-once fence), manifest re-mark, and the
/// config-driven authz matrix. Joins <c>postgres:dispatch-link</c>: the
/// replay test dead-letters real outbox rows, so no foreign dispatcher may
/// race the poison drain.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class AdminSurfaceTests(DispatchDbFixture fixture)
{
    private sealed class PoisonSink : ISink
    {
        public string Channel => "kernel";

        public Task<bool> DeliverAsync(OutboxRow row, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task SummaryCountsEverySeededLedgerSection()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var open = await fixture.Db.SeedWork("open");
        await fixture.Db.SeedWork("completed");
        await fixture.Db.SeedApproval(Guid.NewGuid(), open);
        await fixture.Db.SeedGatewayEnrollment("gw.surface-online", "team-alpha", "shared-runtime");
        await fixture.Db.SeedLinkSession("gw.surface-online", 7, DateTime.UtcNow.AddMinutes(5));

        var summary = await KernelOps.GetJsonAsync(client, "/api/admin/summary", HttpStatusCode.OK);
        Assert.True(summary.GetProperty("workByState").GetProperty("open").GetInt32() >= 1);
        Assert.True(summary.GetProperty("workByState").GetProperty("completed").GetInt32() >= 1);
        Assert.True(summary.GetProperty("approvalsByState").GetProperty("pending").GetInt32() >= 1);
        Assert.True(summary.GetProperty("linkSessionsLive").GetInt32() >= 1);
        Assert.True(summary.GetProperty("gatewaysOnline").GetInt32() >= 1);
        foreach (var section in new[] { "teams", "projects", "deadLettersTotal", "artifactsTotal" })
        {
            Assert.True(summary.TryGetProperty(section, out _), $"summary carries '{section}'");
        }
    }

    [Fact]
    public async Task SearchHitsWorksByObjectiveAndRequiresQ()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var objective = $"surface-search-{Guid.NewGuid():N}";
        var (workId, _) = await KernelOps.CreateWorkAsync(client, objective);

        var hits = await KernelOps.GetJsonAsync(client, "/api/admin/search?q=surface-search", HttpStatusCode.OK);
        var works = hits.GetProperty("works").EnumerateArray()
            .Single(w => w.GetProperty("id").GetString() == workId.ToString());
        Assert.Equal(objective, works.GetProperty("display").GetString());
        Assert.Equal("open", works.GetProperty("state").GetString());

        var missing = await client.GetAsync("/api/admin/search");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("search-query-required", await missing.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WorkDetailCarriesAssignmentsApprovalsManifests()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "surface detail");
        await fixture.Db.SeedAssignmentForTeam(workId, "team-alpha", "running");
        var assignmentId = Guid.Parse(Assert.Single(await fixture.Db.AssignmentIdsForWork(workId)));
        await fixture.Db.SeedExecutionAttempt(assignmentId, "working");
        var approvalId = Guid.NewGuid();
        await fixture.Db.SeedApproval(approvalId, workId);
        await fixture.Db.SeedResultManifest($"man-{Guid.NewGuid():N}", workId, Guid.NewGuid(), [], "recorded", "available");

        var detail = await KernelOps.GetJsonAsync(client, $"/api/admin/work/{workId}", HttpStatusCode.OK);
        Assert.Equal(workId.ToString(), detail.GetProperty("work").GetProperty("workId").GetString());
        var assignment = detail.GetProperty("assignments").EnumerateArray()
            .Single(a => a.GetProperty("assignmentId").GetString() == assignmentId.ToString());
        Assert.Equal("working", Assert.Single(assignment.GetProperty("attempts").EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal("pending", detail.GetProperty("approvals").EnumerateArray()
            .Single(a => a.GetProperty("approvalId").GetString() == approvalId.ToString()).GetProperty("state").GetString());
        Assert.Equal("available", Assert.Single(detail.GetProperty("manifests").EnumerateArray()).GetProperty("resultState").GetString());

        var gone = await client.GetAsync($"/api/admin/work/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("work-not-found", await gone.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AdminTimelineMatchesWorkTimelineVerbatim()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, version) = await KernelOps.CreateWorkAsync(client, "timeline parity");
        await KernelOps.TransitionAsync(client, workId, "planning", version);

        var admin = await KernelOps.GetJsonAsync(client, $"/api/admin/work/{workId}/timeline", HttpStatusCode.OK);
        var work = await KernelOps.GetJsonAsync(client, $"/api/works/{workId}/timeline", HttpStatusCode.OK);
        Assert.Equal(work.GetRawText(), admin.GetRawText());
    }

    [Fact]
    public async Task LinksViewSplitsOnlineOfflineAndFlagsAffectedAssignments()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        await fixture.Db.SeedGatewayEnrollment("gw.surface-live", "team-alpha", "shared-runtime");
        await fixture.Db.SeedLinkSession("gw.surface-live", 3, DateTime.UtcNow.AddMinutes(5));
        await fixture.Db.SeedGatewayEnrollment("gw.surface-dark", "team-beta", "isolated-executor");
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "affected probe");
        await fixture.Db.SeedAssignmentForTeam(workId, "team-beta", "running");
        var assignmentId = Guid.Parse(Assert.Single(await fixture.Db.AssignmentIdsForWork(workId)));

        var view = await KernelOps.GetJsonAsync(client, "/api/admin/links", HttpStatusCode.OK);
        var live = view.GetProperty("gateways").EnumerateArray()
            .Single(g => g.GetProperty("endpointId").GetString() == "gw.surface-live");
        Assert.True(live.GetProperty("online").GetBoolean());
        Assert.Equal(3, live.GetProperty("sessionEpoch").GetInt64());
        Assert.Equal("shared-runtime", live.GetProperty("trustMode").GetString());
        var dark = view.GetProperty("gateways").EnumerateArray()
            .Single(g => g.GetProperty("endpointId").GetString() == "gw.surface-dark");
        Assert.False(dark.GetProperty("online").GetBoolean());
        Assert.Contains(view.GetProperty("affectedAssignments").EnumerateArray(),
            a => a.GetProperty("assignmentId").GetString() == assignmentId.ToString()
                && a.GetProperty("workId").GetString() == workId.ToString());
    }

    [Fact]
    public async Task DeliveriesMergeKernelPlaneRowsNewestFirst()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "delivery merge");

        var mineIds = await fixture.Db.OutboxIdTexts(workId);
        var list = await KernelOps.GetJsonAsync(client, "/api/admin/deliveries?limit=200", HttpStatusCode.OK);
        var mine = list.GetProperty("items").EnumerateArray()
            .Where(d => mineIds.Contains(d.GetProperty("id").GetString()!)).ToArray();
        Assert.True(mine.Length >= 1, "the work's lifecycle events surface on the merged view");
        foreach (var row in mine)
        {
            Assert.Equal("kernel", row.GetProperty("plane").GetString());
        }
    }

    [Fact]
    public async Task MetricsGaugesReflectSeededState()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        await fixture.Db.SeedGatewayEnrollment("gw.surface-metrics", "team-alpha", "isolated-executor");
        await fixture.Db.SeedLinkSession("gw.surface-metrics", 1, DateTime.UtcNow.AddMinutes(5));

        var metrics = await KernelOps.GetJsonAsync(client, "/api/admin/metrics?window=24h", HttpStatusCode.OK);
        Assert.True(metrics.GetProperty("linkSessions").GetInt64() >= 1);
        Assert.True(metrics.GetProperty("counters").EnumerateObject().Any(), "process counters are exposed");
        Assert.Equal(JsonValueKind.Array, metrics.GetProperty("deadLettersPerHour").ValueKind);
    }

    [Fact]
    public async Task ReplayReDispatchesTheByteIdenticalSnapshotOnceAndAudits()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, _ => new PoisonSink());
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "replay round trip");
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            await fixture.Db.ExpireOutboxBackoff(workId);
        }

        var deadLetterId = Guid.Parse(Assert.Single(await fixture.Db.DeadLetterIdsForWork(workId)));
        var snapshot = await fixture.Db.DeadLetterSnapshot(deadLetterId);

        var replayed = await KernelOps.PostJsonAsync(client, $"/api/admin/dead-letters/{deadLetterId}/replay", "{}", HttpStatusCode.OK);
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        var commandId = $"replay:{deadLetterId}";
        Assert.True(await fixture.Db.OutboxRowByCommand(commandId, "pending"), "replay row is a fresh pending dispatch");
        Assert.Equal(snapshot, await fixture.Db.OutboxEnvelopeByCommand(commandId));

        // replay-once law: the commandId fence rejects the second attempt.
        var twice = await client.PostAsync($"/api/admin/dead-letters/{deadLetterId}/replay",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Contains("version-conflict", await twice.Content.ReadAsStringAsync());

        var audits = (await fixture.Db.AuditRows(decision: "allow"))
            .Where(a => a.Action == "admin.dead-letter.replay").ToArray();
        Assert.Single(audits);
        Assert.StartsWith("oidc:", audits[0].Actor);

        var gone = await client.PostAsync($"/api/admin/dead-letters/{Guid.NewGuid()}/replay",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("dead-letter-not-found", await gone.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReplayRejectsContractInvalidSnapshotWithoutWriting()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, _ => new PoisonSink());
        using var client = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(client, "replay fail closed");
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            await fixture.Db.ExpireOutboxBackoff(workId);
        }
        var deadLetterId = Guid.Parse(Assert.Single(await fixture.Db.DeadLetterIdsForWork(workId)));
        await fixture.Db.SetDeadLetterEnvelope(deadLetterId, "{\"not\":\"a cloudevent\"}");

        var response = await client.PostAsync($"/api/admin/dead-letters/{deadLetterId}/replay",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("replay-envelope-invalid", await response.Content.ReadAsStringAsync());
        Assert.False(await fixture.Db.OutboxRowByCommand($"replay:{deadLetterId}"), "a rejected replay writes nothing");
    }

    [Fact]
    public async Task ManifestReMarkFlipsResultStateOnly()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var client = factory.CreateAuthedClient();
        var workId = await fixture.Db.SeedWork("review");
        var manifestId = $"man-{Guid.NewGuid():N}";
        await fixture.Db.SeedResultManifest(manifestId, workId, Guid.NewGuid(), [], "recorded", "available");

        var marked = await KernelOps.PostJsonAsync(client, $"/api/admin/manifests/{manifestId}/re-mark",
            """{"resultState": "validated"}""", HttpStatusCode.OK);
        Assert.Equal("validated", marked.GetProperty("resultState").GetString());
        Assert.Equal("validated", await fixture.Db.ManifestResultState(manifestId));
        var audits = (await fixture.Db.AuditRows(decision: "allow"))
            .Where(a => a.Action == "admin.manifest.re-mark").ToArray();
        Assert.Single(audits);

        var chainOwned = await client.PostAsync($"/api/admin/manifests/{manifestId}/re-mark",
            new StringContent("""{"resultState": "missing"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, chainOwned.StatusCode);
        Assert.Contains("manifest-re-mark-invalid", await chainOwned.Content.ReadAsStringAsync());

        var gone = await client.PostAsync("/api/admin/manifests/man-unknown/re-mark",
            new StringContent("""{"resultState": "validated"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("manifest-not-found", await gone.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AuthzMatrixGatesTheSurfaceByRole()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);

        using var anonymous = factory.CreateClient();
        using (var anon = await anonymous.GetAsync("/api/admin/summary"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        }

        using (var member = await (await CreateUserClientAsync(factory, "bob")).GetAsync("/api/admin/summary"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
        }

        using var admin = await CreateUserClientAsync(factory, "alice");
        using (var read = await admin.GetAsync("/api/admin/summary"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        using var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/admin/config/notifications")
        {
            Content = new StringContent("""{"replyVsReaction": "reply", "progressVerbosity": "normal"}""", Encoding.UTF8, "application/json"),
        };
        patch.Headers.IfMatch.Add(new EntityTagHeaderValue("\"424242\""));
        patch.Headers.Add("X-Change-Reason", "matrix probe");
        using (var mutate = await admin.SendAsync(patch))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, mutate.StatusCode);
        }

        var denies = (await fixture.Db.AuditRows(decision: "deny"))
            .Where(a => a.Action == "authorization.evaluated:admin.read").ToArray();
        Assert.True(denies.Length >= 1, "the 403 wrote its audit row");
    }

    private static async Task<HttpClient> CreateUserClientAsync(KernelTestFactory factory, string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            await KeycloakFixture.TokenForUserAsync(user, user));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
