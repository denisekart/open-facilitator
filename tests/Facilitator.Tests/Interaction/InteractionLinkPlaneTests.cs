using System.Net;

namespace Facilitator.Tests.Interaction;

/// <summary>
/// The interactive leg on the link plane: an enrolled endpoint posts
/// <c>event.assignment.input.required.v1</c> (the Gateway relaying its harness)
/// and answers later with <c>command.assignment.input.v1</c> carrying a
/// <c>provider:</c> actorEvidenceRef (a chat reply mapped adapter-side). The
/// gate admits both (event class + registered command, non-authoritative
/// evidence is workload-only-allowed); the arms share the REST respond law —
/// park + fan-out, then unpark + <c>input.answered</c> + the answer envelope to
/// the assignment's bound endpoint; a replayed requestId answers 409.
/// </summary>
[Collection("postgres:work")]
public sealed class InteractionLinkPlaneTests(PostgresFixture fixture)
{
    [Fact]
    public async Task LinkPlaneEventParksAndCommandRespondUnparks()
    {
        var endpoint = "gw-int-" + Guid.NewGuid().ToString("N")[..8];
        var bound = "gw-run-" + Guid.NewGuid().ToString("N")[..8];
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));
        var (workId, _) = await KernelOps.CreateWorkAsync(kernel, "link-plane interaction");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running");
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, bound);
        var requestId = InteractionWire.NewRequestId();

        var parked = await link.PostAsync("/api/link/v1/events",
            InteractionWire.Content(InteractionWire.InputRequiredEnvelope(workId, assignmentId, requestId, 1, "Link question?")));
        Assert.Equal(HttpStatusCode.Accepted, parked.StatusCode);
        Assert.Equal("input_required", Assert.Single(await fixture.Db.AssignmentRowsForWork(workId)).Status);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "input.required"));
        // No binding/default endpoint/comm-capable session: arm (d) progress note.
        Assert.Equal(["kernel.log"], await fixture.Db.OutboxRecipientsByCommand("input-progress:" + requestId));

        // The chat reply, mapped adapter-side into the answer command.
        var answered = await link.PostAsync("/api/link/v1/events",
            InteractionWire.Content(InteractionWire.InputCommandEnvelope(requestId, "link answer",
                "cmd-" + Guid.NewGuid().ToString("N"), "provider:mm:dlv-" + Guid.NewGuid().ToString("N")[..6])));
        Assert.Equal(HttpStatusCode.Accepted, answered.StatusCode);

        Assert.Equal("running", Assert.Single(await fixture.Db.AssignmentRowsForWork(workId)).Status);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "input.answered"));
        var response = await fixture.Db.OutboxEnvelopeByCommand("input:" + requestId);
        Assert.NotNull(response);
        Assert.Contains("link answer", response, StringComparison.Ordinal);
        Assert.Equal([$"link.{bound}"], await fixture.Db.OutboxRecipientsByCommand("input:" + requestId));

        // A second answer for the settled request: 409 (link-plane echo of
        // the REST conflict, envelope-settled with the refused status).
        var replay = await link.PostAsync("/api/link/v1/events",
            InteractionWire.Content(InteractionWire.InputCommandEnvelope(requestId, "again",
                "cmd-" + Guid.NewGuid().ToString("N"), "provider:mm:dlv-" + Guid.NewGuid().ToString("N")[..6])));
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
    }

    [Fact]
    public async Task LinkPlaneRespondForUnknownRequestIs404()
    {
        var endpoint = "gw-int-" + Guid.NewGuid().ToString("N")[..8];
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var link = factory.CreateLinkClient(await LinkTestHarness.EnrollAsync(kernel, endpoint));

        var missing = await link.PostAsync("/api/link/v1/events",
            InteractionWire.Content(InteractionWire.InputCommandEnvelope("inp-nobody", "x",
                "cmd-" + Guid.NewGuid().ToString("N"), "provider:mm:dlv-" + Guid.NewGuid().ToString("N")[..6])));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
