using System.Net;
namespace Facilitator.ScenarioWalks;

/// <summary>
/// N12 no-double-ask across a restart: a pending ask exists before the
/// process dies; the factory is disposed and a new one boots on the same
/// database (the restart-surface segment). After the restart the approval
/// sweep runs once — the ask nudges EXACTLY once across the boundary (the
/// nudge stamp lives on the row, so the second process cannot re-ask
/// loud), and an approval settled before the restart stays settled.
/// Segments: RestartSurvivalTests.RestartAfterAcceptedEvents_NoLossNoDuplicate
/// + ConsentAuthorityTests.NudgeBeforeExpiryWritesTimelineAndStampsOnce.
/// </summary>
[Collection("postgres:walks")]
public sealed class RestartNoDoubleAskWalkTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PendingAskReAsksOnceAcrossRestart_SettledStaysSettled()
    {
        Guid pendingId;
        Guid settledId;
        Guid workId;

        // Before the restart: one pending ask, one settled approval.
        using (var factoryA = new KernelTestFactory(fixture, noDispatcher: true))
        {
            var kernel = factoryA.CreateAuthedClient();
            await fixture.Db.SeedAdminConfig("consent-policy", """{"nudgeBeforeMinutes":10,"expiresAfterMinutes":60}""");
            (workId, _) = await KernelOps.CreateWorkAsync(kernel, "restart ask");
            pendingId = Guid.NewGuid();
            settledId = Guid.NewGuid();
            var link = factoryA.LinkClient(await WalkKit.EnrollAsync(kernel, "gw-n12-" + WalkKit.Leaf()));
            await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
                WalkKit.RequiredEnvelope(pendingId, workId, ["user:alice"], TimeSpan.FromMinutes(5)),
                HttpStatusCode.Accepted);
            await KernelOps.PostJsonAsync(link, "/api/link/v1/events",
                WalkKit.RequiredEnvelope(settledId, workId, ["user:alice"], TimeSpan.FromMinutes(5)),
                HttpStatusCode.Accepted);
            var aliceA = await WalkKit.UserClientAsync(factoryA, "alice");
            await KernelOps.PostJsonAsync(aliceA, $"/api/works/{workId}/approvals/{settledId}/respond",
                """{"decision":"approved"}""", HttpStatusCode.OK);
        }

        // The restart: a fresh factory on the same fixture serves the probe.
        using var factoryB = new KernelTestFactory(fixture, noDispatcher: true);
        var afterRestart = factoryB.CreateAuthedClient();

        // The settled question stays settled: the ledger state survived.
        var settled = await fixture.Db.Approval(settledId);
        Assert.NotNull(settled);
        Assert.Equal("approved", settled.State);

        // One nudge sweep after the restart asks exactly once; a second
        // sweep adds nothing — no double-ask across the process boundary.
        await KernelOps.PostJsonAsync(afterRestart, "/api/admin/sweeps/approvals", "{}", HttpStatusCode.OK);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.nudged"));
        Assert.True(await fixture.Db.ApprovalNudged(pendingId));
        await KernelOps.PostJsonAsync(afterRestart, "/api/admin/sweeps/approvals", "{}", HttpStatusCode.OK);
        Assert.Equal(1, await fixture.Db.TimelineEventCount(workId, "approval.nudged"));

        // A settled approval cannot be re-answered after the restart either.
        var alice = await WalkKit.UserClientAsync(factoryB, "alice");
        using var reDecide = await alice.PostAsync($"/api/works/{workId}/approvals/{settledId}/respond",
            WalkKit.Content("""{"decision":"denied"}"""));
        Assert.Equal(HttpStatusCode.Conflict, reDecide.StatusCode);
        Assert.Equal("https://errors.openfacilitator.org/v1/approval-already-consumed",
            (await reDecide.Content.ReadJsonAsync()).GetProperty("type").GetString());
    }
}
