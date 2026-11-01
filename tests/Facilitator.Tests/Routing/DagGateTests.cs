using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Work;


namespace Facilitator.Tests.Routing;

/// <summary>
/// The dependsOn DAG gate (Plan 35 B): a create-batch sibling whose edges are
/// unmet keeps its routing intent pending with the dispatcher's backoff and
/// the audit basis <c>deps-unmet</c> — never an assign effect, never a
/// dead-letter. Satisfaction is a wake law: a sibling's terminal event (the
/// completed arm walks the row to terminal and pulls the gated intents
/// forward) or a manifest re-marked <c>validated</c> releases the gate NOW,
/// not at backoff. Create-time DAG law: a cycle, an unknown condition, or an
/// unknown sibling index rejects the WHOLE POST /api/works with 422
/// <c>dependency-violation</c> and creates nothing. Assignment identities are
/// deterministic UUIDv5 over the effect command id, so the wire
/// <c>dependsOn.index</c> resolves before the funnel ever runs.
/// Hermeticity: every test owns its team + fleet (<c>dg.</c> endpoints, purged
/// on dispose), and every gated/release assertion polls — foreign
/// dispatchers over the shared outbox claim intents too, which only ever adds
/// requeue attempts.
/// </summary>
[Collection("postgres:dispatch-link")]
public sealed class DagGateTests(DispatchDbFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task DisposeAsync() =>
        // A leaked live fleet would join a later run's candidate set.
        fixture.Db.PurgeFleets("dg.");

    [Fact]
    public async Task CompletedSiblingReleasesGatedAssignWithDeterministicIdentities()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var endpoint = $"dg.a{Leaf()}";
        await FleetHarness.SeedFleetAsync(fixture.Db, endpoint, team, "research.analyze", "research.deliver");
        var projectId = await fixture.Db.SeedProject(team);
        var workId = await CreateAsync(kernel, projectId,
            Sibling("research.analyze"),
            Sibling("research.deliver", (0, "completed")));

        // Sibling 1 (index 0, no deps) routes immediately: effect + audit +
        // the ledger row at its deterministic identity.
        var first = await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 0)), "first assign effect");
        Assert.Equal("org.facilitator.command.work.assign.v1", JsonDocument.Parse(first).RootElement.GetProperty("type").GetString());
        var audit0 = await fixture.Db.RoutingDecision(IntentCommand(workId, 0));
        Assert.Equal("routed", audit0!.Status);
        Assert.Equal(endpoint, audit0.ChosenEndpointId);
        Assert.Single(await fixture.Db.AssignmentRowsForWork(workId));

        // Sibling 2 is gated: requeued with backoff, audit basis deps-unmet,
        // no assign effect, no dead-letter.
        var gated = await AssertGatedAsync(factory, workId, 1, minAttempts: 2);
        Assert.Equal("[]", gated.AllowedCandidates);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 1)));

        // The intent envelope carries the RESOLVED edge: the sibling index
        // became that sibling's deterministic assignment identity at create.
        var intentEnvelope = JsonDocument.Parse((await fixture.Db.OutboxEnvelopeByCommand(IntentCommand(workId, 1)))!);
        var depId0 = ExpectedId(workId, 0);
        var edge = Assert.Single(JsonDocument
            .Parse(intentEnvelope.RootElement.GetProperty("data").GetProperty("dependsOnJson").GetString()!)
            .RootElement.EnumerateArray());
        Assert.Equal(depId0.ToString(), edge.GetProperty("assignmentId").GetString());
        Assert.Equal("completed", edge.GetProperty("condition").GetString());
        Assert.False(JsonDocument.Parse((await fixture.Db.OutboxEnvelopeByCommand(IntentCommand(workId, 0)))!)
            .RootElement.GetProperty("data").TryGetProperty("dependsOnJson", out _));

        // The wake: sibling 1's completed event walks its row to terminal and
        // pulls the gated intent forward; the funnel re-evaluates NOW.
        await CompleteAsync(kernel, workId, depId0);
        var second = await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 1)), "released assign effect");
        var released = await fixture.Db.RoutingDecision(IntentCommand(workId, 1));
        Assert.Equal("routed", released!.Status);
        Assert.Equal(endpoint, released.ChosenEndpointId);

        // Both assignments exist at their deterministic ids; the released row
        // completed its dep, the sibling routed at generation 1.
        var ids = (await fixture.Db.AssignmentIdsForWork(workId)).Select(Guid.Parse).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { depId0, ExpectedId(workId, 1) }.OrderBy(x => x).ToArray(), ids);
        Assert.Equal(
            new[] { ("completed", "research.analyze", 1), ("routed", "research.deliver", 1) },
            (await fixture.Db.AssignmentRowsForWork(workId)).OrderBy(r => r.Capability, StringComparer.Ordinal).ToArray());

        var envelope = JsonDocument.Parse(second).RootElement;
        Assert.Equal("org.facilitator.command.work.assign.v1", envelope.GetProperty("type").GetString());
        Assert.Equal($"work/{workId}/assignment/{ExpectedId(workId, 1):N}", envelope.GetProperty("subject").GetString());
        Assert.Equal("research.deliver", envelope.GetProperty("data").GetProperty("capabilityId").GetString());
        Assert.Contains($"link.{endpoint}", await fixture.Db.OutboxRecipients(workId));
        KernelOps.AssertEnvelopesValid(await fixture.Db.OutboxEnvelopes(workId));
    }

    [Fact]
    public async Task ValidatedResultGateHoldsUntilManifestReMark()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var endpoint = $"dg.v{Leaf()}";
        await FleetHarness.SeedFleetAsync(fixture.Db, endpoint, team, "research.analyze", "research.deliver");
        var projectId = await fixture.Db.SeedProject(team);
        var workId = await CreateAsync(kernel, projectId,
            Sibling("research.analyze"),
            Sibling("research.deliver", (0, "result_validated")));

        var first = await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 0)), "first assign effect");
        Assert.NotNull(first);

        // Completion alone does NOT release a result_validated edge: the
        // sibling is terminal (wake fires) but the gate holds with a live
        // candidate standing by.
        var depId0 = ExpectedId(workId, 0);
        await CompleteAsync(kernel, workId, depId0);
        Assert.Contains(("completed", "research.analyze", 1), await fixture.Db.AssignmentRowsForWork(workId));
        await AssertGatedAsync(factory, workId, 1, minAttempts: 2);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 1)));

        // A RECORDED-but-not-validated manifest is still unmet.
        var manifestId = $"man-dg-{Leaf()}";
        await fixture.Db.SeedAssignmentManifest(manifestId, workId, depId0, "available");
        await AssertGatedAsync(factory, workId, 1, minAttempts: 4);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 1)));

        // The admin re-mark flips the result state and wakes the gate in the
        // same mutation: released NOW.
        var marked = await KernelOps.PostJsonAsync(kernel, $"/api/admin/manifests/{manifestId}/re-mark",
            """{"resultState": "validated"}""", HttpStatusCode.OK);
        Assert.Equal("validated", marked.GetProperty("resultState").GetString());
        Assert.Equal("validated", await fixture.Db.ManifestResultState(manifestId));

        var second = await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 1)), "validated-release assign effect");
        Assert.Equal("research.deliver", JsonDocument.Parse(second).RootElement
            .GetProperty("data").GetProperty("capabilityId").GetString());
        Assert.Equal("routed", (await fixture.Db.RoutingDecision(IntentCommand(workId, 1)))!.Status);
        KernelOps.AssertEnvelopesValid(await fixture.Db.OutboxEnvelopes(workId));
    }

    [Fact]
    public async Task DiamondReleasesLevelsInDependencyOrder()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var endpoint = $"dg.d{Leaf()}";
        await FleetHarness.SeedFleetAsync(fixture.Db, endpoint, team,
            "work.plan", "work.build", "work.test", "work.integrate");
        var projectId = await fixture.Db.SeedProject(team);
        var workId = await CreateAsync(kernel, projectId,
            Sibling("work.plan"),
            Sibling("work.build", (0, "completed")),
            Sibling("work.test", (0, "completed")),
            Sibling("work.integrate", (1, "completed"), (2, "completed")));

        // A routes; B and C gate on it; D gates on B AND C — dep rows that do
        // not exist yet are honestly unmet, so D waits from the first pass.
        await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 0)), "A assign effect");
        await AssertGatedAsync(factory, workId, 1, minAttempts: 2);
        await AssertGatedAsync(factory, workId, 2, minAttempts: 2);
        await AssertGatedAsync(factory, workId, 3, minAttempts: 2);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 3)));

        // A completes: both middle siblings release (one wake, two intents).
        await CompleteAsync(kernel, workId, ExpectedId(workId, 0));
        await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 1)), "B assign effect");
        await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 2)), "C assign effect");

        // One middle edge satisfied is not both: D holds until the second completes.
        await AssertGatedAsync(factory, workId, 3, minAttempts: 3);
        await CompleteAsync(kernel, workId, ExpectedId(workId, 1));
        await AssertGatedAsync(factory, workId, 3, minAttempts: 4);
        Assert.Null(await fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 3)));

        await CompleteAsync(kernel, workId, ExpectedId(workId, 2));
        var last = await RoutingHarness.DrainUntilAsync(
            factory, () => fixture.Db.OutboxEnvelopeByCommand(EffectCommand(workId, 3)), "D assign effect");
        Assert.Equal("work.integrate", JsonDocument.Parse(last).RootElement
            .GetProperty("data").GetProperty("capabilityId").GetString());

        // The ledger truth: every level released in order, four deterministic
        // identities, one effect envelope per sibling, all to the same fleet.
        Assert.Equal(
            new[]
            {
                ("completed", "work.build", 1), ("routed", "work.integrate", 1),
                ("completed", "work.plan", 1), ("completed", "work.test", 1),
            },
            (await fixture.Db.AssignmentRowsForWork(workId)).OrderBy(r => r.Capability, StringComparer.Ordinal).ToArray());
        var ids = (await fixture.Db.AssignmentIdsForWork(workId)).Select(Guid.Parse).OrderBy(x => x).ToArray();
        Assert.Equal(Enumerable.Range(0, 4).Select(i => ExpectedId(workId, i)).OrderBy(x => x).ToArray(), ids);
        var envelopes = await fixture.Db.OutboxEnvelopes(workId);
        Assert.Equal(4, envelopes.Count(e => e.Contains("command.work.assign.v1", StringComparison.Ordinal)));
        Assert.Contains($"link.{endpoint}", await fixture.Db.OutboxRecipients(workId));
        KernelOps.AssertEnvelopesValid(envelopes);
    }

    [Fact]
    public async Task CyclicBatchIsRejectedAndCreatesNothing()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var projectId = await fixture.Db.SeedProject(team);

        var (status, problem) = await PostCreateAsync(kernel, projectId,
            Sibling("work.build", (1, "completed")),
            Sibling("work.test", (0, "completed")));
        AssertDagRejected(problem, status, "dependency cycle");
        await AssertProjectEmptyAsync(kernel, projectId);
    }

    [Fact]
    public async Task UnknownConditionIsRejectedNamingTheEdge()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var projectId = await fixture.Db.SeedProject(team);

        var (status, problem) = await PostCreateAsync(kernel, projectId,
            Sibling("work.build"),
            Sibling("work.test", (0, "whenever")));
        AssertDagRejected(problem, status, "assignments[1].dependsOn");
        Assert.Contains("condition must be one of", problem.GetProperty("detail").GetString());
        await AssertProjectEmptyAsync(kernel, projectId);
    }

    [Fact]
    public async Task UnknownSiblingIndexIsRejectedNamingTheEdge()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        using var kernel = factory.CreateAuthedClient();
        var team = await FreshTeamAsync();
        var projectId = await fixture.Db.SeedProject(team);

        var (status, problem) = await PostCreateAsync(kernel, projectId,
            Sibling("work.build"),
            Sibling("work.test", (7, "completed")));
        AssertDagRejected(problem, status, "index 7 is not a sibling");
        await AssertProjectEmptyAsync(kernel, projectId);
    }

    // --- plumbing -------------------------------------------------------------

    private static string Leaf() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>A per-test unique roster team: the cross-team filter makes
    /// every other suite's enrollments invisible to this test's works.</summary>
    private async Task<string> FreshTeamAsync()
    {
        var slug = $"dg-w-{Leaf()}";
        await fixture.Db.SeedTeam(slug);
        return slug;
    }

    private static string EffectCommand(Guid workId, int index) =>
        $"work-assign:{RoutingIntent.CreateEffectKey(workId, index)}";

    private static string IntentCommand(Guid workId, int index) => $"routing:{EffectCommand(workId, index)}";

    /// <summary>The identity the DAG law promises the wire: UUIDv5 over the
    /// sibling's effect command id, derivable before the funnel runs.</summary>
    private static Guid ExpectedId(Guid workId, int index) =>
        RoutingIntent.AssignmentIdFor(EffectCommand(workId, index));

    private static JsonObject Sibling(string capabilityId, params (int Index, string Condition)[] dependsOn)
    {
        var item = new JsonObject
        {
            ["capabilityId"] = capabilityId,
            ["objective"] = $"run {capabilityId}",
            ["targetId"] = "kernel",
            ["actorEvidenceRef"] = "identity-evidence:dag-suite",
        };
        if (dependsOn.Length > 0)
        {
            var deps = new JsonArray();
            foreach (var (index, condition) in dependsOn)
            {
                deps.Add(new JsonObject { ["index"] = index, ["condition"] = condition });
            }

            item["dependsOn"] = deps;
        }

        return item;
    }

    /// <summary>POST /api/works with an explicit assignments[] batch; the
    /// caller judges the status (201 vs the 422 problem body).</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostCreateAsync(
        HttpClient kernel, Guid projectId, params JsonNode[] assignments)
    {
        var body = new JsonObject
        {
            ["objective"] = "dag gate",
            ["projectId"] = projectId.ToString(),
            ["commandId"] = Guid.NewGuid().ToString("N"),
            ["assignments"] = new JsonArray(assignments.Select(a => a.DeepClone()).ToArray()),
        };
        using var response = await kernel.PostAsync("/api/works",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, await JsonWire.ReadJsonAsync(response.Content));
    }

    private async Task<Guid> CreateAsync(HttpClient kernel, Guid projectId, params JsonNode[] assignments)
    {
        var (status, body) = await PostCreateAsync(kernel, projectId, assignments);
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("workId").GetGuid();
    }

    /// <summary>Drain until the gated sibling's audit row shows at least
    /// <paramref name="minAttempts"/> requeued deps-unmet passes with the
    /// intent row visibly pending (a foreign dispatcher may lease it for a
    /// moment — the poll self-heals), then assert the gate's other truths: no
    /// candidates opened, nothing dead-lettered.</summary>
    private async Task<TestDbProbe.RoutingDecisionRow> AssertGatedAsync(
        KernelTestFactory factory, Guid workId, int index, int minAttempts)
    {
        var intent = IntentCommand(workId, index);
        var audit = await RoutingHarness.DrainUntilAsync(
            factory,
            async () =>
            {
                await fixture.Db.ExpireOutboxBackoff(workId);
                var row = await fixture.Db.RoutingDecision(intent);
                if (row is null || row.AttemptCount < minAttempts || row.Status != "pending" || row.Basis != "deps-unmet")
                {
                    return null;
                }

                return await fixture.Db.OutboxRowByCommand(intent, "pending") ? row : null;
            },
            $"gated routing intent {intent}");
        Assert.Equal("[]", audit.AllowedCandidates);
        Assert.Equal(0, await fixture.Db.DeadLetterCount(workId, "deps-unmet"));
        return audit;
    }

    /// <summary>The completed arm on the CE plane (fenced at the routed
    /// generation): walks the assignment to terminal and fires the wake law.</summary>
    private static async Task CompleteAsync(HttpClient kernel, Guid workId, Guid assignmentId)
    {
        var envelope = JsonNode.Parse(PostgresFixture.ReadFixture("valid/event.assignment.completed.json"))!.AsObject();
        envelope["id"] = "evt-" + Guid.NewGuid().ToString("N");
        envelope["subject"] = $"work/{workId}/assignment/{assignmentId:N}";
        var data = envelope["data"]!.AsObject();
        data["workId"] = workId.ToString("N");
        data["assignmentId"] = assignmentId.ToString("N");
        data["executionId"] = "exec-" + Guid.NewGuid().ToString("N");
        data["executionGeneration"] = 1;
        var accepted = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            envelope.ToJsonString(), HttpStatusCode.Accepted);
        Assert.Equal("accepted", accepted.GetProperty("status").GetString());
    }

    private static void AssertDagRejected(JsonElement problem, HttpStatusCode status, string detailNeedle)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("https://errors.openfacilitator.org/v1/dependency-violation", problem.GetProperty("type").GetString());
        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        var detail = problem.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrEmpty(detail));
        Assert.Contains(detailNeedle, detail);
    }

    /// <summary>The consumer-visible "nothing was created": the project the
    /// rejected batch named still has no Works.</summary>
    private static async Task AssertProjectEmptyAsync(HttpClient kernel, Guid projectId)
    {
        var detail = await KernelOps.GetJsonAsync(kernel, $"/api/admin/projects/{projectId}", HttpStatusCode.OK);
        Assert.Empty(detail.GetProperty("works").EnumerateArray());
    }
}
