using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Workflows;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// W1 workflow kill-window across the seam: a flow triggers off a live
/// conversation (issue → bound work), parks on a wait (the half-advanced
/// durable state), and the process dies with the wake row still pending in
/// the outbox. A new process boots on the same database and finishes the
/// job exactly once: one instance, the parked leg closed done, one effect
/// command row, one <c>workflow.stage</c> row — and a second drain after
/// completion changes nothing (the read-back law, not a memory). Before
/// this walk no test crossed a restart with a half-run flow.
/// Segments: WorkflowRuntimeTests.WaitLegParks + RestartNoDoubleAskWalkTests
/// (restart posture) + ReviewForkWalkTests (binding-follows-conversation).
/// </summary>
[Collection("postgres:walks")]
public sealed class WorkflowKillWindowWalkTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DefId = "t.kill-window";

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        using var boot = new KernelTestFactory(fixture, noDispatcher: true);
        boot.CreateClient(); // boots migrations on the shared DB
        await PurgeAsync();
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await PurgeAsync();

    [Fact]
    public async Task HalfRunFlowCompletesExactlyOnceAcrossRestart()
    {
        Guid instanceId;

        // Before the restart: trigger, park, wake row pending, no delivery.
        using (var factoryA = new KernelTestFactory(fixture, noDispatcher: true))
        {
            factoryA.Services.GetRequiredService<WorkflowRuntime>().Enabled = true;
            var kernel = factoryA.CreateAuthedClient();
            var dispatcher = factoryA.Services.GetRequiredService<IEventDispatcher>();
            await StoreFlowAsync(kernel);

            var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "kill-window subject");
            await SeedBindingAsync(workId);

            await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
                IssueEnvelope(), HttpStatusCode.Accepted);
            await dispatcher.DrainOnceAsync(CancellationToken.None);

            var parked = await InstancesAsync();
            instanceId = Assert.Single(parked).GetProperty("instanceId").GetGuid();
            Assert.Equal("waiting", parked[0].GetProperty("status").GetString());

            // The correlating mention arrives; its wake row is durable but
            // UNDELIVERED — the process dies here (factory disposed, the
            // dispatcher never drains it).
            await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
                MentionEnvelope(correlating: true), HttpStatusCode.Accepted);
            Assert.Equal(1, await WakeRowCountAsync());
        }

        // After the restart: the new process drains, resumes, completes —
        // exactly once.
        using var factoryB = new KernelTestFactory(fixture, noDispatcher: true);
        factoryB.Services.GetRequiredService<WorkflowRuntime>().Enabled = true;
        var dispatcherB = factoryB.Services.GetRequiredService<IEventDispatcher>();

        await dispatcherB.DrainOnceAsync(CancellationToken.None);
        var completed = Assert.Single(await InstancesAsync());
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal(instanceId, completed.GetProperty("instanceId").GetGuid());

        var log = await StepLogAsync(instanceId);
        Assert.Equal(["w", "w", "s"], log.Select(l => l.GetProperty("stepId").GetString()));
        Assert.All(log, row => Assert.Equal("done", row.GetProperty("status").GetString()));

        // Exactly one effect, one timeline row, one wake row (the
        // outbox-command-id uniqueness the plan names).
        var posts = await RowsAsync(
            "SELECT jsonb_build_object('commandId', command_id) FROM facilitator.outbox" +
            " WHERE event_type = 'org.facilitator.command.communication.post.v1'" +
            "   AND command_id LIKE 'wf:%'", DefId);
        var post = Assert.Single(posts);
        Assert.EndsWith(":s:3", post.GetProperty("commandId").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, await WakeRowCountAsync());
        Assert.Single(await RowsAsync(
            """
            SELECT jsonb_build_object('e', event_id) FROM facilitator.work_events
            WHERE kind = 'workflow.stage' AND correlation_id = $1
            """, instanceId.ToString("N")));

        // A second drain after completion: no new rows, status unchanged.
        await dispatcherB.DrainOnceAsync(CancellationToken.None);
        Assert.Equal("completed", Assert.Single(await InstancesAsync()).GetProperty("status").GetString());
        Assert.Single(await RowsAsync(
            "SELECT jsonb_build_object('c', command_id) FROM facilitator.outbox" +
            " WHERE event_type = 'org.facilitator.command.communication.post.v1'" +
            "   AND command_id LIKE 'wf:%'", DefId));
    }

    // ---- helpers (self-contained: the walks ship public infra only) ----------

    private static async Task StoreFlowAsync(HttpClient kernel)
    {
        var body = $$"""
        {
          "id": "{{DefId}}", "version": 1, "title": "Kill window",
          "description": "walk: a half-run flow completes once across a restart",
          "on": { "event": "org.facilitator.event.provider.issue.v1", "where": { "data.action": "opened" } },
          "steps": [
            { "id": "w", "title": "await a mention",
              "wait": { "event": "org.facilitator.event.provider.mention.v1",
                        "correlate": "$event.data.reference.rootThreadId",
                        "timeout": "PT1H", "onTimeout": "end" } },
            { "id": "s", "title": "ack", "on": "w:completed",
              "emit": { "command": "org.facilitator.command.communication.post.v1", "inputs": { "text": "done after restart" } } }
          ]
        }
        """;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/workflows/definitions/{DefId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "kill-window walk");
        using var stored = await kernel.SendAsync(request);
        Assert.True(
            stored.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"store failed: {(int)stored.StatusCode} {await stored.Content.ReadAsStringAsync()}");
        await KernelOps.PostJsonAsync(kernel,
            $"/api/admin/workflows/definitions/{DefId}/versions/1/toggle", """{"enabled": true}""",
            HttpStatusCode.OK, "kill-window walk");
    }

    private static string IssueEnvelope()
    {
        var node = JsonNode.Parse(
            PostgresFixture.ReadFixture("valid/event.provider.issue.opened.json"))!.AsObject();
        node["id"] = "evt-" + Guid.NewGuid().ToString("N");
        node["data"]!["workTrigger"] = true;
        return node.ToJsonString();
    }

    private static string MentionEnvelope(bool correlating)
    {
        var node = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.mention.json"))!.AsObject();
        var data = node["data"]!.AsObject();
        node["id"] = "evt-" + Guid.NewGuid().ToString("N");
        data["providerDeliveryId"] = Guid.NewGuid().ToString("N");
        data["actorProviderAccountId"] = Guid.NewGuid().ToString("N");
        var reference = data["reference"]!.AsObject();
        reference["providerInstanceId"] = Guid.NewGuid().ToString("N");
        reference["tenantOrOrganizationId"] = Guid.NewGuid().ToString("N");
        reference["channelOrResourceId"] = Guid.NewGuid().ToString("N");
        reference["rootThreadId"] = Guid.NewGuid().ToString("N");
        reference["parentMessageId"] = Guid.NewGuid().ToString("N");
        reference["messageId"] = correlating ? "1" : Guid.NewGuid().ToString("N");
        reference["authorProviderAccountId"] = Guid.NewGuid().ToString("N");
        data["text"] = "ack";
        data.Remove("payload");
        return node.ToJsonString();
    }

    private async Task SeedBindingAsync(Guid workId) =>
        await ExecuteAsync(
            """
            INSERT INTO facilitator.communication_bindings
                (binding_id, provider, channel, root_message_id, work_id, created_at)
            VALUES ($1, 'forgejo', 'demo/widgets', '1', $2, now())
            """, [Guid.NewGuid(), workId]);

    private Task<long> WakeRowCountAsync() =>
        ScalarLongAsync("SELECT count(*) FROM facilitator.outbox WHERE command_id LIKE 'wf-wake:%'");

    private async Task PurgeAsync()
    {
        await ExecuteAsync(
            """
            DELETE FROM facilitator.workflow_step_log WHERE instance_id IN
                (SELECT id FROM facilitator.workflow_instances WHERE def_id = $1);
            DELETE FROM facilitator.workflow_instances WHERE def_id = $1;
            DELETE FROM facilitator.workflow_definitions WHERE def_id = $1;
            DELETE FROM facilitator.work_events WHERE kind = 'workflow.stage';
            DELETE FROM facilitator.outbox WHERE command_id LIKE 'wf:%' OR command_id LIKE 'wf-%'
               OR event_type = 'org.facilitator.command.communication.post.v1';
            DELETE FROM facilitator.inbox WHERE payload ->> 'type' IN
                ('org.facilitator.event.provider.issue.v1', 'org.facilitator.event.provider.mention.v1');
            DELETE FROM facilitator.communication_bindings
                WHERE provider = 'forgejo' AND channel = 'demo/widgets' AND root_message_id = '1'
                  AND work_id IN (SELECT work_id FROM facilitator.works WHERE objective = 'kill-window subject');
            """, [DefId]);
    }

    private async Task<List<JsonElement>> InstancesAsync() =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('instanceId', id, 'status', status, 'currentStep', current_step)
            FROM facilitator.workflow_instances WHERE def_id = $1
            """, DefId);

    private async Task<List<JsonElement>> StepLogAsync(Guid instanceId) =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('seq', seq, 'stepId', step_id, 'status', status)
            FROM facilitator.workflow_step_log WHERE instance_id = $1 ORDER BY seq
            """, instanceId);

    private async Task<List<JsonElement>> RowsAsync(string sql, object parameter)
    {
        await using var connection = await OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(parameter);
        var results = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(JsonDocument.Parse(reader.GetFieldValue<string>(0)).RootElement.Clone());
        }

        return results;
    }

    private async Task ExecuteAsync(string sql, object[] parameters)
    {
        await using var connection = await OpenAsync();
        foreach (var statement in sql.Split(';'))
        {
            if (statement.Trim().Length == 0)
            {
                continue;
            }

            await using var command = new Npgsql.NpgsqlCommand(statement, connection);
            foreach (var parameter in parameters)
            {
                command.Parameters.AddWithValue(parameter);
            }

            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> ScalarLongAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<Npgsql.NpgsqlConnection> OpenAsync()
    {
        var connection = new Npgsql.NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
