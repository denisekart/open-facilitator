using System.Net;

namespace Facilitator.Tests.Interaction;

/// <summary>
/// The interactive leg on the CE + REST planes: <c>event.assignment.input.required.v1</c>
/// parks the running assignment (side-state edge), timelines the question
/// (<c>input.required</c>, correlation = requestId), and fans out in the frozen
/// order — (b) the team default endpoint, (c) a communication-capable session
/// picked through the decision seam (audited), (d) timeline-only + a progress
/// note; a generation-mismatched event fences 422, a requestId-redelivery is a
/// duplicate. The REST respond unparks to <c>running</c>, timelines
/// <c>input.answered</c> with a minted consumed <c>kernel:</c> receipt, and
/// routes the answer envelope to the assignment's bound endpoint; a second
/// answer is 409, a foreign or unknown requestId a 404, a superseded generation
/// a 409. Public surface first; DB assertions through the typed probe.
/// </summary>
[Collection("postgres:work")]
public sealed class InteractionLifecycleTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EventParksAssignmentTimelinesQuestionAndFansOutToDefaultEndpoint()
    {
        var endpoint = "gw-def-" + Guid.NewGuid().ToString("N")[..8];
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: new Dictionary<string, string?>
        {
            ["OFAC:InteractionDefaultEndpoint"] = endpoint,
        });
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "default-endpoint fan-out");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, "gw-bound-" + Guid.NewGuid().ToString("N")[..6]);
        var requestId = InteractionWire.NewRequestId();

        var parked = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1,
                "Include on-prem?", ["yes, include on-prem", "no, cloud only"]),
            HttpStatusCode.Accepted);
        Assert.Equal("parked", parked.GetProperty("status").GetString());
        Assert.Equal(requestId, parked.GetProperty("requestId").GetString());

        // Parked: the assignment row sits in the input_required side-state,
        // the question is on the timeline keyed by requestId.
        Assert.Equal("input_required", Assert.Single(await fixture.Db.AssignmentRowsForWork(workId)).Status);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "input.required"));
        var detail = Assert.Single(await fixture.Db.TimelineDetail(workId, "input.required"));
        Assert.Contains(requestId, detail, StringComparison.Ordinal);
        Assert.Contains("Include on-prem?", detail, StringComparison.Ordinal);

        // Fan-out (b): one communication.reply row addressed to the default
        // endpoint, question + numbered choices + the reply trailer.
        var fanout = "input-fanout:" + requestId;
        Assert.Equal([$"link.{endpoint}"], await fixture.Db.OutboxRecipientsByCommand(fanout));
        var envelope = await fixture.Db.OutboxEnvelopeByCommand(fanout);
        Assert.NotNull(envelope);
        Assert.Contains("command.communication.reply.v1", envelope, StringComparison.Ordinal);
        Assert.Contains("Include on-prem?", envelope, StringComparison.Ordinal);
        Assert.Contains("1) yes, include on-prem", envelope, StringComparison.Ordinal);
        Assert.Contains("2) no, cloud only", envelope, StringComparison.Ordinal);
        Assert.Contains($"Reply in this thread to answer (request {requestId})", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventWithoutAnyRouteFansOutTimelineOnlyWithProgressNote()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "timeline-only fan-out");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        var requestId = InteractionWire.NewRequestId();

        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Ship Tuesday?"),
            HttpStatusCode.Accepted);

        // No binding, no default endpoint, no communication-capable session:
        // arm (d) — a single work.progress note to the kernel log channel.
        var progress = "input-progress:" + requestId;
        Assert.Equal(["kernel.log"], await fixture.Db.OutboxRecipientsByCommand(progress));
        var envelope = await fixture.Db.OutboxEnvelopeByCommand(progress);
        Assert.NotNull(envelope);
        Assert.Contains("work.progress.v1", envelope, StringComparison.Ordinal);
        Assert.Contains("waiting for input: Ship Tuesday?", envelope, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId, "input.answered"));
    }

    [Fact]
    public async Task EventFansOutToCommunicationCapableSessionWithAudit()
    {
        var fleet = "gw-comm-" + Guid.NewGuid().ToString("N")[..8];
        await FleetHarness.SeedFleetAsync(fixture.Db, fleet, "team-alpha", "communication.reply.v1");
        try
        {
            using var factory = new KernelTestFactory(fixture, noDispatcher: true);
            var kernel = factory.CreateAuthedClient();
            var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "decision-seam fan-out");
            var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
            var requestId = InteractionWire.NewRequestId();

            await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
                InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Pick a region?"),
                HttpStatusCode.Accepted);

            // Arm (c): the live communication-capable session is the routed
            // recipient, and the pick is audited like the routing funnel.
            var fanout = "input-fanout:" + requestId;
            Assert.Equal([$"link.{fleet}"], await fixture.Db.OutboxRecipientsByCommand(fanout));
            var audit = await fixture.Db.RoutingDecision(fanout);
            Assert.NotNull(audit);
            Assert.Equal("routed", audit.Status);
            Assert.Equal(fleet, audit.ChosenEndpointId);
            Assert.Contains(fleet, audit.AllowedCandidates, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.Db.PurgeFleets(fleet);
        }
    }

    [Fact]
    public async Task EventFencedWhenGenerationMismatchLeavesNoTrace()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "event-side fence");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running", generation: 1);
        var requestId = InteractionWire.NewRequestId();

        using var response = await kernel.PostAsync("/api/ingress/events", InteractionWire.Content(
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 2, "Fenced?")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(response.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/stale-generation", problem.GetProperty("type").GetString());

        Assert.Equal("running", Assert.Single(await fixture.Db.AssignmentRowsForWork(workId)).Status);
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId, "input.required"));
    }

    [Fact]
    public async Task EventRedeliveryWithSameRequestIdIsDuplicate()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "requestId dedupe");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        var requestId = InteractionWire.NewRequestId();
        var envelope = InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Once only?");

        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events", envelope, HttpStatusCode.Accepted);
        var again = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Once only?"),
            HttpStatusCode.Accepted);

        Assert.Equal("duplicate", again.GetProperty("status").GetString());
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "input.required"));
        Assert.Equal(1, await fixture.Db.OutboxCountByCommand("input-progress:" + requestId));
    }

    [Fact]
    public async Task RestRespondUnparksMintsEvidenceAndRoutesEnvelopeToBoundEndpoint()
    {
        var bound = "gw-bound-" + Guid.NewGuid().ToString("N")[..8];
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "rest respond");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, bound);
        var requestId = InteractionWire.NewRequestId();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Ship it?"),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");

        var answered = await KernelOps.PostJsonAsync(alice,
            $"/api/works/{workId}/interactions/{requestId}/respond",
            """{"answer":"Yes, ship it."}""", HttpStatusCode.OK);
        Assert.Equal("answered", answered.GetProperty("status").GetString());
        var evidenceRef = answered.GetProperty("evidenceRef").GetString()!;
        Assert.StartsWith("kernel:", evidenceRef, StringComparison.Ordinal);
        // D5 receipt semantics: the row exists, already consumed.
        Assert.True(await fixture.Db.EvidenceConsumed(evidenceRef["kernel:".Length..]));

        // Unparked, timeline shows the answer with actor + excerpt, and the
        // answer envelope rides the assignment's bound endpoint.
        Assert.Equal("running", Assert.Single(await fixture.Db.AssignmentRowsForWork(workId)).Status);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "input.answered"));
        var detail = Assert.Single(await fixture.Db.TimelineDetail(workId, "input.answered"));
        Assert.Contains("Yes, ship it.", detail, StringComparison.Ordinal);
        Assert.Contains("oidc:", detail, StringComparison.Ordinal);

        var response = await fixture.Db.OutboxEnvelopeByCommand("input:" + requestId);
        Assert.NotNull(response);
        Assert.Contains("command.assignment.input.v1", response, StringComparison.Ordinal);
        Assert.Contains("Yes, ship it.", response, StringComparison.Ordinal);
        Assert.Equal([$"link.{bound}"], await fixture.Db.OutboxRecipientsByCommand("input:" + requestId));
    }

    [Fact]
    public async Task RestRespondTwiceIsConflict()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "double answer");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, "gw-x-" + Guid.NewGuid().ToString("N")[..6]);
        var requestId = InteractionWire.NewRequestId();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Answered once?"),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");
        await KernelOps.PostJsonAsync(alice, $"/api/works/{workId}/interactions/{requestId}/respond",
            """{"answer":"first"}""", HttpStatusCode.OK);

        using var replay = await alice.PostAsync($"/api/works/{workId}/interactions/{requestId}/respond",
            InteractionWire.Content("""{"answer":"second"}"""));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(replay.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/interaction-already-answered",
            problem.GetProperty("type").GetString());
        // The command-ledger dedupe id names the request.
        Assert.Contains("input:" + requestId, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestRespondUnknownRequestIdIs404AndForeignWorkIs404()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "404 law");
        var (foreignId, _) = await KernelOps.CreateWorkAsync(kernel, "other work");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        var requestId = InteractionWire.NewRequestId();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Scoped?"),
            HttpStatusCode.Accepted);
        var alice = await CreateUserClientAsync(factory, "alice");

        // Unknown requestId under the right work: 404.
        using var unknown = await alice.PostAsync($"/api/works/{workId}/interactions/inp-nobody/respond",
            InteractionWire.Content("""{"answer":"x"}"""));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // The pending request under ANOTHER work: identical 404 (no leak).
        using var foreign = await alice.PostAsync($"/api/works/{foreignId}/interactions/{requestId}/respond",
            InteractionWire.Content("""{"answer":"x"}"""));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(foreign.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/interaction-not-found",
            problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RestRespondFenceMismatchIs409()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "respond-side fence");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, "gw-f-" + Guid.NewGuid().ToString("N")[..6]);
        var requestId = InteractionWire.NewRequestId();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Stale answer?"),
            HttpStatusCode.Accepted);

        // A redispatch between question and answer supersedes the generation.
        await fixture.Db.BumpAssignmentGeneration(assignmentId);
        var alice = await CreateUserClientAsync(factory, "alice");
        using var conflict = await alice.PostAsync($"/api/works/{workId}/interactions/{requestId}/respond",
            InteractionWire.Content("""{"answer":"late"}"""));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await JsonWire.ReadJsonAsync(conflict.Content);
        Assert.Equal("https://errors.openfacilitator.org/v1/interaction-fence-mismatch",
            problem.GetProperty("type").GetString());
        Assert.Equal(0, await fixture.Db.TimelineEventCount(workId, "input.answered"));
    }

    [Fact]
    public async Task CommandOnCePlaneAnswersPendingRequest()
    {
        var bound = "gw-ce-" + Guid.NewGuid().ToString("N")[..8];
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "wire respond");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, bound);
        var requestId = InteractionWire.NewRequestId();
        await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Answered over the wire?"),
            HttpStatusCode.Accepted);

        var answered = await KernelOps.PostJsonAsync(kernel, "/api/ingress/events",
            InteractionWire.InputCommandEnvelope(requestId, "wire answer",
                "cmd-" + Guid.NewGuid().ToString("N"), "provider:mm:dlv-" + Guid.NewGuid().ToString("N")[..6]),
            HttpStatusCode.OK);
        Assert.Equal("answered", answered.GetProperty("status").GetString());

        Assert.Equal("running", Assert.Single(await fixture.Db.AssignmentRowsForWork(workId)).Status);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "input.answered"));
        var response = await fixture.Db.OutboxEnvelopeByCommand("input:" + requestId);
        Assert.NotNull(response);
        Assert.Contains("wire answer", response, StringComparison.Ordinal);
        Assert.Equal([$"link.{bound}"], await fixture.Db.OutboxRecipientsByCommand("input:" + requestId));
    }

    private static async Task<HttpClient> CreateUserClientAsync(KernelTestFactory factory, string user)
    {
        var client = factory.CreateClient();
        var token = await KeycloakFixture.TokenForUserAsync(user, user);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
