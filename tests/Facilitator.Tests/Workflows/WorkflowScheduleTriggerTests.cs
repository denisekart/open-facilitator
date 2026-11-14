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
/// The schedule leg of the plane: a due plan slot fires exactly one tick
/// (the tick envelope's identity converges replays onto the same trigger
/// row and instance), the plan advances per the shared ScheduleSpec law,
/// and a replay of a fired slot converges instead of restarting. The plan
/// column is the seam: tests set it directly (the dispatcher gates on DB
/// time, so a faked process clock is not the honest lever here).
/// </summary>
[Collection("postgres:persistence")]
public sealed class WorkflowScheduleTriggerTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DefId = "t.scheduled";
    private const string ScheduledType = "org.facilitator.event.workflow.scheduled.v1";

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

    [Fact]
    public async Task EnablingStampsThePlan_AndDueSlotsFireOneInstanceEach()
    {
        await StoreAsync();

        // Disabled: no plan (the tick engine ignores it entirely).
        Assert.Null(await PlanAsync());

        await EnableAsync();

        // Stamp law (ScheduleEntity posture): enabling a scheduled
        // definition plans from NOW — the next pass fires.
        var stamped = await PlanAsync();
        Assert.NotNull(stamped);
        Assert.InRange(stamped!.Value, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        var due = Trunc(DateTime.UtcNow).AddMinutes(-30);
        await SetPlanAsync(due);
        var sweeper = _factory.Services.GetRequiredService<WorkflowSweeper>();
        Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None));
        await DrainUntilAsync(() => InstanceCountAsync(1), "the due slot starts one instance");

        // The tick envelope is wire-valid and self-describing, and the
        // plan advanced exactly one interval from the fired slot.
        var tick = Assert.Single(await TickRowsAsync());
        var envelope = tick.GetProperty("envelope");
        Assert.Equal(ScheduledType, envelope.GetProperty("type").GetString());
        Assert.Equal(
            WorkflowIdentity.ScheduledEnvelopeId(DefId, 1, due).ToString("N"),
            envelope.GetProperty("id").GetString());
        Assert.Equal(DefId, envelope.GetProperty("data").GetProperty("definitionId").GetString());
        Assert.Equal(due.ToString("yyyy-MM-ddTHH:mm:ssZ"), envelope.GetProperty("data").GetProperty("occurrenceAt").GetString());
        Assert.StartsWith($"wf-trigger:{DefId}@1:", tick.GetProperty("commandId").GetString(), StringComparison.Ordinal);
        Assert.Equal(due.AddMinutes(5), await PlanAsync());

        // The completed instance carries the tick through the context:
        // the output contract publishes what the tick envelope said.
        var completed = Assert.Single(await InstancesAsync());
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal(DefId, completed.GetProperty("output").GetProperty("definitionId").GetString());
        Assert.Equal(
            due.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            completed.GetProperty("output").GetProperty("occurrenceAt").GetString());

        // The second slot: a fresh plan slot is a fresh envelope identity,
        // a fresh trigger row, a second instance.
        await SetPlanAsync(due.AddMinutes(-7));
        Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None));
        await DrainUntilAsync(() => InstanceCountAsync(2), "the second slot starts a second instance");
        Assert.Equal(2, (await TickRowsAsync()).Count);
    }

    [Fact]
    public async Task BehindPlanFiresOneSlotPerPass_AndReplayOfAFiredSlotConverges()
    {
        await StoreAsync();
        await EnableAsync();
        var sweeper = _factory.Services.GetRequiredService<WorkflowSweeper>();
        var basePlan = Trunc(DateTime.UtcNow).AddMinutes(-16);
        await SetPlanAsync(basePlan);

        // Four slots due (T0, T5, T10, T15): each pass fires a single slot
        // (verbatim schedule-plane catch-up law — no thundering herd) and
        // walks the plan forward one interval per pass.
        for (var slot = 0; slot < 4; slot++)
        {
            Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None));
            Assert.Equal(basePlan.AddMinutes(5 * (slot + 1)), await PlanAsync());
        }

        Assert.Equal(0, await sweeper.SweepOnceAsync(CancellationToken.None)); // plan T20 is future
        await DrainUntilAsync(() => InstanceCountAsync(4), "four slots, four instances");
        Assert.Equal(4, (await TickRowsAsync()).Count);

        // Replay law: pointing the plan back at an already-fired slot
        // recomputes the SAME tick envelope id — the convergence check in
        // the append finds the existing row, adds nothing, and no second
        // instance starts (the pass still reports, the plan walks on).
        await SetPlanAsync(basePlan);
        Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None)); // reports the replay
        Assert.Equal(4, (await TickRowsAsync()).Count); // ... absorbed by the converged row
        Assert.Equal(basePlan.AddMinutes(5), await PlanAsync());
        await DrainUntilAsync(() => InstanceCountAsync(4), "the replay starts nothing new");
    }

    [Fact]
    public async Task DisabledDefinitionNeverTicks()
    {
        await StoreAsync();
        await SetPlanAsync(Trunc(DateTime.UtcNow).AddMinutes(-10)); // stale plan on a disabled row
        var sweeper = _factory.Services.GetRequiredService<WorkflowSweeper>();
        Assert.Equal(0, await sweeper.SweepOnceAsync(CancellationToken.None));
        Assert.Empty(await TickRowsAsync());
    }

    /// <summary>A slot is a UTC minute: identity/plan comparisons are
    /// second-precision (the tick envelope truncates the same way).</summary>
    private static DateTime Trunc(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    private static string DefinitionJson()
    {
        var document = new JsonObject
        {
            ["id"] = DefId,
            ["version"] = 1,
            ["title"] = "Schedule test flow",
            ["description"] = "phase 5 schedule-trigger proof",
            ["on"] = new JsonObject { ["schedule"] = "interval:5m" },
            // Output contract law: the instance output projects over the
            // step log — the completion leg publishes what the tick carried.
            ["output"] = new JsonObject
            {
                ["project"] = new JsonObject
                {
                    ["definitionId"] = "steps.s1.output.definitionId",
                    ["occurrenceAt"] = "steps.s1.output.occurrenceAt",
                },
            },
            ["steps"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "s1",
                    ["title"] = "record the tick",
                    ["select"] = new JsonObject
                    {
                        // Context law: the tick envelope is $event — the
                        // projection proves the synthetic envelope rides it.
                        ["project"] = new JsonObject
                        {
                            ["definitionId"] = "event.data.definitionId",
                            ["occurrenceAt"] = "event.data.occurrenceAt",
                        },
                    },
                },
                new JsonObject
                {
                    // The completion leg: each leg commits its own
                    // transaction, so the completion projection sees the
                    // previous leg's logged output.
                    ["id"] = "done",
                    ["title"] = "close",
                    ["on"] = "s1:completed",
                    ["select"] = new JsonObject { ["project"] = new JsonObject { ["ran"] = "instance" } },
                },
            },
        };
        return document.ToJsonString();
    }

    private async Task StoreAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/workflows/definitions/{DefId}")
        {
            Content = new StringContent(DefinitionJson(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "phase 5 schedule test");
        using var response = await _kernel.SendAsync(request);
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"store failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private async Task EnableAsync() =>
        await KernelOps.PostJsonAsync(_kernel,
            $"/api/admin/workflows/definitions/{DefId}/versions/1/toggle", """{"enabled": true}""",
            HttpStatusCode.OK, "phase 5 schedule test");

    private async Task SetPlanAsync(DateTime planUtc) =>
        await ExecuteAsync(
            "UPDATE facilitator.workflow_definitions SET next_tick_at = '{0}'::timestamptz WHERE def_id = '{1}'"
                .Replace("{0}", planUtc.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.Ordinal)
                .Replace("{1}", DefId, StringComparison.Ordinal));

    private async Task<DateTime?> PlanAsync()
    {
        var rows = await RowsAsync(
            """
            SELECT jsonb_build_object('plan', to_char(next_tick_at, 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))
            FROM facilitator.workflow_definitions WHERE def_id = $1
            """, DefId);
        var row = Assert.Single(rows);
        return row.GetProperty("plan").ValueKind == JsonValueKind.Null
            ? null
            : DateTime.ParseExact(row.GetProperty("plan").GetString()!, "yyyy-MM-ddTHH:mm:ssZ", null);
    }

    private async Task<List<JsonElement>> TickRowsAsync() =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('commandId', command_id, 'envelope', envelope::jsonb)
            FROM facilitator.outbox WHERE event_type = $1
            """, ScheduledType);

    private async Task<List<JsonElement>> InstancesAsync() =>
        await RowsAsync(
            "SELECT jsonb_build_object('instanceId', id, 'status', status, 'output', output::jsonb)"
            + " FROM facilitator.workflow_instances WHERE def_id = $1 ORDER BY started_at",
            DefId);

    private async Task<bool> InstanceCountAsync(int expected) =>
        (await InstancesAsync()).Count == expected;

    private Task DrainUntilAsync(Func<Task<bool>> settled, string because) =>
        KernelOps.PollUntilAsync(
            async () =>
            {
                await _dispatcher.DrainOnceAsync(CancellationToken.None);
                return await settled();
            },
            because,
            20_000);

    private async Task<List<JsonElement>> RowsAsync(string sql, object parameter)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        cmd.Parameters.AddWithValue(parameter);
        var results = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(JsonDocument.Parse(reader.GetFieldValue<string>(0)).RootElement.Clone());
        }

        return results;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task PurgeAsync()
    {
        var statements = new[]
        {
            "DELETE FROM facilitator.workflow_step_log WHERE instance_id IN"
            + " (SELECT id FROM facilitator.workflow_instances WHERE def_id = '@DEF@')",
            "DELETE FROM facilitator.workflow_instances WHERE def_id = '@DEF@'",
            "DELETE FROM facilitator.workflow_definitions WHERE def_id = '@DEF@'",
            $"DELETE FROM facilitator.outbox WHERE event_type = '{ScheduledType}'",
        };
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var cmd = new NpgsqlCommand(statement.Replace("@DEF@", DefId, StringComparison.Ordinal), connection);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
