using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Workflows;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.Tests.Integrations;

/// <summary>
/// The assign-rules SWAP (Phase 5): a per-provider config rule imports an
/// enabled scoped copy of the gallery template; a live copy makes the
/// labeled-issue arm defer — the plane creates the Work, the binding, the
/// progress note and the routing intent through the shared WorkCreate law,
/// and the arm's judgment names the plane's DETERMINISTIC work id
/// (recomputable here from (envelope id → trigger row → instance → step)).
/// Shadow posture hands the behavior back to the arm while the plane
/// records a would-do plan; plane-off leaves the arm byte-identical (the
/// neighboring <see cref="AssignRuleRoutingTests"/> are that gate).
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class AssignRulesSwapTests(DispatchDbFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Repo = "demo/widgets";
    private const string RuleDefId = "assign-rule-forgejo-1";

    private readonly List<KernelTestFactory> _factories = [];

    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await fixture.Db.PurgeFleets("gw-coding-");
        await fixture.Db.PurgeFleets("gw-global-");
        // Imported rows outlive a config deletion by law; in tests they
        // outlive the class for hygiene (the shared DB sees other
        // collections' provider configs, whose boot importers would
        // otherwise match a foreign row).
        await ExecuteAsync("DELETE FROM facilitator.workflow_definitions WHERE def_id LIKE 'assign-rule-forgejo-%'");
    }

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }
    }

    [Fact]
    public async Task LiveImportedCopyRunsTheFlow_TheArmDefersAndNamesTheDeterministicWork()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var (link, adapterEndpoint, factory) = await PlaneAsync(plane: true, shadow: false, codingEndpoint);
        await FleetAsync(codingEndpoint, "coding.implement");
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";
        var envelope = IssueEvent(delivery, Repo, issue, "alice", label: "ofac:coding");

        var posted = await link.PostAsync("/api/link/v1/events", Content(envelope));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        // The identity chain, recomputed from the delivery alone (the
        // convergence law): envelope id -> trigger row -> instance -> step.
        var wireId = JsonDocument.Parse(envelope).RootElement.GetProperty("id").GetString()!;
        var triggerRow = WorkflowIdentity.TriggerRowId(RuleDefId, 1, wireId);
        var instance = WorkflowIdentity.InstanceIdFor(RuleDefId, 1, triggerRow);
        var planeWork = WorkflowIdentity.WorkCreateWorkId(instance, AssignRulesTemplate.CreateStepId);

        // The arm DEFERRED: its judgment names the plane's deterministic
        // work (the row itself lands when the plane runs), and the binding
        // the plane created agrees.
        var judgment = await JudgmentAsync(delivery);
        Assert.Equal($"work/{planeWork} (plane: {RuleDefId})", judgment);
        var bound = await RoutingHarness.DrainUntilAsync(
            factory,
            async () => await fixture.Db.BindingWorkId("forgejo", Repo, issue) is { } w ? w.ToString("D") : null,
            "plane-created binding");
        var boundWork = Guid.Parse(bound);
        Assert.Equal(planeWork, boundWork);

        // The plane's routing intent produced the SAME assign the arm used
        // to append inline: rule capability + rule endpoint preference +
        // repo/issue re-hydration inputs, rule command-id family.
        var assign = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{planeWork:N}"),
            "plane coding assign effect");
        using var doc = JsonDocument.Parse(assign);
        var data = doc.RootElement.GetProperty("data");
        Assert.Equal("coding.implement", data.GetProperty("capabilityId").GetString());
        Assert.Equal("forgejo-interim", data.GetProperty("targetId").GetString());
        Assert.Equal(Repo, data.GetProperty("inputs").GetProperty("repo").GetString());
        Assert.Equal(issue, data.GetProperty("inputs").GetProperty("issue").GetString());
        var audit = await fixture.Db.RoutingDecision($"routing:work-assign-coding:{planeWork:N}");
        Assert.NotNull(audit);
        Assert.Equal("preference-satisfied", audit!.Basis);
        Assert.Equal(codingEndpoint, audit.ChosenEndpointId);

        // The ack: the flow's communication.post rides the ADAPTER's
        // endpoint (the arm's AppendCommand posture), addressed to the
        // issue's conversation, with the step effect command id.
        var ack = await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand(
                WorkflowIdentity.StepCommandId(RuleDefId, 1, instance, "ack", 2)),
            "plane thread ack");
        using var ackDoc = JsonDocument.Parse(ack);
        Assert.Equal("On it.", ackDoc.RootElement.GetProperty("data").GetProperty("text").GetString());
        Assert.Equal(issue, ackDoc.RootElement.GetProperty("data").GetProperty("target").GetProperty("rootThreadId").GetString());
        Assert.Equal(Repo, ackDoc.RootElement.GetProperty("data").GetProperty("target").GetProperty("channelOrResourceId").GetString());
        var recipients = await fixture.Db.OutboxRecipients(planeWork);
        Assert.Contains($"link.{adapterEndpoint}", recipients);

        // The instance ran to completion with both legs logged; the work
        // carries the created row + the residence document.
        await DrainUntilAsync(factory, async () =>
        {
            var rows = await RowsAsync(
                "SELECT jsonb_build_object('status', status) FROM facilitator.workflow_instances WHERE id = $1::uuid",
                [instance.ToString("D")]);
            return rows.Count == 1 && rows[0].GetProperty("status").GetString() == "completed";
        }, "the flow completed");
        var log = await RowsAsync(
            "SELECT jsonb_build_object('stepId', step_id, 'status', status) FROM facilitator.workflow_step_log"
            + " WHERE instance_id = $1::uuid ORDER BY seq",
            [instance.ToString()]);
        Assert.Equal(["create", "ack"], log.Select(l => l.GetProperty("stepId").GetString()));
        var workRow = Assert.Single(await RowsAsync(
            "SELECT jsonb_build_object('state', state, 'objective', objective, 'origin', origin, 'instance', workflow_instance_id)"
            + " FROM facilitator.works WHERE work_id = $1::uuid",
            [planeWork.ToString()]));
        Assert.Equal("open", workRow.GetProperty("state").GetString());
        Assert.StartsWith("Issue demo/widgets#", workRow.GetProperty("objective").GetString());
        Assert.Equal("org.facilitator.event.provider.issue.v1", workRow.GetProperty("origin").GetString());
        Assert.Equal(instance, Guid.Parse(workRow.GetProperty("instance").GetString()!));

        // The import itself: enabled, derived, one version.
        var imported = Assert.Single(await RowsAsync(
            "SELECT jsonb_build_object('enabled', enabled, 'source', source) FROM facilitator.workflow_definitions WHERE def_id = $1",
            [RuleDefId]));
        Assert.True(imported.GetProperty("enabled").GetBoolean());
        Assert.Equal("derived", imported.GetProperty("source").GetString());
    }

    [Fact]
    public async Task ShadowCopyKeepsTheArm_ThePlaneOnlyRecords()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var (link, _, factory) = await PlaneAsync(plane: true, shadow: true, codingEndpoint);
        await FleetAsync(codingEndpoint, "coding.implement");
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";
        var envelope = IssueEvent(delivery, Repo, issue, "alice", label: "ofac:coding");

        var posted = await link.PostAsync("/api/link/v1/events", Content(envelope));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

        // The arm performed the creation: a fresh guid (NOT the plane's
        // deterministic id), the plain judgment, the arm's inline ack.
        var bound = await RoutingHarness.DrainUntilAsync(
            factory,
            async () => await fixture.Db.BindingWorkId("forgejo", Repo, issue) is { } w ? w.ToString("D") : null,
            "arm-created binding");
        var boundWork = Guid.Parse(bound);
        var wireId = JsonDocument.Parse(envelope).RootElement.GetProperty("id").GetString()!;
        var planeWork = WorkflowIdentity.WorkCreateWorkId(
            WorkflowIdentity.InstanceIdFor(
                RuleDefId, 1, WorkflowIdentity.TriggerRowId(RuleDefId, 1, wireId)),
            AssignRulesTemplate.CreateStepId);
        Assert.NotEqual(planeWork, boundWork);
        Assert.Equal($"work/{boundWork}", await JudgmentAsync(delivery));
        await RoutingHarness.DrainUntilAsync(
            factory,
            () => fixture.Db.OutboxEnvelopeByCommand($"work-assign-coding:{boundWork:N}"),
            "arm coding assign effect");

        // The plane observed and recorded a would-do shadow (no effects of
        // its own: the shadow posture writes step-log rows only).
        var wireId2 = JsonDocument.Parse(envelope).RootElement.GetProperty("id").GetString()!;
        var shadowInstance = WorkflowIdentity.InstanceIdFor(
            RuleDefId, 1, WorkflowIdentity.TriggerRowId(RuleDefId, 1, wireId2));
        await DrainUntilAsync(factory, async () =>
        {
            var rows = await RowsAsync(
                "SELECT jsonb_build_object('status', status) FROM facilitator.workflow_instances WHERE id = $1::uuid",
                [shadowInstance.ToString("D")]);
            return rows.Count == 1 && rows[0].GetProperty("status").GetString() == "shadowed";
        }, "the shadow instance records the plan");
        var planeEffects = await RowsAsync(
            "SELECT jsonb_build_object('n', count(*)) FROM facilitator.outbox WHERE command_id LIKE $1",
            [$"wf:{RuleDefId}@1:{shadowInstance:N}:%"]); // step effect ids carry the instance
        Assert.Equal(0, planeEffects[0].GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task PlaneOffAppendsNoTriggerRows_TheArmRunsItsOwnPath()
    {
        var codingEndpoint = "gw-coding-" + Guid.NewGuid().ToString("N")[..6];
        var (link, _, factory) = await PlaneAsync(plane: false, shadow: false, codingEndpoint);
        await FleetAsync(codingEndpoint, "coding.implement");
        var issue = NewIssueNumber();
        var delivery = $"dlv-{Guid.NewGuid():N}";
        var envelope = IssueEvent(delivery, Repo, issue, "alice", label: "ofac:coding");

        var posted = await link.PostAsync("/api/link/v1/events", Content(envelope));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var bound = await RoutingHarness.DrainUntilAsync(
            factory,
            async () => await fixture.Db.BindingWorkId("forgejo", Repo, issue) is { } w ? w.ToString("D") : null,
            "arm binding");
        var boundWork = Guid.Parse(bound);
        Assert.Equal($"work/{boundWork}", await JudgmentAsync(delivery));
        // THIS event's trigger row (deterministic from the envelope id):
        // the plane-off emitter appended nothing (shared-DB hygiene: a
        // LIKE-count would see a sibling test's rows).
        var wireIdOff = JsonDocument.Parse(envelope).RootElement.GetProperty("id").GetString()!;
        var triggerRow = WorkflowIdentity.TriggerRowId(RuleDefId, 1, wireIdOff);
        var triggerRows = await RowsAsync(
            "SELECT jsonb_build_object('n', count(*)) FROM facilitator.outbox WHERE outbox_id = $1::uuid",
            [triggerRow.ToString("D")]);
        Assert.Equal(0, triggerRows[0].GetProperty("n").GetInt32());
    }

    // ---- harness ------------------------------------------------------------

    private async Task<(HttpClient Link, string AdapterEndpoint, KernelTestFactory Factory)> PlaneAsync(
        bool plane, bool shadow, string codingEndpoint)
    {
        var endpoint = "gw-fj-" + Guid.NewGuid().ToString("N")[..8];
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = endpoint,
            ["Providers:Forgejo:Tenant"] = "git.localhost",
            ["Providers:Forgejo:SiteUrl"] = "http://forgejo:3000",
            ["Providers:Forgejo:BotUserId"] = "ofac-bot",
            ["Providers:Forgejo:AssignRules:0:Label"] = "ofac:coding",
            ["Providers:Forgejo:AssignRules:0:CapabilityId"] = "coding.implement",
            ["Providers:Forgejo:AssignRules:0:EndpointId"] = codingEndpoint,
        };
        if (plane)
        {
            settings["Facilitator:Workflows:Enabled"] = "true";
        }

        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: settings);
        _factories.Add(factory);
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(factory.CreateAuthedClient(), endpoint));
        // The import rides the boot seeder's StartAsync: wait for the row,
        // then FORCE the canonical shape — the shared collection DB sees
        // other classes' boots (e.g. PrDispatchTests carries a different
        // forgejo rule), whose importer wins the ON CONFLICT race on a
        // first boot. Re-inserting the expected payload makes the row this
        // test observes deterministic regardless of boot order (the knob
        // resets per boot, then sets shadow when asked).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (await ExecuteScalarAsync(
                   $"SELECT count(*) FROM facilitator.workflow_definitions WHERE def_id = '{RuleDefId}'") == "0")
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("the assign-rules importer never ran at boot");
            }

            await Task.Delay(50);
        }

        await ExecuteAsync($"""
            DELETE FROM facilitator.workflow_definitions WHERE def_id = '{RuleDefId}';
            INSERT INTO facilitator.workflow_definitions
                (def_id, version, title, description, payload, digest, enabled, shadow, source, team, created_at, created_by, next_tick_at)
            VALUES
                ('{RuleDefId}', 1, 'Assign rule ofac:coding (forgejo rule 1)',
                 'test-canonical import shape',
                 '{CanonicalPayload(codingEndpoint).Replace("'", "''")}'::jsonb,
                 'sha256:0000000000000000000000000000000000000000000000000000000000000000',
                 true, {(shadow ? "true" : "false")}, 'derived', NULL, now(), 'importer', NULL)
            """);

        return (link, endpoint, factory);
    }

    /// <summary>The canonical imported shape for THIS test's config:
    /// template + rule baked (capability, preference, interim target,
    /// labels matcher) — the same fields the boot importer writes, with
    /// this test's rule endpoint.</summary>
    private static string CanonicalPayload(string codingEndpoint) =>
        """
        {"description":"test-canonical import shape","enabled":true,"id":"assign-rule-forgejo-1","on":{"event":"org.facilitator.event.provider.issue.v1","where":{"data.action":"labeled","data.labels":"ofac:coding","data.provider":"forgejo","data.workTrigger":"true"}},"steps":[{"id":"create","on":"always","title":"Create the work + routing intent","work-create":{"capability":"coding.implement","commandBase":"work-assign-coding","objectiveRef":"${event.data.text}","inputs":{"issue":"${event.data.reference.rootThreadId}","repo":"${event.data.reference.channelOrResourceId}"},"preference":"@@EP@@","target":"forgejo-interim"}},{"id":"ack","on":"create:completed","emit":{"command":"org.facilitator.command.communication.post.v1","inputs":{"text":"On it."}},"title":"Ack the thread"}],"title":"Assign rule ofac:coding (forgejo rule 1)","version":1}
        """.Replace("@@EP@@", codingEndpoint, StringComparison.Ordinal);

    private Task FleetAsync(string endpoint, string capability) =>
        FleetHarness.SeedFleetAsync(fixture.Db, endpoint, "team-alpha", capability);

    private async Task<string?> JudgmentAsync(string deliveryId)
    {
        var rows = await RowsAsync(
            "SELECT jsonb_build_object('j', verified_reference) FROM facilitator.provider_events"
            + " WHERE provider = 'forgejo' AND provider_delivery_id = $1",
            [deliveryId]);
        return rows.Count == 0 ? null : rows[0].GetProperty("j").GetString();
    }

    private static string NewIssueNumber() =>
        new Random().Next(10000, 99999).ToString();

    private static StringContent Content(string envelope) => new(envelope, Encoding.UTF8, "application/json");

    /// <summary>The forgejo labeled-issue fixture with a fresh delivery/issue
    /// id and the normalized label set (the kernel reads
    /// <c>data.labels</c>, never the vendor payload).</summary>
    private static string IssueEvent(string deliveryId, string repo, string number, string actor, string label)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.issue.labeled.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        data["providerDeliveryId"] = deliveryId;
        data["actorProviderAccountId"] = actor;
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        reference["authorProviderAccountId"] = actor;
        var payload = data["payload"]!.AsObject();
        payload["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
        payload["label"] = new JsonObject { ["name"] = label };
        payload["issue"] = new JsonObject
        {
            ["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture),
            ["labels"] = new JsonArray(new JsonObject { ["name"] = label }),
        };

        // The adapter-normalized field: the kernel reads this, never the payload.
        data["labels"] = new JsonArray(label);
        return doc.ToJsonString();
    }

    private static async Task DrainUntilAsync(
        KernelTestFactory factory, Func<Task<bool>> settled, string because)
    {
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            if (await settled())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"condition not met within 30 s: {because}");
    }

    private async Task<List<JsonElement>> RowsAsync(string sql, object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            cmd.Parameters.AddWithValue(parameter);
        }

        var results = new List<JsonElement>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(JsonDocument.Parse(reader.GetFieldValue<string>(0)).RootElement.Clone());
        }

        return results;
    }

    private async Task<string> ExecuteScalarAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        return (await cmd.ExecuteScalarAsync())?.ToString() ?? "0";
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync();
    }
}
