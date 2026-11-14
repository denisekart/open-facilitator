using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Workflows;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// W5 assign-rules swap equivalence: the SAME labeled-issue event runs the
/// creation twice — once through the ARM (plane off), once through the
/// PLANE (the imported <c>assign-rule-forgejo-1</c> copy live). The
/// receipts agree where they must: a Work + binding + <c>created</c> row +
/// routing intent + progress echo + the arm's inline ack / the flow's ack
/// leg, all addressed to the same endpoints with the same rule
/// contribution (capability, preference satisfied, repo/issue inputs, the
/// <c>work-assign-coding:</c> family). The plane run adds exactly the
/// <c>workflow.stage</c> rows on top, and the arm's judgment for the plane
/// run names the plane's DETERMINISTIC work id (recomputed from the
/// delivery: envelope id → trigger row → instance → step).
/// Segments: AssignRuleRoutingTests (the arm's shape) +
/// AssignRulesSwapTests (the unit posture) + ReviewForkSwapWalkTests (the
/// equivalence-of-receipts walk law).
/// </summary>
[Collection("postgres:walks")]
public sealed class AssignRulesSwapWalkTests(PostgresFixture fixture)
{
    private const string Repo = "walk-widgets";

    /// <summary>The scenario: one labeled issue, two postures. The rule
    /// (label <c>ofac:coding</c>, capability <c>coding.implement</c>,
    /// preferred endpoint) is configured identically in both boots.</summary>
    [Fact]
    public async Task PlaneCreationMatchesArmCreationPlusStageRows()
    {
        var coding = $"walk-ep{WalkKit.Leaf()}";
        var adapterA = $"walk-ad{WalkKit.Leaf()}";
        var adapterB = $"walk-ad{WalkKit.Leaf()}";

        // ---- Run A: the arm performs the creation (plane off).
        Guid workA;
        string judgmentA;
        using (var factoryA = Boot(plane: false, coding, adapterA))
        {
            var kernel = factoryA.CreateAuthedClient(); // boots migrations
            await fixture.Db.SeedProviderIdentity("forgejo", "git.localhost", "acct-1", "subject-1");
            var link = factoryA.LinkClient(await WalkKit.EnrollAsync(kernel, adapterA));
            var dispatcher = factoryA.Services.GetRequiredService<IEventDispatcher>();

            // One fleet row serves both runs (the DB is shared across the
            // boots; a second seed would collide on the endpoint key).
            await WalkKit.SeedFleetAsync(fixture.Db, coding, "team-alpha", "coding.implement");

            var issueA = new Random().Next(10000, 99999).ToString();
            var (envelopeA, deliveryA) = IssueEvent(issueA, "ofac:coding");
            var posted = await link.PostAsync("/api/link/v1/events", WalkKit.Content(envelopeA));
            Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
            workA = await DrainBindingAsync(dispatcher, issueA);
            judgmentA = await JudgmentAsync(deliveryA);
            Assert.Equal($"work/{workA}", judgmentA);
            await DrainUntilAsync(dispatcher, async () =>
                (await OutboxRowsAsync(workA)).Any(r =>
                    r.GetProperty("commandId").GetString()!.StartsWith("work-assign-coding:", StringComparison.Ordinal)),
                "the funnel produced the arm's coding assign");
        }

        var createdA = await OutboxRowsAsync(workA);
        var timelineA = await TimelineAsync([workA]);

        // ---- Run B: the plane performs it (imported copy live). The arm
        // defers; the judgment must NAME the deterministic work before the
        // plane even runs.
        using var factoryB = Boot(plane: true, coding, adapterB);
        {
            var kernel = factoryB.CreateAuthedClient();
            var link = factoryB.LinkClient(await WalkKit.EnrollAsync(kernel, adapterB));
            var dispatcher = factoryB.Services.GetRequiredService<IEventDispatcher>();

            var issueB = new Random().Next(10000, 99999).ToString();
            var (envelopeB, deliveryB) = IssueEvent(issueB, "ofac:coding");
            var posted = await link.PostAsync("/api/link/v1/events", WalkKit.Content(envelopeB));
            Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);

            var wireId = JsonDocument.Parse(envelopeB).RootElement.GetProperty("id").GetString()!;
            var triggerRow = WorkflowIdentity.TriggerRowId("assign-rule-forgejo-1", 1, wireId);
            var instanceB = WorkflowIdentity.InstanceIdFor("assign-rule-forgejo-1", 1, triggerRow);
            var workB = WorkflowIdentity.WorkCreateWorkId(instanceB, AssignRulesTemplate.CreateStepId);

            // The swap posture, asserted from the delivery alone BEFORE
            // draining: the arm judged the plane's deterministic work.
            var judgment = await JudgmentAsync(deliveryB);
            Assert.Equal($"work/{workB} (plane: assign-rule-forgejo-1)", judgment);

            await DrainUntilAsync(dispatcher, async () =>
                (await OutboxRowsAsync(workB)).Any(r =>
                    r.GetProperty("commandId").GetString()!.StartsWith("work-assign-coding:", StringComparison.Ordinal)),
                "the funnel produced the plane's coding assign");
            var createdB = await OutboxRowsAsync(workB);
            var timelineB = await TimelineAsync([workB]);

            // The assign effect: same rule contribution, same family,
            // preference satisfied on the rule endpoint (the funnel ran
            // for both postures — the effect is the funnel's, so the
            // equality is structural: command id per work + recipients).
            var assignA = Single(createdA, "work-assign-coding");
            var assignB = Single(createdB, "work-assign-coding");
            Assert.Equal("coding.implement", Capability(assignA));
            Assert.Equal("coding.implement", Capability(assignB));
            Assert.Equal(Repo, Input(assignB, "repo"));
            Assert.Equal(issueB, Input(assignB, "issue"));
            Assert.Contains($"link.{coding}", Recipients(createdB));

            // The ack reaches the adapter endpoint in both postures (the
            // arm's inline append vs the flow's emit leg).
            var adapterRecipientsA = Recipients(createdA).Where(r => r.StartsWith("link.walk-ad", StringComparison.Ordinal)).ToList();
            var adapterRecipientsB = Recipients(createdB).Where(r => r.StartsWith("link.walk-ad", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(adapterRecipientsA);
            Assert.NotEmpty(adapterRecipientsB);

            // Progress echo: one row each, same type + recipient + note
            // (recipients differ only by the per-run adapter endpoint
            // token: compare identity-normalized).
            var progressA = Single(createdA, "work.progress");
            var progressB = Single(createdB, "work.progress");
            Assert.Equal(
                CrossBlind(Normalize(progressA.GetProperty("recipient").GetString()!, "run-A", [workA])),
                CrossBlind(Normalize(progressB.GetProperty("recipient").GetString()!, "run-B", [workB, instanceB])));
            Assert.Equal(
                CrossBlind(Normalize(progressA.GetProperty("note").GetString()!, "run-A", [workA])),
                CrossBlind(Normalize(progressB.GetProperty("note").GetString()!, "run-B", [workB, instanceB])));

            // The timeline: the plane adds the stage rows and nothing else
            // differs (per-run identity normalized, stage rows dropped from
            // B before the compare).
            var withoutStage = timelineB
                .Where(r => r.GetProperty("kind").GetString() != "workflow.stage")
                .Select(r => CrossBlind(Normalize(r.ToString(), "run-B", [workB, instanceB])))
                .ToList();
            var normalizedA = timelineA
                .Select(r => CrossBlind(Normalize(r.ToString(), "run-A", [workA])))
                .ToList();
            Assert.True(
                normalizedA.SequenceEqual(withoutStage),
                $"arm timeline != plane timeline (minus stage rows):\nA: {string.Join("\n", normalizedA)}\nB: {string.Join("\n", withoutStage)}");
            Assert.Contains(timelineB, r => r.GetProperty("kind").GetString() == "workflow.stage");

            // The instance completed with both legs (the flow's own story).
            var log = await RowsAsync(
                "SELECT jsonb_build_object('stepId', step_id, 'status', status)"
                + " FROM facilitator.workflow_step_log WHERE instance_id = $1::uuid ORDER BY seq",
                instanceB.ToString("D"));
            Assert.Equal(["create", "ack"], log.Select(l => l.GetProperty("stepId").GetString()));
        }
    }

    // ---- harness ------------------------------------------------------------

    private KernelTestFactory Boot(bool plane, string codingEndpoint, string adapterEndpoint)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Providers:Forgejo:Enabled"] = "true",
            ["Providers:Forgejo:EndpointId"] = adapterEndpoint,
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
        return factory;
    }

