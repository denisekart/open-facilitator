using System.Net;
using System.Text.Json;

using Facilitator.Kernel.Provisioning;
using Facilitator.Kernel.Work;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Work;

/// <summary>
/// The session-loss law on a fake clock: an open working attempt whose
/// owning endpoint holds no live link lease is marked on the first offline
/// sweep and failed by a kernel-authored, schema-valid
/// <c>event.assignment.failed.v1</c> (<c>reason=session-lease-expired</c>,
/// <c>retryable=true</c>) on the second — the attempt lands <c>failed</c>, the
/// assignment <c>failed</c>, the inbox row settles <c>processed</c> exactly
/// once. A live lease never fails; a reconnect inside the hysteresis window
/// clears the mark and restarts the episode; a re-dispatched straggler
/// attempt at an old generation is out of scope; without the
/// <c>workload-scale</c> policy row (manual mode) the sweeper is silent.
/// Passes are driven explicitly via <see cref="SessionLossSweeper.SweepOnceAsync"/>;
/// the factory parks the hosted loop at one hour (the GC precedent) and no
/// test advances the fake clock past it. Lanes are terminalized at test end
/// so a leaked open attempt never joins a later test's candidate set.
/// </summary>
[Collection("postgres:work")]
public sealed class SessionLossTests(PostgresFixture fixture)
{
    private const string PolicyResource = ProvisioningProcessor.PolicyResource;

    private const string PolicyPayload = """{"minWorkers":0,"maxWorkers":2,"idleShutdownMinutes":15}""";

    [Fact]
    public async Task DeadOwner_FailsAttemptOnceWithValidRetryableEvent()
    {
        using var factory = await HostAsync();
        await fixture.Db.SeedAdminConfig(PolicyResource, PolicyPayload);
        var lane = await DeadLaneAsync();
        var sweeper = Sweeper(factory);
        try
        {
            // First offline pass: suspect only — no event, nothing settled.
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            Assert.Null(await fixture.Db.InboxStateError(EventId(lane.ExecutionId)));

            // Second consecutive offline pass: exactly one failed event, on the
            // wire law, retryable, and the ledger walks to terminal.
            Assert.Equal(1, await sweeper.SweepOnceAsync(default));
            var settled = await fixture.Db.InboxStateError(EventId(lane.ExecutionId));
            Assert.NotNull(settled);
            Assert.Equal("processed", settled.Value.State);
            Assert.Null(settled.Value.Error);
            Assert.Equal("failed", await fixture.Db.AttemptState(lane.ExecutionId));
            Assert.Contains(("failed", "seed.cap", 1), await fixture.Db.AssignmentRowsForWork(lane.WorkId));

            var payload = await fixture.Db.InboxPayload(EventId(lane.ExecutionId));
            Assert.NotNull(payload);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            Assert.Equal("urn:facilitator:kernel", root.GetProperty("source").GetString());
            Assert.Equal(SessionLossSweeper.FailedType, root.GetProperty("type").GetString());
            var data = root.GetProperty("data");
            Assert.Equal(lane.WorkId.ToString(), data.GetProperty("workId").GetString());
            Assert.Equal(lane.AssignmentId.ToString("N"), data.GetProperty("assignmentId").GetString());
            Assert.Equal(lane.ExecutionId.ToString("N"), data.GetProperty("executionId").GetString());
            Assert.Equal(1, data.GetProperty("executionGeneration").GetInt32());
            Assert.Equal(SessionLossSweeper.Reason, data.GetProperty("reason").GetString());
            Assert.True(data.GetProperty("retryable").GetBoolean());
        }
        finally
        {
            await CleanLaneAsync(lane);
        }
    }

    [Fact]
    public async Task ReplayAfterFire_NeverPostsTwice()
    {
        using var factory = await HostAsync();
        await fixture.Db.SeedAdminConfig(PolicyResource, PolicyPayload);
        var lane = await DeadLaneAsync();
        var sweeper = Sweeper(factory);
        try
        {
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            Assert.Equal(1, await sweeper.SweepOnceAsync(default));
            // The attempt is terminal now, so the lane left the candidate set;
            // a third pass finds nothing and the inbox holds the single row.
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            var settled = await fixture.Db.InboxStateError(EventId(lane.ExecutionId));
            Assert.NotNull(settled);
            Assert.Equal("processed", settled.Value.State);
        }
        finally
        {
            await CleanLaneAsync(lane);
        }
    }

    [Fact]
    public async Task LiveLease_SkippedEvenAcrossManyPasses()
    {
        using var factory = await HostAsync();
        await fixture.Db.SeedAdminConfig(PolicyResource, PolicyPayload);
        var lane = await DeadLaneAsync(online: true);
        var sweeper = Sweeper(factory);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            }

