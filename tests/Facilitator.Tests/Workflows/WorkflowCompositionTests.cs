using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Workflows;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Ofac.Protocol.Validation;

namespace Facilitator.Tests.Workflows;

/// <summary>
/// Phase 6 (composition gate): awaited invoke → child with lineage →
/// completion hand-off → parent resumes EXACTLY once with the projected
/// child output; wake re-delivery is a no-op; a crash between child
/// completion and parent resume replays through a fresh host; preemption
/// denies as a branch (budget), at enable time (cycle), and by depth; the
/// sweeper's invoke-timeout cancels the whole subtree; detached children
/// survive a completed parent but die with a cancelled one; and the sink's
/// fall-through law offers an unmatched completion event to on.event
/// matching. Joins <c>postgres:persistence</c>.
/// </summary>
[Collection("postgres:persistence")]
public sealed class WorkflowCompositionTests(PostgresFixture fixture) : IAsyncLifetime
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

    // ---- the happy path -------------------------------------------------------

    [Fact]
    public async Task AwaitedInvokeRunsChildHandOffAndResumesParentOnce()
    {
        await StoreAsync(ParentDef());
        await StoreAsync(ChildDef("t.comp-child", selectEcho: true));
        await EnableAsync("t.comp-child");
        await EnableAsync("t.comp-parent");

        await PostIssueAsync();
        await DrainUntilAsync(() => InstanceStatusAsync("t.comp-parent", "completed"), "the pair converges completed");

        var parent = (await InstancesAsync("t.comp-parent")).Single();
        var child = (await InstancesAsync("t.comp-child")).Single();
        var parentId = parent.GetProperty("instanceId").GetGuid();
        var childId = child.GetProperty("instanceId").GetGuid();

        // Lineage law: depth 1 under the parent, the path names both.
        Assert.Equal("completed", child.GetProperty("status").GetString());
        Assert.Equal(1, child.GetProperty("depth").GetInt32());
        Assert.Equal($"t.comp-parent@1>t.comp-child@1", child.GetProperty("invocationPath").GetString());
        Assert.Equal(parentId, child.GetProperty("parentInstanceId").GetGuid());

        // The child's projected output rode the hand-off into the parent's
        // wake output, and the parent's own projection reads it back.
        Assert.Equal("walk-topic", child.GetProperty("output").GetProperty("topic").GetString());
        Assert.Equal("walk-topic", parent.GetProperty("output").GetProperty("topic").GetString());
        // Parked leg (running->done), the wake-resolution leg, and the
        // tail leg: three rows, two steps.
        var parentLog = await StepLogAsync(parentId);
        Assert.Equal(3, parentLog.Count);
        Assert.Equal(2, parentLog.Select(l => l.GetProperty("stepId").GetString()).Distinct().Count());

        // The hand-off row: exact command id, a real wire envelope.
        var wake = await OutboxRowAsync($"wf-wake:{childId:N}:completed");
        Assert.NotNull(wake);
        Assert.Equal(
            "org.facilitator.event.workflow.completed.v1",
            wake.Value.GetProperty("event_type").GetString());
        Assert.True(
            EnvelopeValidator.CreateDefault().Validate(wake.Value.GetProperty("envelope").GetRawText()).Ok,
            "the completion hand-off is a valid wire envelope");

        // Exactly-once wake: re-delivering the same row (same ids) is a
        // no-op — the park resolved, the fall-through finds no matching
        // definition, the step log count proves nothing ran again.
        var sink = _factory.Services.GetServices<ISink>().Single(s => s.Channel == "workflow");
        var row = new OutboxRow(
            wake.Value.GetProperty("outbox_id").GetGuid(),
            "workflow.kernel",
            "org.facilitator.event.workflow.completed.v1",
            $"wf-wake:{childId:N}:completed",
            JsonDocument.Parse(wake.Value.GetProperty("envelope").GetRawText()).RootElement,
            1);
        Assert.True(await sink.DeliverAsync(row, CancellationToken.None));
        Assert.Equal(3, (await StepLogAsync(parentId)).Count);
    }

    [Fact]
    public async Task CrashMidAwaitReplaysThroughAFreshHost()
    {
        await StoreAsync(ParentDef(timeout: "PT30M"));
        await StoreAsync(ChildDef("t.comp-child", selectEcho: true, slow: true));
        await EnableAsync("t.comp-child");
        await EnableAsync("t.comp-parent");
        await PostIssueAsync();

        // Drive until BOTH park: the parent on the invoke, the child on
        // its long timer (no hand-off yet).
        await DrainUntilAsync(
            async () => await InstanceStatusAsync("t.comp-parent", "waiting")
                        && await InstanceStatusAsync("t.comp-child", "waiting"),
            "the pair parks");
        var parked = (await InstancesAsync("t.comp-parent")).Single();
        var parentId = parked.GetProperty("instanceId").GetGuid();
        Assert.NotEqual(JsonValueKind.Null, parked.GetProperty("deadlineAt").ValueKind);

        // Crash: the awaited child's timer is due — shorten it directly (the
        // child row and its log are the plane's state; the sweeper reads it),
        // then a FRESH host finishes the job: the wake row is durable.
        await ExecuteAsync(
            "UPDATE facilitator.workflow_instances SET deadline_at = now() - interval '1 second' WHERE def_id = 't.comp-child' AND status = 'waiting'",
            []);
        _kernel.Dispose();
        _factory.Dispose();
        _factory = new KernelTestFactory(fixture, noDispatcher: true);
        _factory.Services.GetRequiredService<WorkflowRuntime>().Enabled = true;
        _kernel = _factory.CreateAuthedClient();
        _dispatcher = _factory.Services.GetRequiredService<IEventDispatcher>();

        // The fresh host's sweeper fires the child's due timer; the child
        // completes, the durable wake row resumes the parent.
        await _factory.Services.GetRequiredService<WorkflowSweeper>().SweepOnceAsync(CancellationToken.None);
        await DrainUntilAsync(() => InstanceStatusAsync("t.comp-parent", "completed"), "the fresh host resumes the parent once");
        var parent = (await InstancesAsync("t.comp-parent")).Single();
        Assert.Equal("walk-topic", parent.GetProperty("output").GetProperty("topic").GetString());
        Assert.Equal(3, (await StepLogAsync(parentId)).Count);
    }

    // ---- preemption -----------------------------------------------------------

    [Fact]
    public async Task ReentrantSelfInvokeDeniesAtTheInvocationCap_AsABranch()
    {
        await StoreAsync(SelfInvokeDef());
        await EnableAsync("t.self-reentry");
        await PostIssueAsync();
        await DrainUntilAsync(
            async () =>
            {
                var rows = await InstancesAsync("t.self-reentry");
                return rows.Count == 3
                       && rows.Any(r => r.TryGetProperty("endReason", out var e)
                                        && e.GetString() == "budget-exhausted");
            },
            "root + two children, the third invoke denied");

        var instances = await InstancesAsync("t.self-reentry");
        Assert.Equal(3, instances.Count); // the third invoke denied: NO child row
        var denier = instances.Single(i => i.TryGetProperty("endReason", out var er) && er.GetString() == "budget-exhausted");
        var log = await StepLogAsync(denier.GetProperty("instanceId").GetGuid());
        var denial = log.Single(l => l.GetProperty("output").TryGetProperty("reason", out var r)
                                     && r.GetString() == "budget-exhausted");
        Assert.Equal("failed", denial.GetProperty("status").GetString());

        // The chain grief travels as plain failures (no composition reason
        // for grieving a child): exactly the denier carries the budget stamp.
        Assert.Equal(
                    1,
                    instances.Count(i => i.TryGetProperty("endReason", out var e) && e.ValueKind != JsonValueKind.Null));
    }

    [Fact]
    public async Task EnableTimeCycleIsRefused()
    {
        // The static-cycle law's reachable face: a NON-reentrant
        // self-invoke is a one-edge cycle over the enabled graph — 422
        // composition-cycle.
        await StoreAsync(CycleDef("t.cyc-self", "t.cyc-self"));
        using (var selfToggle = new HttpRequestMessage(
                   HttpMethod.Post, "/api/admin/workflows/definitions/t.cyc-self/versions/1/toggle")
        {
            Content = new StringContent("""{"enabled": true}""", Encoding.UTF8, "application/json"),
        })
        {
            selfToggle.Headers.TryAddWithoutValidation("X-Change-Reason", "phase 6 composition test");
            using var selfResponse = await _kernel.SendAsync(selfToggle);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, selfResponse.StatusCode);
            Assert.Contains("composition-cycle", await selfResponse.Content.ReadAsStringAsync());
        }

        // The two-member cycle A→B→A is refused even earlier by the
        // referential law: a live target must be enabled, and enabling the
        // first member names the second — which no live graph yet
        // contains. 422 invoke-target-unavailable, the same fail-closed
        // posture, decided one guard before the graph walk.
        await StoreAsync(CycleDef("t.cyc-a", "t.cyc-b"));
        await StoreAsync(CycleDef("t.cyc-b", "t.cyc-a"));
        using var toggleA = new HttpRequestMessage(
            HttpMethod.Post, "/api/admin/workflows/definitions/t.cyc-a/versions/1/toggle")
        {
            Content = new StringContent("""{"enabled": true}""", Encoding.UTF8, "application/json"),
        };
        toggleA.Headers.TryAddWithoutValidation("X-Change-Reason", "phase 6 composition test");
        using var responseA = await _kernel.SendAsync(toggleA);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, responseA.StatusCode);
        Assert.Contains("invoke-target-unavailable", await responseA.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DepthCapDeniesTheSeventhLevel()
    {
        foreach (var level in Enumerable.Range(0, 7).Reverse())
        {
            await StoreAsync(ChainDef(level));
            await EnableAsync($"t.d{level}");
        }

        await PostIssueAsync();
        await DrainUntilAsync(
            async () =>
            {
                var rows = await InstancesAsync("t.d");
                return rows.Count == 6
                       && rows.Any(r => r.TryGetProperty("endReason", out var e)
                                        && e.GetString() == "chain-age-exceeded");
            },
            "levels 0..5 exist, 6 never did, the depth denial recorded");

        Assert.Equal(6, (await InstancesAsync("t.d")).Count);
        var deepest = (await InstancesAsync("t.d5")).Single();
        Assert.Equal("failed", deepest.GetProperty("status").GetString());
        Assert.Equal("chain-age-exceeded", deepest.GetProperty("endReason").GetString());
    }

    [Fact]
    public async Task InvokeTimeoutCancelsTheWholeSubtree()
    {
        await StoreAsync(InvokeTimeoutParent());
        await StoreAsync(ParkWithDetachedDef());
        await StoreAsync(ParkLeafDef("t.leaf-park"));
        await StoreAsync(ParkLeafDef("t.leaf-detached"));
        await EnableAsync("t.leaf-park");
        await EnableAsync("t.leaf-detached");
        await EnableAsync("t.leaf-park-child");
        await EnableAsync("t.invoke-timeout-parent");
        await PostIssueAsync();
        await DrainUntilAsync(() => InstanceStatusAsync("t.leaf-park", "waiting"), "the grandchild parks");

        // Age the parent's invoke deadline, fire the sweep (the admin face).
        await ExecuteAsync(
            """
            UPDATE facilitator.workflow_instances SET deadline_at = now() - interval '1 second'
            WHERE def_id = 't.invoke-timeout-parent' AND status = 'waiting'
            """,
            []);
        await KernelOps.PostJsonAsync(
            _kernel, "/api/admin/sweeps/workflows", "{}", HttpStatusCode.OK, "phase 6 composition test");

        await DrainUntilAsync(() => InstanceStatusAsync("t.invoke-timeout-parent", "failed"), "the parent takes its timeout");
        var parent = (await InstancesAsync("t.invoke-timeout-parent")).Single();
        Assert.Equal("invoke-timeout", parent.GetProperty("endReason").GetString());
        foreach (var defId in new[] { "t.leaf-park", "t.leaf-park-child", "t.leaf-detached" })
        {
            var rows = await InstancesAsync(defId);
            if (defId == "t.leaf-park-child")
            {
                // The awaited child: cancelled, carrying its recorded
                // terminal state into the hand-off.
                Assert.Equal("cancelled", Assert.Single(rows).GetProperty("status").GetString());
                Assert.Equal("parent-cancelled", rows.Single().GetProperty("endReason").GetString());
            }
        }

        // The detached grandchild died with the subtree too.
        var detached = Assert.Single(await InstancesAsync("t.leaf-detached"));
        Assert.Equal("cancelled", detached.GetProperty("status").GetString());
        Assert.Equal("parent-cancelled", detached.GetProperty("endReason").GetString());
    }

    [Fact]
    public async Task DetachedChildSurvivesCompletedParent_ButOperatorCancelGrieves()
    {
        await StoreAsync(DetachedParentDef());
        await StoreAsync(ParkLeafDef("t.leaf-detached"));
        await EnableAsync("t.leaf-detached");
        await EnableAsync("t.detached-parent");
        await PostIssueAsync();
        await DrainUntilAsync(
            async () => await InstanceStatusAsync("t.detached-parent", "completed")
                        && await InstanceStatusAsync("t.leaf-detached", "waiting"),
            "the parent completes fire-and-forget, the detached child parks");

        var child = Assert.Single(await InstancesAsync("t.leaf-detached"));
        Assert.Equal("waiting", child.GetProperty("status").GetString()); // survives a COMPLETED parent

        // Operator cancel of the live child: it ends cancelled (operator-cancel).
        var childId = child.GetProperty("instanceId").GetGuid();
        await KernelOps.PostJsonAsync(
            _kernel, $"/api/admin/workflows/instances/{childId}/cancel", "{}", HttpStatusCode.OK);
        child = Assert.Single(await InstancesAsync("t.leaf-detached"));
        Assert.Equal("cancelled", child.GetProperty("status").GetString());
        Assert.Equal("operator-cancel", child.GetProperty("endReason").GetString());
    }

    // ---- fall-through ---------------------------------------------------------

    [Fact]
    public async Task UnmatchedCompletionFallsThroughToEventTriggering()
    {
        await StoreAsync(RootChildDef());
        await StoreAsync(OnCompletedDef());
        await EnableAsync("t.on-completed");
        await EnableAsync("t.root-child");
        await PostIssueAsync();
        await DrainUntilAsync(() => InstanceStatusAsync("t.on-completed", "completed"), "the completion event triggered a flow");

        // Exactly one triggered instance — the fall-through rode the
        // on.event law, not a special path.
        Assert.Single(await InstancesAsync("t.on-completed"));
    }

    // ---- definitions ------------------------------------------------------------

    private static string ParentDef(string timeout = "PT30M") => $$"""
        {
          "id": "t.comp-parent", "version": 1, "title": "composition parent",
          "description": "invokes the child, awaits its output, projects it",
          "enabled": false,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "output": { "project": { "topic": "steps.tail.output.topic" } },
          "steps": [
            { "id": "call", "title": "invoke the child", "on": "always",
              "invoke": { "workflow": "t.comp-child", "inputs": { "topic": "walk-topic" }, "await": true,
                          "timeout": "{{timeout}}", "onTimeout": "tail" } },
            { "id": "tail", "title": "project the child output", "on": "call:completed",
              "select": { "project": { "topic": "steps.call.output.wake.data.output.topic" } } }
          ]
        }
        """;

    private static string ChildDef(string id, bool selectEcho, bool slow = false)
    {
        var steps = slow
            ? """[{ "id": "echo", "title": "project inputs", "on": "always", "select": { "project": { "topic": "input.topic" } } }, { "id": "hold", "title": "park", "on": "echo:completed", "timer": { "for": "PT30M" } }]"""
            : """[{ "id": "echo", "title": "project inputs", "on": "always", "select": { "project": { "topic": "input.topic" } } }]""";
        return $$"""
            {
              "id": "{{id}}", "version": 1, "title": "callable child",
              "description": "callable-only projection of the invoke inputs",
              "enabled": false,
              "on": { "invoked": {} },
              "output": { "project": { "topic": "steps.echo.output.topic" } },
              "steps": {{steps}}
            }
            """;
    }

    private static string SelfInvokeDef() => """
        {
          "id": "t.self-reentry", "version": 1, "title": "reentrant self",
          "description": "self-invoke capped at 3 per chain",
          "enabled": false,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "composition": { "reentrant": true, "maxInvocations": 3 },
          "steps": [
            { "id": "loop", "title": "one more self", "on": "always",
              "invoke": { "workflow": "t.self-reentry", "await": true, "timeout": "PT30M" } }
          ]
        }
        """;

    private static string CycleDef(string id, string target) => $$"""
        {
          "id": "{{id}}", "version": 1, "title": "cycle member {{id}}",
          "description": "invokes {{target}}",
          "enabled": false,
          "on": { "invoked": {} },
          "steps": [
            { "id": "hop", "title": "hop", "on": "always",
              "invoke": { "workflow": "{{target}}", "await": true, "timeout": "PT30M" } }
          ]
        }
        """;

    private static string ChainDef(int level)
    {
        var step = level < 6
            ? $$"""{ "id": "hop", "title": "deeper", "on": "always", "invoke": { "workflow": "t.d{{level + 1}}", "await": true, "timeout": "PT30M" } }"""
            : $$"""{ "id": "bottom", "title": "leaf", "on": "always", "select": { "project": { "z": "instance" } } }""";
        var on = level == 0
            ? """{ "event": "org.facilitator.event.provider.issue.v1" }"""
            : """{ "invoked": {} }""";
        return $$"""
            {
              "id": "t.d{{level}}", "version": 1, "title": "chain level {{level}}",
              "description": "depth budget proof",
              "enabled": false,
              "on": {{on}},
              "steps": [{{step}}]
            }
            """;
    }

    private static string InvokeTimeoutParent() => """
        {
          "id": "t.invoke-timeout-parent", "version": 1, "title": "await that times out",
          "description": "invoke PT1S: the sweeper cancels the subtree",
          "enabled": false,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "call", "title": "await the child forever-ish", "on": "always",
              "invoke": { "workflow": "t.leaf-park-child", "await": true, "timeout": "PT1S" } }
          ]
        }
        """;

    private static string ParkWithDetachedDef() => """
        {
          "id": "t.leaf-park-child", "version": 1, "title": "child with a detached grandchild",
          "description": "spawn detached, then park",
          "enabled": false,
          "on": { "invoked": {} },
          "steps": [
            { "id": "spawn", "title": "detached grandchild", "on": "always",
              "invoke": { "workflow": "t.leaf-detached", "await": false, "timeout": "PT30M" } },
            { "id": "hold", "title": "park the child", "on": "spawn:completed", "timer": { "for": "PT30M" } }
          ]
        }
        """;

    private static string ParkLeafDef(string id) => $$"""
        {
          "id": "{{id}}", "version": 1, "title": "parking leaf {{id}}",
          "description": "callable park on a long timer",
          "enabled": false,
          "on": { "invoked": {} },
          "steps": [
            { "id": "hold", "title": "hold", "on": "always", "timer": { "for": "PT30M" } }
          ]
        }
        """;

    private static string DetachedParentDef() => """
        {
          "id": "t.detached-parent", "version": 1, "title": "fire and forget",
          "description": "await: false — completes without the child",
          "enabled": false,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "spawn", "title": "detached", "on": "always",
              "invoke": { "workflow": "t.leaf-detached", "await": false, "timeout": "PT30M" } }
          ]
        }
        """;

    private static string RootChildDef() => """
        {
          "id": "t.root-child", "version": 1, "title": "root that completes",
          "description": "issue-triggered, completes: its hand-off has no parent",
          "enabled": false,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "done", "title": "select", "on": "always", "select": { "project": { "x": "instance" } } }
          ]
        }
        """;

    private static string OnCompletedDef() => """
        {
          "id": "t.on-completed", "version": 1, "title": "triggers on workflow completion",
          "description": "the sink's fall-through target",
          "enabled": false,
          "on": { "event": "org.facilitator.event.workflow.completed.v1",
                  "where": { "data.definitionId": "t.root-child" } },
          "steps": [
            { "id": "done", "title": "select", "on": "always", "select": { "project": { "y": "instance" } } }
          ]
        }
        """;

    // ---- plumbing -------------------------------------------------------------

    private async Task PostIssueAsync()
    {
        var node = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.issue.opened.json"))!.AsObject();
        node["id"] = "evt-" + Guid.NewGuid().ToString("N");
        node["data"]!["workTrigger"] = true;
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
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "phase 6 composition test");
        using var response = await _kernel.SendAsync(request);
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"store {id} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private async Task EnableAsync(string id) =>
        await KernelOps.PostJsonAsync(
            _kernel,
            $"/api/admin/workflows/definitions/{id}/versions/1/toggle",
            """{"enabled": true}""",
            HttpStatusCode.OK,
            "phase 6 composition test");

    private Task DrainUntilAsync(Func<Task<bool>> settled, string because) =>
        KernelOps.PollUntilAsync(
            async () =>
            {
                await _dispatcher.DrainOnceAsync(CancellationToken.None);
                return await settled();
            },
            because,
            30_000);

    private async Task<bool> InstanceStatusAsync(string defPrefix, string status) =>
        (await InstancesAsync(defPrefix)).Any(i => i.GetProperty("status").GetString() == status);

    private async Task<List<JsonElement>> InstancesAsync(string defPrefix) =>
        (await RowsAsync(
            "SELECT id AS \"instanceId\", def_id AS \"defId\", status, current_step AS \"currentStep\", depth,"
            + " invocation_path AS \"invocationPath\", parent_instance_id AS \"parentInstanceId\","
            + " end_reason AS \"endReason\", output, deadline_at AS \"deadlineAt\""
            + " FROM facilitator.workflow_instances WHERE def_id LIKE $1 || '%' ORDER BY started_at",
            [defPrefix]))
        .ToList();

    private async Task<List<JsonElement>> StepLogAsync(Guid instanceId) =>
        (await RowsAsync(
            "SELECT seq, step_id AS \"stepId\", status, output FROM facilitator.workflow_step_log"
            + " WHERE instance_id = $1 ORDER BY seq",
            [instanceId]))
        .ToList();

    private async Task<JsonElement?> OutboxRowAsync(string commandId)
    {
        var rows = await RowsAsync(
            "SELECT outbox_id, command_id, event_type, envelope FROM facilitator.outbox WHERE command_id = $1",
            [commandId]);
        return rows.Count == 0 ? null : rows[0];
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

        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<JsonElement>();
        while (await reader.ReadAsync())
        {
            var obj = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                {
                    obj[reader.GetName(i)] = null;
                    continue;
                }

                var fieldType = reader.GetFieldType(i);
                obj[reader.GetName(i)] = fieldType == typeof(Guid)
                    ? JsonValue.Create(reader.GetGuid(i))
                    : fieldType == typeof(int)
                        ? JsonValue.Create(reader.GetInt32(i))
                        : fieldType == typeof(long)
                            ? JsonValue.Create(reader.GetInt64(i))
                            : fieldType == typeof(DateTime) || fieldType == typeof(DateTimeOffset)
                                ? JsonValue.Create(reader.GetFieldValue<DateTime>(i).ToUniversalTime().ToString("O"))
                                : fieldType == typeof(string)
                                    ? ParseMaybeJson(reader.GetString(i))
                                    : JsonValue.Create(reader.GetValue(i).ToString());
            }

            rows.Add(JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone());
        }

        return rows;
    }

    /// <summary>jsonb columns arrive as strings: parse real JSON, keep
    /// plain text as text.</summary>
    private static JsonNode? ParseMaybeJson(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    private async Task ExecuteAsync(string sql, object[] parameters)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var p in parameters)
        {
            cmd.Parameters.AddWithValue(p);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task PurgeAsync() =>
        await ExecuteAsync(
            """
            DELETE FROM facilitator.workflow_step_log WHERE instance_id IN
                (SELECT id FROM facilitator.workflow_instances WHERE def_id LIKE 't.comp%'
                                                     OR def_id LIKE 't.d%'
                                                     OR def_id LIKE 't.cyc%'
                                                     OR def_id LIKE 't.self%'
                                                     OR def_id LIKE 't.leaf%'
                                                     OR def_id LIKE 't.invoke-timeout%'
                                                     OR def_id LIKE 't.detached%'
                                                     OR def_id LIKE 't.root-child%'
                                                     OR def_id LIKE 't.on-completed%');
            DELETE FROM facilitator.workflow_instances WHERE def_id LIKE 't.comp%'
                                                        OR def_id LIKE 't.d%'
                                                        OR def_id LIKE 't.cyc%'
                                                        OR def_id LIKE 't.self%'
                                                        OR def_id LIKE 't.leaf%'
                                                        OR def_id LIKE 't.invoke-timeout%'
                                                        OR def_id LIKE 't.detached%'
                                                        OR def_id LIKE 't.root-child%'
                                                        OR def_id LIKE 't.on-completed%';
            DELETE FROM facilitator.workflow_definitions WHERE def_id LIKE 't.comp%'
                                                           OR def_id LIKE 't.d%'
                                                           OR def_id LIKE 't.cyc%'
                                                           OR def_id LIKE 't.self%'
                                                           OR def_id LIKE 't.leaf%'
                                                           OR def_id LIKE 't.invoke-timeout%'
                                                           OR def_id LIKE 't.detached%'
                                                           OR def_id LIKE 't.root-child%'
                                                           OR def_id LIKE 't.on-completed%';
            DELETE FROM facilitator.outbox WHERE command_id LIKE 'wf-%';
            DELETE FROM facilitator.inbox WHERE payload ->> 'type' = 'org.facilitator.event.provider.issue.v1'
                                          OR payload ->> 'type' = 'org.facilitator.event.workflow.completed.v1';
            """,
            []);
}
