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
/// Phase 1 of the workflow plane: the runtime skeleton. Import the
/// hand-written test flow, enable it, seed a <c>provider.issue</c> envelope
/// through the existing ingress path, drive the dispatcher: exactly one
/// instance (<c>completed</c>), step log seq 1-2, the <c>wf:…</c> effect
/// command id, one outbox command row carrying the projected text, one
/// <c>workflow.stage</c> timeline row; a duplicate delivery (same wire id)
/// starts nothing new (the duplicate law at the inbox). Wait/approval legs
/// park and resume; expiry rides the approval sweeper into the plane's
/// wake leg and takes the failed edge; the sweeper's deadline pass fires
/// timers. Boot law: while <c>Enabled</c> is false the emitter appends
/// nothing. Joins <c>postgres:persistence</c> (schema-bound rows on the
/// shared DB, no new fixture).
/// </summary>
[Collection("postgres:persistence")]
public sealed class WorkflowRuntimeTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string DefId = "t.emit-on-issue";

    private KernelTestFactory _factory = null!;
    private HttpClient _kernel = null!;
    private IEventDispatcher _dispatcher = null!;

    /// <summary>The plan's proof flow, loaded from its own file (the file is
    /// the artifact, not an inline literal).</summary>
    internal static string TestFlowJson => File.ReadAllText(
        Path.Combine(RepoRoot(), "tests", "Facilitator.Tests", "Workflows", "flow.emit-on-issue.json"));

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
    public async Task TriggerRunsTheFlowOnce_AndDuplicateDeliveryIsNoop()
    {
        await StoreAsync(TestFlowJson);
        await EnableAsync();

        // Work-context resolution (load-bearing): the issue's conversation
        // is bound to a work, so the instance runs WITH a subject work and
        // its timeline rows land on it.
        var (workId, _) = await KernelOps.CreateWorkAsync(_kernel, "flow subject");
        await SeedBindingAsync(workId);

        var issue = Reissue(PostgresFixture.ReadFixture("valid/event.provider.issue.opened.json"));
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", issue, HttpStatusCode.Accepted);
        await DrainUntilAsync(() => InstanceCountAsync(1), "issue triggers one instance");

        var firstInstances = await InstancesAsync();
        var instance = Assert.Single(firstInstances);
        Assert.Equal("completed", instance.GetProperty("status").GetString());
        Assert.Equal(workId, instance.GetProperty("primaryWorkId").GetGuid());

        var log = await StepLogAsync(instance.GetProperty("instanceId").GetGuid());
        Assert.Equal(2, log.Count);
        Assert.Equal(1, log[0].GetProperty("seq").GetInt32());
        Assert.Equal("s1", log[0].GetProperty("stepId").GetString());
        Assert.Equal("done", log[0].GetProperty("status").GetString());
        Assert.Equal("s2", log[1].GetProperty("stepId").GetString());
        var effectCommandId = log[1].GetProperty("effectCommandId").GetString()!;
        Assert.StartsWith($"wf:{DefId}@1:", effectCommandId, StringComparison.Ordinal);
        Assert.EndsWith(":s2:2", effectCommandId, StringComparison.Ordinal);

        // One communication.post outbox row with the projected text.
        var post = Assert.Single(await OutboxAsync("org.facilitator.command.communication.post.v1"));
        Assert.Equal(effectCommandId, post.GetProperty("commandId").GetString());
        Assert.Equal(
            "opened",
            post.GetProperty("envelope").GetProperty("data").GetProperty("text").GetString());

        // One workflow.stage timeline row, correlated to the instance.
        var stages = await StageRowsAsync(
            instance.GetProperty("instanceId").GetGuid().ToString("N"));
        Assert.NotEmpty(stages);

        // The duplicate law: the SAME wire event re-POSTed is an inbox
        // duplicate (200 duplicate, not 202) — no new trigger row, no
        // second instance.
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", issue, HttpStatusCode.OK);
        await DrainUntilAsync(() => InstanceCountAsync(1), "duplicate starts nothing");
        Assert.Single(await InstancesAsync());
    }

    [Fact]
    public async Task WaitLegParksThenASecondEventResumesIt()
    {
        await StoreAsync(Variant("""
        { "on": { "event": "org.facilitator.event.provider.issue.v1", "where": { "data.action": "opened" } },
          "steps": [
            { "id": "w", "title": "await a mention",
              "wait": { "event": "org.facilitator.event.provider.mention.v1",
                        "correlate": "$event.data.reference.rootThreadId",
                        "timeout": "PT1H", "onTimeout": "end" } },
            { "id": "s", "title": "ack", "on": "w:completed",
              "emit": { "command": "org.facilitator.command.communication.post.v1", "inputs": { "text": "resumed" } } }
          ] }
        """));
        await EnableAsync();

        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", IssueEnvelope(), HttpStatusCode.Accepted);
        await DrainUntilAsync(() => InstanceStatusAsync(0, "waiting"), "wait parks the instance");

        var parked = Assert.Single(await InstancesAsync());
        Assert.Equal("w", parked.GetProperty("currentStep").GetString());
        Assert.NotEqual(JsonValueKind.Null, parked.GetProperty("deadlineAt").ValueKind);

        // An unrelated mention (different conversation, no field carrying
        // the parked key "1") must not wake it.
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events",
            MentionEnvelope(unrelated: true), HttpStatusCode.Accepted);
        await DrainUntilAsync(() => InstanceStatusAsync(0, "waiting"), "unrelated mention leaves the park");
        // One more settled beat: the unrelated event had its chance to
        // produce a wake row and be delivered.
        await _dispatcher.DrainOnceAsync(CancellationToken.None);
        Assert.Equal("waiting", Assert.Single(await InstancesAsync()).GetProperty("status").GetString());

        // The correlating mention: the envelope's data carries the parked
        // correlation key (the trigger's rootThreadId "1").
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events",
            MentionEnvelope(unrelated: false), HttpStatusCode.Accepted);
        await DrainUntilAsync(() => InstanceStatusAsync(0, "completed"), "correlating mention resumes the park");

        var done = Assert.Single(await InstancesAsync());
        var log = await StepLogAsync(done.GetProperty("instanceId").GetGuid());
        Assert.Equal(["w", "w", "s"], log.Select(l => l.GetProperty("stepId").GetString()));
        Assert.Equal("done", log[1].GetProperty("status").GetString()); // the parked leg closed done
    }

    [Fact]
    public async Task ApprovalLegResumesOnRespondAndExpiryTakesTheFailedEdge()
    {
        await StoreAsync(Variant("""
        { "on": { "event": "org.facilitator.event.provider.issue.v1", "where": { "data.action": "opened" } },
          "steps": [
            { "id": "a", "title": "ask",
              "approval": { "question": "proceed?", "approvers": "group:/teams/team-alpha" } },
            { "id": "g", "title": "go", "on": "a:completed", "select": { "project": { "ok": "instance" } } },
            { "id": "e", "title": "end failed", "on": "failed", "select": { "project": {} } }
          ] }
        """));
        await EnableAsync();
        var (subjectWork, _) = await KernelOps.CreateWorkAsync(_kernel, "approval subject");
        await SeedBindingAsync(subjectWork, "approval subject");

        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", IssueEnvelope(), HttpStatusCode.Accepted);
        await DrainUntilAsync(() => InstanceStatusAsync(0, "waiting"), "approval parks the instance");

        var parked = Assert.Single(await InstancesAsync());
        var instanceId = parked.GetProperty("instanceId").GetGuid();
        var approvalId = Guid.Parse(JsonDocument.Parse(
            (await StepLogAsync(instanceId))[0].GetProperty("output").GetRawText())
            .RootElement.GetProperty("approvalId").GetString()!);

        // The respond leg: an eligible team member decides through the REST
        // surface; the ledger's decision observer appends the wake row on
        // that transaction, delivery resumes the park on the completed
        // edge.
        var alice = await CreateUserClientAsync("alice");
        var decided = await KernelOps.PostJsonAsync(alice,
            $"/api/works/{subjectWork}/approvals/{approvalId}/respond",
            """{"decision":"approved","comment":"go"}""", HttpStatusCode.OK);
        Assert.Equal("approved", decided.GetProperty("state").GetString());
        await DrainUntilAsync(() => InstanceStatusAsync(0, "completed"), "respond wake resumes the park");

        var resumed = Assert.Single(await InstancesAsync());
        var log = await StepLogAsync(instanceId);
        Assert.Equal(["a", "a", "g"], log.Select(l => l.GetProperty("stepId").GetString()));

        // The expiry leg: a second run parks; the approval deadline passes
        // (both rows share the instant in production), the sweeper's
        // deadline pass closes the park and takes the failed edge — an
        // unanswered consent is a human silence, never a silent stall.
        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", IssueEnvelope(), HttpStatusCode.Accepted);
        await DrainUntilAsync(() => InstanceCountAsync(2), "second run starts");
        var second = (await InstancesAsync()).Single(i => i.GetProperty("instanceId").GetGuid() != instanceId);
        Assert.Equal("waiting", second.GetProperty("status").GetString());
        await ExpireDeadlineAsync(second.GetProperty("instanceId").GetGuid());
        var sweeper = _factory.Services.GetRequiredService<WorkflowSweeper>();
        Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None));

        var failed = (await InstancesAsync()).Single(i => i.GetProperty("instanceId").GetGuid() != instanceId);
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        var secondLog = await StepLogAsync(second.GetProperty("instanceId").GetGuid());
        Assert.Equal(["a", "a", "e"], secondLog.Select(l => l.GetProperty("stepId").GetString()));
    }

    [Fact]
    public async Task TimerLegCompletesOnTheSweeperDeadlinePass()
    {
        await StoreAsync(Variant("""
        { "on": { "event": "org.facilitator.event.provider.issue.v1", "where": { "data.action": "opened" } },
          "steps": [
            { "id": "t", "title": "delay", "timer": { "for": "PT1S" } },
            { "id": "s", "title": "after", "on": "t:completed", "select": { "project": { "who": "instance" } } }
          ] }
        """));
        await EnableAsync();

        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", IssueEnvelope(), HttpStatusCode.Accepted);
        await DrainUntilAsync(() => InstanceStatusAsync(0, "waiting"), "timer parks the instance");
        var waiting = Assert.Single(await InstancesAsync());

        var sweeper = _factory.Services.GetRequiredService<WorkflowSweeper>();
        Assert.Equal(0, await sweeper.SweepOnceAsync(CancellationToken.None)); // not due yet
        await Task.Delay(1200); // the PT1S deadline passes
        Assert.Equal(1, await sweeper.SweepOnceAsync(CancellationToken.None));

        var completed = Assert.Single(await InstancesAsync());
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        var log = await StepLogAsync(completed.GetProperty("instanceId").GetGuid());
        Assert.Equal(["t", "t", "s"], log.Select(l => l.GetProperty("stepId").GetString()));
    }

    [Fact]
    public async Task DisabledPlaneEmitsNoTriggerRows()
    {
        _factory.Services.GetRequiredService<WorkflowRuntime>().Enabled = false;
        await StoreAsync(TestFlowJson);
        await EnableAsync(); // the DEFINITION enables; the plane stays off

        await KernelOps.PostJsonAsync(_kernel, "/api/ingress/events", IssueEnvelope(), HttpStatusCode.Accepted);
        await _dispatcher.DrainOnceAsync(CancellationToken.None);

        Assert.Empty(await InstancesAsync());
        await using var connection = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM facilitator.outbox WHERE recipient_endpoint_id = 'workflow.kernel'", connection);
        Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>Drain repeatedly until <paramref name="settled"/> holds:
    /// production's dispatcher polls continuously, and a plane row a
    /// foreign test host's dispatcher claimed first requeues (the
    /// disabled-sink no-route law), so a single drain would be a race, not
    /// a behavior.</summary>
    private Task DrainUntilAsync(Func<Task<bool>> settled, string because) =>
        KernelOps.PollUntilAsync(
            async () =>
            {
                await _dispatcher.DrainOnceAsync(CancellationToken.None);
                return await settled();
            },
            because,
            20_000);

    private async Task<bool> InstanceCountAsync(int expected) =>
        (await InstancesAsync()).Count == expected;

    private async Task<bool> InstanceStatusAsync(int index, string status)
    {
        var instances = await InstancesAsync();
        return index < instances.Count
               && instances[index].GetProperty("status").GetString() == status;
    }

    private Facilitator.Kernel.Work.ApprovalSweeper factory_Sweeper() =>
        _factory.Services.GetRequiredService<Facilitator.Kernel.Work.ApprovalSweeper>();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenFacilitator.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }

    /// <summary>A full definition document around a trigger+steps object
    /// (merged member-wise), pinned to the shared proof-flow id.</summary>
    private static string Variant(string fragment)
    {
        var document = new JsonObject
        {
            ["id"] = DefId,
            ["version"] = 1,
            ["title"] = "Runtime test flow",
            ["description"] = "phase 1 runtime proof",
        };
        foreach (var member in JsonNode.Parse(fragment)!.AsObject())
        {
            document[member.Key] = member.Value?.DeepClone();
        }

        return document.ToJsonString();
    }

    private async Task StoreAsync(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/workflows/definitions/{DefId}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        request.Headers.TryAddWithoutValidation("X-Change-Reason", "phase 1 runtime test");
        using var response = await _kernel.SendAsync(request);
        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
            $"store failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
    }

    private async Task EnableAsync() =>
        await KernelOps.PostJsonAsync(_kernel,
            $"/api/admin/workflows/definitions/{DefId}/versions/1/toggle", """{"enabled": true}""",
            HttpStatusCode.OK, "phase 1 runtime test");

    private static string IssueEnvelope() =>
        Reissue(PostgresFixture.ReadFixture("valid/event.provider.issue.opened.json"));

    /// <summary>Fresh wire id, and the adapter's trigger flag on (the
    /// fixture models an unlabelled issue; this flow triggers on the
    /// labelled shape).</summary>
    private static string Reissue(string fixtureEnvelope)
    {
        var node = JsonNode.Parse(fixtureEnvelope)!.AsObject();
        node["id"] = "evt-" + Guid.NewGuid().ToString("N");
        node["data"]!["workTrigger"] = true;
        return node.ToJsonString();
    }

    // Built on the mention fixture's own strings (which contain digits):
    // the "unrelated" variant carries only guid-shaped strings; the
    // correlating variant names the parked key ("1") in data.messageId.
    private static string MentionEnvelope(bool unrelated)
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
        reference["messageId"] = Guid.NewGuid().ToString("N");
        reference["authorProviderAccountId"] = Guid.NewGuid().ToString("N");
        data["text"] = "ack";
        data.Remove("payload");
        if (!unrelated)
        {
            reference["messageId"] = "1"; // carries the parked correlation key
        }

        return node.ToJsonString();
    }

    // The forgejo conversation of the issue fixture (demo/widgets #1) bound
    // to a live work — the arms' own binding table.
    private async Task SeedBindingAsync(Guid workId, string objective = "flow subject") =>
        await ExecuteAsync(
            """
            INSERT INTO facilitator.communication_bindings
                (binding_id, provider, channel, root_message_id, work_id, created_at)
            VALUES ($1, 'forgejo', 'demo/widgets', '1', $2, now())
            """, [Guid.NewGuid(), workId]);

    // Both sides of a park share the instant; production sets them equal
    // (approval expiry == the park's deadline), the test moves them as one.
    private Task ExpireDeadlineAsync(Guid instanceId) =>
        ExecuteAsync(
            """
            UPDATE facilitator.workflow_instances SET deadline_at = now() - interval '1 minute' WHERE id = $1;
            UPDATE facilitator.approvals SET expires_at = now() - interval '1 minute'
              WHERE requested_by = 'workflow:kernel'
            """, [instanceId]);

    private async Task<HttpClient> CreateUserClientAsync(string user)
    {
        var client = _factory.CreateClient();
        var token = await KeycloakFixture.TokenForUserAsync(user, user);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private async Task PurgeAsync()
    {
        await ExecuteAsync(
            """
            DELETE FROM facilitator.workflow_step_log WHERE instance_id IN
                (SELECT id FROM facilitator.workflow_instances WHERE def_id = $1);
            DELETE FROM facilitator.workflow_instances WHERE def_id = $1;
            DELETE FROM facilitator.workflow_definitions WHERE def_id = $1;
            DELETE FROM facilitator.work_events WHERE kind = 'workflow.stage';
            DELETE FROM facilitator.outbox WHERE command_id LIKE 'wf:%'
               OR command_id LIKE 'wf-%'
               OR event_type = 'org.facilitator.command.communication.post.v1';
            DELETE FROM facilitator.approvals WHERE requested_by = 'workflow:kernel';
            DELETE FROM facilitator.communication_bindings
                WHERE provider = 'forgejo' AND channel = 'demo/widgets' AND root_message_id = '1'
                  AND work_id IN (SELECT work_id FROM facilitator.works
                      WHERE objective IN ('flow subject', 'approval subject'));
            DELETE FROM facilitator.inbox WHERE payload ->> 'type' IN
                ('org.facilitator.event.provider.issue.v1', 'org.facilitator.event.provider.mention.v1');
            """, [DefId]);
    }

    private async Task ExecuteAsync(string sql, object[] parameters)
    {
        await using var connection = await OpenAsync();

        // One command per statement: Npgsql rejects multi-command prepared
        // statements.
        foreach (var statement in sql.Split(';'))
        {
            if (statement.Trim().Length == 0)
            {
                continue;
            }

            await using var cmd = new NpgsqlCommand(statement, connection);
            foreach (var parameter in parameters)
            {
                cmd.Parameters.AddWithValue(parameter);
            }

            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<List<JsonElement>> InstancesAsync() =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('instanceId', id, 'status', status, 'currentStep', current_step,
                                      'deadlineAt', deadline_at, 'output', output::jsonb,
                                      'primaryWorkId', primary_work_id)
            FROM facilitator.workflow_instances WHERE def_id = $1 ORDER BY started_at
            """, DefId);

    private async Task<List<JsonElement>> StepLogAsync(Guid instanceId) =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('seq', seq, 'stepId', step_id, 'status', status,
                                      'effectCommandId', effect_command_id, 'output', output::jsonb)
            FROM facilitator.workflow_step_log WHERE instance_id = $1 ORDER BY seq
            """, instanceId);

    private async Task<List<JsonElement>> OutboxAsync(string eventType) =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('commandId', command_id, 'envelope', envelope::jsonb)
            FROM facilitator.outbox WHERE event_type = $1
            """, eventType);

    private async Task<List<JsonElement>> StageRowsAsync(string instanceCorrelation) =>
        await RowsAsync(
            """
            SELECT jsonb_build_object('kind', kind, 'detail', detail::jsonb)
            FROM facilitator.work_events
            WHERE kind = 'workflow.stage' AND correlation_id = $1
              AND detail::jsonb ->> 'note' LIKE '%communication.post%'
            """, instanceCorrelation);

    private async Task<List<JsonElement>> RowsAsync(string sql, object parameter)
    {
        await using var connection = await OpenAsync();
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

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