            Assert.Null(await fixture.Db.InboxStateError(EventId(lane.ExecutionId)));
            Assert.Equal("working", await fixture.Db.AttemptState(lane.ExecutionId));
        }
        finally
        {
            await CleanLaneAsync(lane);
        }
    }

    [Fact]
    public async Task ReconnectInsideHysteresis_ClearsMark_ThenFreshEpisodeFires()
    {
        using var factory = await HostAsync();
        await fixture.Db.SeedAdminConfig(PolicyResource, PolicyPayload);
        var lane = await DeadLaneAsync(online: true);
        var sweeper = Sweeper(factory);
        try
        {
            // Offline pass 1 marks, but the owner re-leases before pass 2:
            // the blip clears the mark and burns nothing.
            factory.Clock!.Advance(TimeSpan.FromMinutes(11)); // seeded 10-minute lease expired
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            await fixture.Db.RefreshLinkLease(lane.Endpoint, 2, factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(10));
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));

            // Lease expires for good: two MORE offline passes (a fresh
            // episode) are required before the law fires.
            factory.Clock.Advance(TimeSpan.FromMinutes(11));
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            Assert.Equal(1, await sweeper.SweepOnceAsync(default));
            Assert.Equal("failed", await fixture.Db.AttemptState(lane.ExecutionId));
        }
        finally
        {
            await CleanLaneAsync(lane);
        }
    }

    [Fact]
    public async Task StragglerAtOldGeneration_OutOfScope()
    {
        using var factory = await HostAsync();
        await fixture.Db.SeedAdminConfig(PolicyResource, PolicyPayload);
        var lane = await DeadLaneAsync();
        // A re-dispatch moved the line: the assignment now runs at generation
        // 2, so the generation-1 leftover is history, not an orphan.
        await fixture.Db.BumpAssignmentGeneration(lane.AssignmentId);
        var sweeper = Sweeper(factory);
        try
        {
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            Assert.Null(await fixture.Db.InboxStateError(EventId(lane.ExecutionId)));
            Assert.Equal("working", await fixture.Db.AttemptState(lane.ExecutionId));
        }
        finally
        {
            await CleanLaneAsync(lane);
        }
    }

    [Fact]
    public async Task ManualMode_NoPolicyRow_Silent()
    {
        using var factory = await HostAsync();
        // The shared container may carry the policy row from other tests in
        // the collection: delete it so manual mode is the actual state.
        await fixture.Db.DeleteAdminConfig(PolicyResource);
        var lane = await DeadLaneAsync();
        var sweeper = Sweeper(factory);
        try
        {
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            Assert.Equal(0, await sweeper.SweepOnceAsync(default));
            Assert.Null(await fixture.Db.InboxStateError(EventId(lane.ExecutionId)));
            Assert.Equal("working", await fixture.Db.AttemptState(lane.ExecutionId));
        }
        finally
        {
            await CleanLaneAsync(lane);
        }
    }

    private static string EventId(Guid executionId) => $"session-loss:{executionId:N}";

    private async Task<KernelTestFactory> HostAsync()
    {
        var factory = new KernelTestFactory(fixture, noDispatcher: true, fakeClock: true);
        // The kernel entrypoint migrates the schema before it serves, so one
        // health roundtrip proves the tables exist before any probe seeding.
        using var warmup = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await warmup.GetAsync("/health")).StatusCode);
        return factory;
    }

    private static SessionLossSweeper Sweeper(KernelTestFactory factory) =>
        factory.Services.GetRequiredService<SessionLossSweeper>();

    private sealed record DeadLane(Guid WorkId, Guid AssignmentId, Guid ExecutionId, string Endpoint);

    /// <summary>A running assignment with an open working attempt at
    /// generation 1, owned by a link endpoint: offline (no session row at
    /// all) by default, or holding a 10-minute lease when
    /// <paramref name="online"/> (the caller advances the fake clock past it
    /// to go offline).</summary>
    private async Task<DeadLane> DeadLaneAsync(bool online = false)
    {
        var workId = await fixture.Db.SeedWork("active");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "running", capabilityId: "seed.cap");
        var executionId = Guid.NewGuid();
        await fixture.Db.SeedAttemptRow(executionId, assignmentId, 1, "working", DateTime.UtcNow);
        var endpoint = "rt.sessionloss-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, endpoint);
        if (online)
        {
            await fixture.Db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(10));
        }

        return new DeadLane(workId, assignmentId, executionId, endpoint);
    }

    /// <summary>Terminalize a lane's attempt and drop its session so a
    /// leftover row is inert for every later test's sweeper pass.</summary>
    private async Task CleanLaneAsync(DeadLane lane)
    {
        await fixture.Db.EndAttempt(lane.ExecutionId);
        await fixture.Db.DeleteLinkSession(lane.Endpoint);
    }
}
