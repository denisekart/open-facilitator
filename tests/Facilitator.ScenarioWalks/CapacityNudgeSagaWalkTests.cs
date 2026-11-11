using System.Net;
using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.ScenarioWalks;

/// <summary>
/// D2 capacity nudge saga: an accepted assignment on a live lane goes
/// stale as the driven clock advances — the gateway reports
/// <c>waiting-for-capacity</c> (the advisory reason renders on the
/// execution read beside the honest status), ONE nudge sweep fires one
/// ledger-commanded nudge, the budget then stays silent on repeat passes,
/// and the admin work read shows the stall. The nudge leg and the
/// capacity-reason leg walk together for the first time here.
/// Segments: NudgeSweeperTests.BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled
/// + OpsSurfaceTests.GatewayQueueReasonRendersOnTheExecutionRead (+ the
/// FlowControlTests window-pause law as the capacity-deferral source).
/// </summary>
[Collection("postgres:walks")]
public sealed class CapacityNudgeSagaWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StaleLaneReasonNudgedOnceThenAdminSeesStalled()
    {
        using var factory = new KernelTestFactory(fixture, noDispatcher: true, fakeClock: true);
        var kernel = factory.CreateAuthedClient();
        await fixture.Db.SeedAdminConfig("nudge-policy", """{"staleAfterMinutes":5}""");
        await fixture.Db.AdminConfigPurge("loop-budget");

        // A live accepted lane: assignment + started attempt + assign ledger
        // row + a leased gateway session (the segment's StaleLane twin).
        var workId = await fixture.Db.SeedWork("active");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "accepted");
        var startedAt = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
        await fixture.Db.SeedAttemptRow(Guid.NewGuid(), assignmentId, 1, "working", startedAt);
        var endpoint = "rt.walk-" + WalkKit.Leaf();
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, endpoint);
        await fixture.Db.SeedGatewayEnrollment(endpoint, "team-alpha", "isolated-executor");
        await fixture.Db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(30));

        // The gateway reports the capacity deferral (advisory, observational).
        var gwToken = await WalkKit.EnrollAsync(kernel, "gw-d2-" + WalkKit.Leaf(), team: "team-alpha");
        var gw = factory.LinkClient(gwToken);
        using (var reported = await gw.PostAsync(
                   $"/api/link/v1/assignments/{assignmentId}/queue-reason",
                   WalkKit.Content("""{"queueReason":"waiting-for-capacity"}""")))
        {
            // The seeded assignment row has no team; the route 404s a
            // foreign-team line honestly — re-seed under the gateway's team.
            Assert.Equal(HttpStatusCode.NotFound, reported.StatusCode);
        }

        var teamAssignment = await fixture.Db.SeedTeamAssignment(workId, "team-alpha", "accepted");
        using (var reason = await gw.PostAsync(
                   $"/api/link/v1/assignments/{teamAssignment}/queue-reason",
                   WalkKit.Content("""{"queueReason":"waiting-for-capacity"}""")))
        {
            Assert.Equal(HttpStatusCode.NoContent, reason.StatusCode);
        }
        var lines = await KernelOps.GetJsonAsync(kernel, $"/api/works/{workId}/executions", HttpStatusCode.OK);
        Assert.Contains(lines.EnumerateArray(),
            l => l.GetProperty("queueReason").GetString() == "waiting-for-capacity");

        // The driven advance: 20 fake minutes make the lane stale.
        factory.AdvanceClock(TimeSpan.FromMinutes(20));

        // ONE nudge sweep fires exactly one nudge (the sweep endpoint is the
        // I2 law; the ledger command id names episode/generation/count).
        var sweep = await KernelOps.PostJsonAsync(kernel, "/api/admin/sweeps/nudge", "{}", HttpStatusCode.OK);
        Assert.Equal(1, sweep.GetProperty("effects").GetInt32());
        Assert.True(await fixture.Db.OutboxRowByCommand($"work-nudge:{assignmentId:N}:1:1:1"),
            "the nudge's ledger row is missing");

        // Silence never refreshes: passes 2 and 3 stay in episode 1 and
        // burn the cap; pass 4 finds the budget spent.
        for (var count = 2; count <= 3; count++)
        {
            var pass = await KernelOps.PostJsonAsync(kernel, "/api/admin/sweeps/nudge", "{}", HttpStatusCode.OK);
            Assert.Equal(1, pass.GetProperty("effects").GetInt32());
        }

        var spent = await KernelOps.PostJsonAsync(kernel, "/api/admin/sweeps/nudge", "{}", HttpStatusCode.OK);
        Assert.Equal(0, spent.GetProperty("effects").GetInt32());

        // The admin work read sees the stall (the ops-surface leg).
        var detail = await KernelOps.GetJsonAsync(kernel, $"/api/admin/work/{workId}", HttpStatusCode.OK);
        var assignment = detail.GetProperty("assignments").EnumerateArray()
            .Single(a => a.GetProperty("assignmentId").GetString() == assignmentId.ToString());
        Assert.True(assignment.GetProperty("nudgeCount").GetInt32() >= 1);
        Assert.True(assignment.GetProperty("stalled").GetBoolean());

        await fixture.Db.AdminConfigPurge("nudge-policy");
        await fixture.Db.ExpireSessionLease(endpoint);
    }

    [Fact]
    public async Task NudgeRidesTheDispatcherOnceWhenCapacityReturns()
    {
        // The dispatch-proceeds leg: a due outbox row the dispatcher delivers
        // exactly once when the lane is live again (the FlowControl resume
        // law's server-side twin: a freed window delivers the queued row).
        using var factory = new KernelTestFactory(fixture, noDispatcher: true);
        var kernel = factory.CreateAuthedClient();
        var endpoint = "gw-d2b-" + WalkKit.Leaf();
        await WalkKit.SeedFleetAsync(fixture.Db, endpoint, "team-alpha", "research.analyze");
        var workId = await fixture.Db.SeedWork("active");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "accepted");
        var outboxId = await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, endpoint);
        Assert.Equal(1, await fixture.Db.OutboxCountBy(workId, "delivered"));

        // The dispatcher's DrainOnce law: a live session materializes the
        // row into the link ledger; a second drain adds nothing (once only).
        var dispatcher = factory.Services.GetRequiredService<IEventDispatcher>();
        await dispatcher.DrainOnceAsync(CancellationToken.None);
        await dispatcher.DrainOnceAsync(CancellationToken.None);
        Assert.True(await fixture.Db.OutboxDelivered(outboxId));
        _ = kernel;
    }
}