    private async Task<Guid> DrainBindingAsync(IEventDispatcher dispatcher, string issue)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await dispatcher.DrainOnceAsync(CancellationToken.None);
            if (await fixture.Db.BindingWorkId("forgejo", Repo, issue) is { } workId)
            {
                return workId;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("the labeled issue never produced a bound work");
    }

    private async Task DrainUntilAsync(IEventDispatcher dispatcher, Func<Task<bool>> settled, string because)
    {
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

    /// <summary>The labeled-issue fixture rewritten for a fresh issue with
    /// the normalized label set.</summary>
    private static (string Envelope, string Delivery) IssueEvent(string number, string label)
    {
        var doc = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.provider.issue.labeled.json"))!.AsObject();
        doc["id"] = "evt-" + Guid.NewGuid().ToString("N");
        var data = doc["data"]!.AsObject();
        var delivery = "walk-dlv" + Guid.NewGuid().ToString("N")[..12];
        data["providerDeliveryId"] = delivery;
        data["actorProviderAccountId"] = "acct-1";
        var reference = data["reference"]!.AsObject();
        reference["channelOrResourceId"] = Repo;
        reference["rootThreadId"] = number;
        reference["subjectRef"] = number;
        var payload = data["payload"]!.AsObject();
        payload["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture);
        payload["label"] = new JsonObject { ["name"] = label };
        payload["issue"] = new JsonObject
        {
            ["number"] = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture),
            ["labels"] = new JsonArray(new JsonObject { ["name"] = label }),
        };

        data["labels"] = new JsonArray(label);
        return (doc.ToJsonString(), delivery);
    }

    private async Task<string> JudgmentAsync(string delivery)
    {
        var rows = await RowsAsync(
            "SELECT jsonb_build_object('j', verified_reference) FROM facilitator.provider_events"
            + " WHERE provider = 'forgejo' AND provider_delivery_id = $1",
            delivery);
        return Assert.Single(rows).GetProperty("j").GetString()!;
    }

    private Task<List<JsonElement>> OutboxRowsAsync(Guid workId) =>
        RowsAsync(
            """
            SELECT jsonb_build_object('commandId', command_id, 'type', event_type,
                                      'recipient', recipient_endpoint_id,
                                      'capability', envelope::jsonb -> 'data' ->> 'capabilityId',
                                      'note', envelope::jsonb -> 'data' ->> 'progressNote',
                                      'inputs', envelope::jsonb -> 'data' -> 'inputs')
            FROM facilitator.outbox WHERE aggregate_id = $1::uuid
            """, workId.ToString("D"));

    private Task<List<JsonElement>> TimelineAsync(Guid[] workIds) =>
        RowsAsync(
            """
            SELECT jsonb_build_object('kind', kind, 'actor', actor, 'detail', detail::text)
            FROM facilitator.work_events WHERE work_id = ANY($1::uuid[]) ORDER BY created_at
            """, workIds);

    /// <summary>Effect rows match by command-id PREFIX (the intent row
    /// carries the same fragment inside `routing:`, the progress row by
    /// its event type).</summary>
    private static JsonElement Single(List<JsonElement> rows, string commandFragment) =>
        Assert.Single(rows, r => r.GetProperty("commandId").GetString()!.StartsWith(commandFragment, StringComparison.Ordinal)
                              || r.GetProperty("type").GetString() == $"org.facilitator.event.{commandFragment}.v1");

    private static List<string> Recipients(List<JsonElement> rows) =>
        rows.Select(r => r.GetProperty("recipient").GetString()!).ToList();

    private static string? Capability(JsonElement row) => row.GetProperty("capability").GetString();

    private static string? Input(JsonElement row, string key) =>
        row.GetProperty("inputs").TryGetProperty(key, out var v) ? v.GetString() : null;

    private static string Normalize(string text, string runToken, IReadOnlyList<Guid> ids)
    {
        foreach (var id in ids)
        {
            text = text
                .Replace(id.ToString(), runToken)
                .Replace(id.ToString("N"), runToken);
        }

        return System.Text.RegularExpressions.Regex.Replace(
            text,
            "walk-dlv[0-9a-f]+|walk-ep[0-9a-f]+|walk-ad[0-9a-f]+|evt-[0-9a-f-]+|#[0-9]{5}|\"[0-9]{5}\"|run-[AB]"
            + "|[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            "@");
    }

    private static string CrossBlind(string normalized) =>
        normalized.Replace("run-A", "@").Replace("run-B", "@");

    private async Task<List<JsonElement>> RowsAsync(string sql, params object[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        var rows = new List<JsonElement>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(JsonDocument.Parse(reader.GetFieldValue<string>(0)).RootElement.Clone());
        }

        return rows;
    }
}
