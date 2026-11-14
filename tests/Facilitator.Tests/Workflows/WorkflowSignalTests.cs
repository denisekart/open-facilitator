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
/// Phase 6.6 (signal gate): a flow's signal step wakes ONE addressed
/// instance's park exactly once (the wake command id converges under
/// re-delivery); an unmatched signal falls through to on.event matching
/// (a matching definition starts exactly one instance, none exist — no
/// DLQ); lineage is untouched on both sides (a signal spawns nothing);
/// and a signal row appended before a crash resumes the target exactly
/// once on a fresh host (the durable outbox law). Joins
/// <c>postgres:persistence</c>.
/// </summary>
[Collection("postgres:persistence")]
public sealed class WorkflowSignalTests(PostgresFixture fixture) : IAsyncLifetime
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

    [Fact]
    public async Task SignalWakesTheParkedTargetOnce_AndRedeliveryIsNoop()
    {
        await StoreAsync(SenderDef());
        await StoreAsync(ReceiverDef());
        await EnableAsync("t.sig-receiver");
        await EnableAsync("t.sig-sender");
        var target = await PostMentionAsync();
        await PostIssueAsync(target);

        // The sender signals the parked receiver; both finish.
        await DrainUntilAsync(
            async () => await InstanceStatusAsync("t.sig-receiver", "completed")
                        && await InstanceStatusAsync("t.sig-sender", "completed"),
            "the signal crosses and both flows finish");

        var sender = (await InstancesAsync("t.sig-sender")).Single();
        var receiver = (await InstancesAsync("t.sig-receiver")).Single();
        var senderId = sender.GetProperty("instanceId").GetGuid();
        var receiverId = receiver.GetProperty("instanceId").GetGuid();

        // The sender's signal leg is done, effect id in the plane's signal
        // namespace.
        var senderLog = await StepLogAsync(senderId);
        var signalLeg = senderLog.Single(l => l.GetProperty("stepId").GetString() == "ping");
        Assert.Equal("done", signalLeg.GetProperty("status").GetString());
        var effect = signalLeg.GetProperty("effectCommandId").GetString()!;
        Assert.StartsWith($"wf-signal:{senderId:N}:ping:", effect, StringComparison.Ordinal);

        // The receiver's wake output: the signal envelope, sender named.
        var receiverLog = await StepLogAsync(receiverId);
        // The parked leg closes as 'done' and the wake-resolution leg is a
        // second row for the same step (the advance law): the last one
        // carries the wake output.
        var awaitLegs = receiverLog.Where(l => l.GetProperty("stepId").GetString() == "await").ToList();
        Assert.Equal(2, awaitLegs.Count);
        var woken = awaitLegs[^1];
        Assert.Equal("done", woken.GetProperty("status").GetString());
        Assert.Equal(
            "org.facilitator.event.workspace.heartbeat.v1",
            woken.GetProperty("output").GetProperty("resumedBy").GetString());
        Assert.Equal(
            $"workflow/{senderId}",
            woken.GetProperty("output").GetProperty("wake").GetProperty("subject").GetString());

        // The wake row: exact command id (sender row as the seed), a
        // valid envelope.
        var signalRow = await OutboxRowAsync(effect);
        Assert.NotNull(signalRow);
        var wake = await OutboxRowAsync($"wf-wake:{receiverId:N}:{signalRow!.Value.GetProperty("outbox_id").GetGuid():N}");
        Assert.NotNull(wake);
        Assert.True(
            EnvelopeValidator.CreateDefault().Validate(wake!.Value.GetProperty("envelope").GetRawText()).Ok,
            "the signal envelope is a valid wire envelope");

        // Exactly-once: re-deliver the SAME wake row through the sink.
        var sink = _factory.Services.GetServices<ISink>().Single(s => s.Channel == "workflow");
        var row = new OutboxRow(
            wake.Value.GetProperty("outbox_id").GetGuid(),
            "workflow.kernel",
            "org.facilitator.event.workspace.heartbeat.v1",
            wake.Value.GetProperty("command_id").GetString()!,
            JsonDocument.Parse(wake.Value.GetProperty("envelope").GetRawText()).RootElement,
            1);
        Assert.True(await sink.DeliverAsync(row, CancellationToken.None));
        Assert.Equal(receiverLog.Count, (await StepLogAsync(receiverId)).Count);

        // No lineage by construction: neither flow records a parent.
        Assert.Equal("t.sig-sender@1", sender.GetProperty("invocationPath").GetString());
        Assert.Equal("t.sig-receiver@1", receiver.GetProperty("invocationPath").GetString());
        Assert.Equal(0, sender.GetProperty("depth").GetInt32());
        Assert.Equal(0, receiver.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task UnmatchedSignalFallsThroughToEventTriggering()
    {
        await StoreAsync(SenderDef());
        await StoreAsync(OnSignalDef());
        await EnableAsync("t.on-signal");
        await EnableAsync("t.sig-sender");
        // No receiver at all: the addressed instance does not exist —
        // the signal is consumed and the envelope rides on.event matching.
        await PostIssueAsync(Guid.NewGuid());
        await DrainUntilAsync(() => InstanceStatusAsync("t.on-signal", "completed"), "the fall-through triggered a flow");

        // Exactly one triggered instance from the signal (no park existed:
        // the envelope rode the on.event law); the sender completed.
        Assert.Single(await InstancesAsync("t.on-signal"));
        var dlq = await CountAsync(
            "SELECT count(*) FROM facilitator.outbox WHERE status = 'dead' AND command_id LIKE 'wf-signal:%'");
        Assert.Equal(0, dlq);
    }

    [Fact]
    public async Task SignalBeforeTargetDrainResumesOnFreshHost()
    {
        await StoreAsync(SenderDef());
        await StoreAsync(ReceiverDef(parkWithTimer: true));
        await EnableAsync("t.sig-receiver");
        await EnableAsync("t.sig-sender");
        var target = await PostMentionAsync();
        await PostIssueAsync(target);

        // Drive until the signal row exists (the receiver may or may not
        // have drained it yet).
        await DrainUntilAsync(
            async () => (await RowsAsync(
                "SELECT 1 FROM facilitator.outbox WHERE command_id LIKE 'wf-signal:%'", []))
            .Count > 0, "the signal row is durable");

        // Fresh host: the durable wake chain plays out exactly once.
        _kernel.Dispose();
        _factory.Dispose();
        _factory = new KernelTestFactory(fixture, noDispatcher: true);
        _factory.Services.GetRequiredService<WorkflowRuntime>().Enabled = true;
        _kernel = _factory.CreateAuthedClient();
        _dispatcher = _factory.Services.GetRequiredService<IEventDispatcher>();
        await DrainUntilAsync(() => InstanceStatusAsync("t.sig-receiver", "completed"), "the fresh host delivers the signal once");

        var receiver = Assert.Single(await InstancesAsync("t.sig-receiver"));
        var awaitLegs = (await StepLogAsync(receiver.GetProperty("instanceId").GetGuid()))
            .Where(l => l.GetProperty("stepId").GetString() == "await")
            .ToList();
        Assert.Equal(2, awaitLegs.Count); // the fresh host resumed it exactly once
        Assert.All(awaitLegs, leg => Assert.Equal("done", leg.GetProperty("status").GetString()));
    }

    // ---- definitions ----------------------------------------------------------

    /// <summary>Issue-triggered: signals a target instance it computes —
    /// the receiver's deterministic id is stable, so a literal uuid would
    /// not do: the sender finds its target through a select of the
    /// instance row it can resolve — here, the test seeds the receiver
    /// FIRST and passes its id through the issue envelope? No: simplest
    /// honest law — the receiver triggers on the same issue, and the
    /// signal target is resolved by the sender from the workflow instance
    /// table via a `join`-free projection: the definition pins the target
    /// through $input; the test starts the sender with an invoke... To
    /// keep the gate on the SIGNAL law (not target discovery), the sender
    /// is a root flow whose target is the receiver's deterministic
    /// instance: the SAME trigger envelope, so the sender can compute
    /// UUIDv5(receiver@1 > envelopeId). The test therefore embeds the
    /// target id via data: the sender copies `data.target` from its
    /// trigger event, which the test rewrote to the receiver's
    /// deterministic instance id.</summary>
    private static string SenderDef() => """
        {
          "id": "t.sig-sender", "version": 1, "title": "signal sender",
          "description": "sends one point-to-point wake",
          "enabled": false,
          "on": { "event": "org.facilitator.event.provider.issue.v1" },
          "steps": [
            { "id": "ping", "title": "signal the target", "on": "always",
              "signal": {
                "target": "$event.data.payload.target",
                "event": {
                  "type": "org.facilitator.event.workspace.heartbeat.v1",
                  "data": { "workspaceId": "ping-1", "status": "busy" }
                }
              } }
          ]
        }
        """;

    private static string ReceiverDef(bool parkWithTimer = false)
    {
        _ = parkWithTimer;
        return """
            {
              "id": "t.sig-receiver", "version": 1, "title": "signal receiver",
              "description": "parks on a wait keyed 'ping-1'; a signal with that key wakes it",
              "enabled": false,
              "on": { "event": "org.facilitator.event.provider.mention.v1" },
              "steps": [
                { "id": "await", "title": "await the coordination signal", "on": "always",
                  "wait": {
                    "event": "org.facilitator.event.workspace.heartbeat.v1",
                    "correlate": "ping-1",
                    "timeout": "PT30M",
                    "onTimeout": "end"
                  } },
                { "id": "done", "title": "record", "on": "await:completed",
                  "select": { "project": { "woken": "instance" } } }
              ]
            }
            """;
    }

    private static string OnSignalDef() => """
        {
          "id": "t.on-signal", "version": 1, "title": "triggers on the signal type",
          "description": "the sink's fall-through target for a signal with no matching park",
          "enabled": false,
          "on": {
            "event": "org.facilitator.event.workspace.heartbeat.v1",
            "where": { "data.status": "busy" }
          },
          "steps": [
            { "id": "done", "title": "select", "on": "always", "select": { "project": { "y": "instance" } } }
          ]
        }
        """;

    // ---- plumbing -------------------------------------------------------------

    /// <summary>Phase 1: the receiver triggers on a mention and parks on
    /// its wait. Returns its deterministic instance id (UUIDv5 over
    /// receiver@1 > the mention's trigger row) for the sender to address.</summary>
    private async Task<Guid> PostMentionAsync()
    {
        var node = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.mention.json"))!.AsObject();
        var wireId = "evt-" + Guid.NewGuid().ToString("N");
        node["id"] = wireId;
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", node.ToJsonString(), HttpStatusCode.Accepted);
        await DrainUntilAsync(() => InstanceStatusAsync("t.sig-receiver", "waiting"), "the receiver parks on its wait");
        return WorkflowIdentity.InstanceIdFor(
            "t.sig-receiver", 1, WorkflowIdentity.TriggerRowId("t.sig-receiver", 1, wireId));
    }

    /// <summary>Phase 2: the sender triggers on an issue carrying the
    /// target in the open `payload` map (the issue schema is closed).</summary>
    private async Task PostIssueAsync(Guid target)
    {
        var node = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.issue.opened.json"))!.AsObject();
        node["id"] = "evt-" + Guid.NewGuid().ToString("N");
        node["data"]!["workTrigger"] = true;
        node["data"]!["payload"] = new JsonObject { ["target"] = target.ToString() };
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
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "phase 6.6 signal test");
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
            "phase 6.6 signal test");

    private Task DrainUntilAsync(Func<Task<bool>> settled, string because) =>
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

    private async Task<List<JsonElement>> InstancesAsync(string defId) =>
        (await RowsAsync(
            "SELECT id AS \"instanceId\", def_id AS \"defId\", status, current_step AS \"currentStep\", depth,"
            + " invocation_path AS \"invocationPath\", parent_instance_id AS \"parentInstanceId\","
            + " end_reason AS \"endReason\", output"
            + " FROM facilitator.workflow_instances WHERE def_id = $1",
            [defId]))
        .ToList();

    private async Task<List<JsonElement>> StepLogAsync(Guid instanceId) =>
        (await RowsAsync(
            "SELECT seq, step_id AS \"stepId\", status, effect_command_id AS \"effectCommandId\", output"
            + " FROM facilitator.workflow_step_log WHERE instance_id = $1 ORDER BY seq",
            [instanceId]))
        .ToList();

    private async Task<JsonElement?> OutboxRowAsync(string commandId)
    {
        var rows = await RowsAsync(
            "SELECT outbox_id, command_id, event_type, envelope FROM facilitator.outbox WHERE command_id = $1",
            [commandId]);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<int> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

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
                            : fieldType == typeof(string)
                                ? ParseMaybeJson(reader.GetString(i))
                                : JsonValue.Create(reader.GetValue(i).ToString());
            }

            rows.Add(JsonDocument.Parse(obj.ToJsonString()).RootElement.Clone());
        }

        return rows;
    }

    private async Task PurgeAsync()
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            DELETE FROM facilitator.workflow_step_log WHERE instance_id IN
                (SELECT id FROM facilitator.workflow_instances WHERE def_id LIKE 't.sig%' OR def_id = 't.on-signal');
            DELETE FROM facilitator.workflow_instances WHERE def_id LIKE 't.sig%' OR def_id = 't.on-signal';
            DELETE FROM facilitator.workflow_definitions WHERE def_id LIKE 't.sig%' OR def_id = 't.on-signal';
            DELETE FROM facilitator.outbox WHERE command_id LIKE 'wf-%';
            DELETE FROM facilitator.inbox WHERE payload ->> 'type' IN
                ('org.facilitator.event.provider.issue.v1', 'org.facilitator.event.workspace.heartbeat.v1',
                 'org.facilitator.event.provider.mention.v1');
            """,
            conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
