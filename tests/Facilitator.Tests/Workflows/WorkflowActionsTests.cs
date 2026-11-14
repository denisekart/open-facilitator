using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Workflows;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.Tests.Workflows;

/// <summary>
/// Phase 8 (operator actions): pause freezes a parked run and resume
/// restores the exact posture (deadline intact, next wake takes it); skip
/// closes the current leg as 'skipped' and walks the completed edge; retry
/// re-enters a named leg of a FAILED run with a fresh leg seq and drives it.
/// Every action is fenced (wrong-state = 409, never corruption) and every
/// one lands an audit row — allow AND deny. Joins
/// <c>postgres:persistence</c>.
/// </summary>
[Collection("postgres:persistence")]
public sealed class WorkflowActionsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private KernelTestFactory _factory = null!;
    private HttpClient _kernel = null!;
    private IEventDispatcher _dispatcher = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        _factory = new KernelTestFactory(fixture, noDispatcher: true);
        _factory.Services.GetRequiredService<WorkflowRuntime>().Enabled = true;
        _kernel = _factory.CreateAuthedClient(); // boots migrations
        _dispatcher = _factory.Services.GetRequiredService<IEventDispatcher>();
        await PurgeAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _kernel.Dispose();
        _factory.Dispose();
        await PurgeAsync();
    }

    // ---- definitions ---------------------------------------------------------

    // Parks on a wait event: the pause/resume subject.
    private static string ParkDef() => """
        {
          "id": "t.act-park", "version": 1, "title": "park",
          "description": "waits for a heartbeat",
          "enabled": true,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "hold", "title": "park", "on": "always",
              "wait": { "event": "org.facilitator.event.workspace.heartbeat.v1",
                        "correlate": "act-1", "timeout": "PT30M", "onTimeout": "end" } }
          ]
        }
        """;

    // select (lands), timer (parks — the pause/skip subject), select: the
    // skipped leg's completed edge leads to 'three'.
    private static string TimerDef() => """
        {
          "id": "t.act-two", "version": 1, "title": "select, hold, select",
          "description": "parks on a timer between two projections",
          "enabled": true,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "one", "title": "first", "on": "always",
              "select": { "project": { "a": "instance" } } },
            { "id": "two", "title": "hold", "on": "one:completed", "timer": { "for": "PT30M" } },
            { "id": "three", "title": "third", "on": "two:completed",
              "select": { "project": { "b": "instance" } } }
          ]
        }
        """;

    // Invokes a callable: the leg fails while the target is disabled
    // (target-unavailable) — the retry subject.
    private static string InvokeMissingDef() => """
        {
          "id": "t.act-invoke", "version": 1, "title": "invoke a ghost",
          "description": "invokes a callable that is offline at first",
          "enabled": true,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "call", "title": "invoke the ghost", "on": "always",
              "invoke": { "workflow": "t.act-ghost", "await": true, "timeout": "PT30M" } },
            { "id": "tail", "title": "project the input", "on": "call:completed",
              "select": { "project": { "repo": "$event.data.reference.channelOrResourceId" } } }
          ]
        }
        """;

    private async Task ToggleAsync(string id, bool enabled) =>
        await KernelOps.PostJsonAsync(
            _kernel,
            $"/api/admin/workflows/definitions/{id}/versions/1/toggle",
            $$"""{"enabled": {{(enabled ? "true" : "false")}}}""",
            HttpStatusCode.OK,
            "phase 8 actions test");

    // ---- scenarios ------------------------------------------------------------

    [Fact]
    public async Task PauseFreezesThePark_ResumeRestoresIt_AndTheAuditTrailTellsBothTales()
    {
        await StoreAsync(ParkDef());
        await PostIssueAsync();
        var instance = await WaitSingleInstanceAsync("t.act-park", "waiting");

        await ActAsync("pause", instance, HttpStatusCode.OK);
        Assert.Equal("paused", await StatusAsync("t.act-park"));

        // A wake for a paused park resolves as a no-op fall-through (the
        // conditional UPDATE finds no 'waiting' row): the run stays paused.
        await PostMentionAsync();
        await DrainAsync(async () => await InstanceStatusAsync("t.act-park", "paused"),
            "the wake cannot move a paused run");

        await ActAsync("resume", instance, HttpStatusCode.OK);
        Assert.Equal("waiting", await StatusAsync("t.act-park"));

        // Audit law: both acts, allow, each citing its reason.
        var rows = await RowsAsync(
            "SELECT action, decision FROM facilitator.audit WHERE resource LIKE $1 ORDER BY occurred_at",
            [$"%/instances/{instance}/%"]);
        Assert.Contains(rows, r => r.GetProperty("action").GetString() == "workflow.instance.pause"
                                   && r.GetProperty("decision").GetString() == "allow");
        Assert.Contains(rows, r => r.GetProperty("action").GetString() == "workflow.instance.resume"
                                   && r.GetProperty("decision").GetString() == "allow");
    }

    [Fact]
    public async Task SkipClosesTheLegAsSkipped_AndWalksTheCompletedEdge()
    {
        await StoreAsync(TimerDef());
        await PostIssueAsync();
        // Leg 'one' lands, the run parks on the 'two' timer; pause freezes
        // the park (status paused, deadline kept).
        var instance = await WaitSingleInstanceAsync("t.act-two", "waiting");
        await ActAsync("pause", instance, HttpStatusCode.OK);

        await ActAsync("skip-step", instance, HttpStatusCode.OK);

        // 'two' is 'skipped' (not 'done', not 'failed'), the pointer moved
        // to 'three' — the completed edge, unforked — and the run stays
        // paused with its now-empty deadline, so resume walks it to ground.
        var log = await StepLogAsync(instance);
        Assert.Equal("skipped", log.Single(l => l.GetProperty("stepId").GetString() == "two")
            .GetProperty("status").GetString());
        Assert.Equal("paused", await StatusAsync("t.act-two"));
        var current = (await InstancesAsync("t.act-two")).Single()
            .GetProperty("currentStep").GetString();
        Assert.Equal("three", current);

        await ActAsync("resume", instance, HttpStatusCode.OK);
        await DrainAsync(async () => await StatusAsync("t.act-two") == "completed",
            "the resumed run walks 'three' and completes");
    }

    [Fact]
    public async Task RetryReEntersAFailedLegWithAFreshCommandId_AndRefusalsAreFenced()
    {
        await StoreAsync(GhostDef());
        await StoreAsync(InvokeMissingDef()); // passes store-time target validation
        await ToggleAsync("t.act-ghost", enabled: false); // the leg will fail
        await PostIssueAsync();
        var instance = await WaitSingleInstanceAsync("t.act-invoke", "failed");

        // The failed 'call' leg: status failed, an error output, and NO
        // effect (a leg that never landed appended nothing).
        var first = (await StepLogAsync(instance))
            .Single(l => l.GetProperty("stepId").GetString() == "call");
        Assert.Equal("failed", first.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("effectCommandId").ValueKind);

        // The fence: skip on a failed run refuses (409) and audits a deny.
        await ActAsync("skip-step", instance, HttpStatusCode.Conflict);

        // Bring the ghost back, so the retry converges.
        await ToggleAsync("t.act-ghost", enabled: true);
        await ActAsync("retry-step", instance, HttpStatusCode.OK, """{"stepId":"call"}""");
        await DrainAsync(async () => await StatusAsync("t.act-invoke") == "completed",
            "the retried leg spawns the child, the hand-off closes the flow");

        var log = await StepLogAsync(instance);
        var callLegs = log.Where(l => l.GetProperty("stepId").GetString() == "call")
            .OrderBy(l => l.GetProperty("seq").GetInt32()).ToList();
        // The failed leg stands; the re-run is fresh legs on a fresh seq —
        // a parked re-entry (parked: invoke), then its wake closing it.
        Assert.Equal(3, callLegs.Count);
        Assert.Equal("failed", callLegs[0].GetProperty("status").GetString());
        Assert.Equal("done", callLegs[1].GetProperty("status").GetString());
        Assert.True(callLegs[1].GetProperty("output").TryGetProperty("parked", out _));
        Assert.True(callLegs[2].GetProperty("output").TryGetProperty("wake", out _));
        Assert.Equal("done", log.Single(l => l.GetProperty("stepId").GetString() == "tail")
            .GetProperty("status").GetString());

        // The re-run spawned the child under a fresh effect identity: the
        // ghost now has exactly one instance, and it is terminal.
        var ghost = (await InstancesAsync("t.act-ghost")).Single();
        Assert.Equal("completed", ghost.GetProperty("status").GetString());
    }

    private static string GhostDef() => """
        {
          "id": "t.act-ghost", "version": 1, "title": "the ghost, at last",
          "description": "callable-only projection",
          "enabled": true,
          "on": { "invoked": {} },
          "steps": [
            { "id": "echo", "title": "project inputs", "on": "always",
              "select": { "project": { "ok": "instance" } } }
          ]
        }
        """;

    // ---- plumbing -------------------------------------------------------------

    private async Task ActAsync(string action, Guid instanceId, HttpStatusCode expected, string body = "{}")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/admin/workflows/instances/{instanceId}/{action}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "phase 8 actions test");
        using var response = await _kernel.SendAsync(request);
        Assert.True(
            response.StatusCode == expected,
            $"{action} expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<Guid> WaitSingleInstanceAsync(string defId, string status)
    {
        Guid found = default;
        await DrainAsync(
            async () =>
            {
                var rows = (await InstancesAsync(defId))
                    .Where(i => i.GetProperty("status").GetString() == status)
                    .ToList();
                if (rows.Count == 1)
                {
                    found = rows[0].GetProperty("instanceId").GetGuid();
                    return true;
                }

                return false;
            },
            $"one {defId} instance reaches {status}");
        return found;
    }

    private Task DrainAsync(Func<Task<bool>> settled, string because) =>
        KernelOps.PollUntilAsync(
            async () =>
            {
                await _dispatcher.DrainOnceAsync(CancellationToken.None);
                return await settled();
            },
            because,
            30_000);

    private async Task<bool> InstanceStatusAsync(string defId, string status) =>
        (await InstancesAsync(defId)).Any(i => i.GetProperty("status").GetString() == status);

    private async Task<string?> StatusAsync(string defId)
    {
        var rows = await InstancesAsync(defId);
        return rows.Count == 0 ? null : rows[0].GetProperty("status").GetString();
    }

    private async Task<List<JsonElement>> InstancesAsync(string defId) =>
        (await RowsAsync(
            "SELECT id AS \"instanceId\", def_id AS \"defId\", status, current_step AS \"currentStep\""
            + " FROM facilitator.workflow_instances WHERE def_id = $1",
            [defId]))
        .ToList();

    private async Task<List<JsonElement>> StepLogAsync(Guid instanceId) =>
        (await RowsAsync(
            "SELECT seq, step_id AS \"stepId\", status, effect_command_id AS \"effectCommandId\", output"
            + " FROM facilitator.workflow_step_log WHERE instance_id = $1 ORDER BY seq",
            [instanceId]))
        .ToList();

    private async Task PostIssueAsync()
    {
        var node = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.issue.opened.json"))!.AsObject();
        node["id"] = "evt-" + Guid.NewGuid().ToString("N");
        node["data"]!["workTrigger"] = true;
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", node.ToJsonString(), HttpStatusCode.Accepted);
    }

    private async Task PostMentionAsync()
    {
        var node = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.workspace.heartbeat.json"))!.AsObject();
        node["id"] = "evt-" + Guid.NewGuid().ToString("N");
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", node.ToJsonString(), HttpStatusCode.Accepted);
    }

    private async Task StoreAsync(string body)
    {
        var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetString()!;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/workflows/definitions/{id}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "phase 8 actions test");
        using var response = await _kernel.SendAsync(request);
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"store {id} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<List<JsonElement>> RowsAsync(string sql, object[] parameters)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters)
        {
            cmd.Parameters.AddWithValue(p);
        }

        var rows = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var shape = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = await reader.GetFieldValueAsync<object>(i);
                shape[reader.GetName(i)] = value switch
                {
                    string s when s.StartsWith("{", StringComparison.Ordinal) || s.StartsWith("[", StringComparison.Ordinal) => JsonNode.Parse(s),
                    null or DBNull => null,
                    var other => JsonNode.Parse(JsonSerializer.Serialize(other)),
                };
            }

            rows.Add(JsonDocument.Parse(shape.ToJsonString()).RootElement.Clone());
        }

        return rows;
    }

    private async Task PurgeAsync() =>
        await ExecuteAsync(
            """
            DELETE FROM facilitator.workflow_step_log WHERE instance_id IN
                (SELECT id FROM facilitator.workflow_instances WHERE def_id LIKE 't.act%');
            DELETE FROM facilitator.workflow_instances WHERE def_id LIKE 't.act%';
            DELETE FROM facilitator.workflow_definitions WHERE def_id LIKE 't.act%';
            DELETE FROM facilitator.audit
                WHERE action LIKE 'workflow.instance.%'
                  AND resource LIKE '/api/admin/workflows/instances/%';
            DELETE FROM facilitator.outbox WHERE command_id LIKE 'wf-%';
            DELETE FROM facilitator.inbox WHERE payload ->> 'type' IN
                ('org.facilitator.event.provider.issue.v1', 'org.facilitator.event.workspace.heartbeat.v1');
            """,
            []);

    private async Task ExecuteAsync(string sql, object[] parameters)
    {
        _ = parameters;
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
