using System.Net;
using System.Text.Json;

using Facilitator.Kernel.Work;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Work;

/// <summary>
/// The §5.3 bounded-nudge law on a fake clock: no policy row means no
/// nudging; a stale + online lane earns exactly one command.work.nudge.v1 per
/// pass until the per-episode cap of three spends the budget; fresh activity
/// (an acked delivery) keeps the lane alive; an endpoint without a live
/// session lease is skipped; observed progress opens the NEXT episode and
/// clears "stalled" on the admin surface; an accepted assignment whose
/// execution never started is not a stall. Passes are driven explicitly via
/// <see cref="NudgeSweeper.SweepOnceAsync"/>; the factory parks the hosted
/// loop at one hour so no test races it, and the advance is sized so the
/// sweep's fake now sits past every short fleet lease the shared container
/// accrues — foreign lanes are offline, so only these lanes can be nudged.
/// </summary>
[Collection("postgres:work")]
public sealed class NudgeSweeperTests(PostgresFixture fixture)
{
    private const string Policy = "nudge-policy";

    private const string PolicyPayload = """{"staleAfterMinutes":5}""";

    private static readonly IReadOnlyDictionary<string, string?> ParkedLoop =
        new Dictionary<string, string?> { ["Nudge:SweepIntervalSeconds"] = "3600" };

    [Fact]
    public async Task NoPolicyRow_SilentEvenOnStaleOnlineLane()
    {
        using var factory = await Host();
        var lane = await StaleLaneAsync(online: true);
        try
        {
            factory.Clock!.Advance(TimeSpan.FromMinutes(20));

            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Equal(1, await fixture.Db.OutboxCountBy(lane.WorkId)); // only the seeded assign
            Assert.Empty(await fixture.Db.NudgeRowsForAssignment(lane.AssignmentId));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(lane.Endpoint);
        }
    }

    [Fact]
    public async Task StaleOnlineLane_EmitsOneValidNudgeWithLedgerRow()
    {
        using var factory = await Host();
        await fixture.Db.SeedAdminConfig(Policy, PolicyPayload);
        var lane = await StaleLaneAsync(online: true);
        try
        {
            factory.Clock!.Advance(TimeSpan.FromMinutes(20));

            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));

            var commandId = $"work-nudge:{lane.AssignmentId:N}:1:1:1";
            var envelope = await fixture.Db.OutboxEnvelopeByCommand(commandId);
            Assert.NotNull(envelope);
            KernelOps.AssertEnvelopesValid([envelope]);
            var data = JsonDocument.Parse(envelope).RootElement.GetProperty("data");
            Assert.Equal("1", data.GetProperty("stallEpisode").GetString());
            Assert.Equal(1, data.GetProperty("executionGeneration").GetInt32());
            Assert.Equal(lane.AssignmentId.ToString("N"), data.GetProperty("assignmentId").GetString());

            // The ownership law: every outbox row on this work, the nudge
            // included, rides the owning endpoint's link channel.
            Assert.Equal([$"link.{lane.Endpoint}"], await fixture.Db.OutboxRecipients(lane.WorkId));
            Assert.Equal(2, await fixture.Db.OutboxCountBy(lane.WorkId)); // assign + nudge
            Assert.Equal([(1, 1, 1)], await fixture.Db.NudgeRowsForAssignment(lane.AssignmentId));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(lane.Endpoint);
        }
    }

    [Fact]
    public async Task BudgetSpentAtCap_ThenSilent_AndAdminSeesStalled()
    {
        using var factory = await Host();
        using var kernel = factory.CreateAuthedClient();
        await fixture.Db.SeedAdminConfig(Policy, PolicyPayload);
        var lane = await StaleLaneAsync(online: true);
        try
        {
            factory.Clock!.Advance(TimeSpan.FromMinutes(20));

            // One episode: silence never refreshes, so passes 1..3 stay in
            // episode 1 and burn the cap; pass 4 finds the budget spent.
            for (var count = 1; count <= 3; count++)
            {
                Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
                Assert.True(await fixture.Db.OutboxRowByCommand($"work-nudge:{lane.AssignmentId:N}:1:1:{count}"),
                    $"nudge {count} missing from the outbox ledger");
            }

            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Equal([$"link.{lane.Endpoint}"], await fixture.Db.OutboxRecipients(lane.WorkId));
            Assert.Equal(4, await fixture.Db.OutboxCountBy(lane.WorkId)); // assign + 3 nudges
            Assert.Equal([(1, 3, 1)], await fixture.Db.NudgeRowsForAssignment(lane.AssignmentId));

            var detail = await KernelOps.GetJsonAsync(kernel, $"/api/admin/work/{lane.WorkId}", HttpStatusCode.OK);
            var assignment = detail.GetProperty("assignments").EnumerateArray()
                .Single(a => a.GetProperty("assignmentId").GetString() == lane.AssignmentId.ToString());
            Assert.Equal(3, assignment.GetProperty("nudgeCount").GetInt32());
            Assert.True(assignment.GetProperty("stalled").GetBoolean());
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(lane.Endpoint);
        }
    }

    [Fact]
    public async Task PeerHoldsSweepLock_PassSkipsSilently()
    {
        // N-replica law: two replicas sweep the same fleet; the advisory lock
        // makes a concurrent pass a silent skip, not a race or an error.
        using var factory = await Host();
        await fixture.Db.SeedAdminConfig(Policy, PolicyPayload);
        var lane = await StaleLaneAsync(online: true);
        try
        {
            factory.Clock!.Advance(TimeSpan.FromMinutes(20));
            await using var peer = await fixture.Db.HoldAdvisoryLockAsync(NudgeSweeper.SweepLockKey);
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Equal(1, await fixture.Db.OutboxCountBy(lane.WorkId)); // only the seeded assign
            Assert.Empty(await fixture.Db.NudgeRowsForAssignment(lane.AssignmentId));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(lane.Endpoint);
        }
    }

    [Fact]
    public async Task AckedDeliveryAfterCutoff_KeepsLaneAlive()
    {
        using var factory = await Host();
        await fixture.Db.SeedAdminConfig(Policy, PolicyPayload);
        var lane = await StaleLaneAsync(online: true);
        factory.Clock!.Advance(TimeSpan.FromMinutes(20));
        try
        {
            // The gateway's session acks the assign at the sweep's fake now:
            // an acked delivery is work activity past the cutoff, so the
            // lane that would otherwise be stale stays alive.
            await fixture.Db.SeedAckedDelivery(
                lane.AssignOutboxId, lane.Endpoint, factory.Clock.GetUtcNow().UtcDateTime);

            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Empty(await fixture.Db.NudgeRowsForAssignment(lane.AssignmentId));
            Assert.Equal(1, await fixture.Db.OutboxCountBy(lane.WorkId));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(lane.Endpoint);
        }
    }

    [Fact]
    public async Task OfflineOwner_SkippedWithoutBurningBudget()
    {
        using var factory = await Host();
        await fixture.Db.SeedAdminConfig(Policy, PolicyPayload);
        // Two offline shapes: no session row at all, and a lease that lapsed
        // before the sweep (seeded relative to real now, which the 20-minute
        // advance has already passed).
        var noSession = await StaleLaneAsync(online: false);
        var lapsed = await StaleLaneAsync(online: false);
        await fixture.Db.SeedLinkSession(lapsed.Endpoint, 1, DateTime.UtcNow.AddMinutes(-1));
        try
        {
            factory.Clock!.Advance(TimeSpan.FromMinutes(20));

            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Empty(await fixture.Db.NudgeRowsForAssignment(noSession.AssignmentId));
            Assert.Empty(await fixture.Db.NudgeRowsForAssignment(lapsed.AssignmentId));
            Assert.Equal(1, await fixture.Db.OutboxCountBy(noSession.WorkId));
            Assert.Equal(1, await fixture.Db.OutboxCountBy(lapsed.WorkId));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(noSession.Endpoint);
            await fixture.Db.ExpireSessionLease(lapsed.Endpoint);
        }
    }

    [Fact]
    public async Task ObservedProgress_OpensNextEpisode_AndClearsStalledView()
    {
        using var factory = await Host();
        using var kernel = factory.CreateAuthedClient();
        await fixture.Db.SeedAdminConfig(Policy, PolicyPayload);
        var lane = await StaleLaneAsync(online: true);
        try
        {
            factory.Clock!.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.True(await fixture.Db.OutboxRowByCommand($"work-nudge:{lane.AssignmentId:N}:1:1:1"));

            // Observed progress refreshes activity; once the lane is stale
            // against the window again, the next nudge belongs to a FRESH
            // episode with its own budget, and the admin view stops calling
            // the lane stalled (latest-episode doctrine).
            await fixture.Db.SeedTimelineRow(lane.WorkId, "command.accepted");
            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));

            var commandId = $"work-nudge:{lane.AssignmentId:N}:1:2:1";
            Assert.True(await fixture.Db.OutboxRowByCommand(commandId), "episode-2 nudge missing");
            var envelope = await fixture.Db.OutboxEnvelopeByCommand(commandId);
            Assert.NotNull(envelope);
            KernelOps.AssertEnvelopesValid([envelope]);
            Assert.Equal("2", JsonDocument.Parse(envelope).RootElement.GetProperty("data")
                .GetProperty("stallEpisode").GetString());
            Assert.Equal([(1, 1, 1), (2, 1, 1)], await fixture.Db.NudgeRowsForAssignment(lane.AssignmentId));

            var detail = await KernelOps.GetJsonAsync(kernel, $"/api/admin/work/{lane.WorkId}", HttpStatusCode.OK);
            var assignment = detail.GetProperty("assignments").EnumerateArray()
                .Single(a => a.GetProperty("assignmentId").GetString() == lane.AssignmentId.ToString());
            Assert.Equal(1, assignment.GetProperty("nudgeCount").GetInt32());
            Assert.False(assignment.GetProperty("stalled").GetBoolean());
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(lane.Endpoint);
        }
    }

    [Fact]
    public async Task NeverStartedAssignment_IsNotAStall()
    {
        using var factory = await Host();
        await fixture.Db.SeedAdminConfig(Policy, PolicyPayload);
        // Accepted but no execution_attempts row: routed-pre-receipt.
        var workId = await fixture.Db.SeedWork("active");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "accepted");
        var endpoint = "rt.nudge-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, endpoint);
        await fixture.Db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(30));
        try
        {
            factory.Clock!.Advance(TimeSpan.FromMinutes(20));

            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Empty(await fixture.Db.NudgeRowsForAssignment(assignmentId));
            Assert.Equal(1, await fixture.Db.OutboxCountBy(workId));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(endpoint);
        }
    }

    private async Task<KernelTestFactory> Host()
    {
        var factory = new KernelTestFactory(fixture, noDispatcher: true, settings: ParkedLoop, fakeClock: true);
        // The kernel entrypoint migrates the schema before it serves, so one
        // health roundtrip proves the tables exist before any probe seeding.
        using var warmup = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await warmup.GetAsync("/health")).StatusCode);
        return factory;
    }

    private static NudgeSweeper Sweeper(KernelTestFactory factory) =>
        factory.Services.GetRequiredService<NudgeSweeper>();

    private sealed record Lane(Guid WorkId, Guid AssignmentId, Guid AssignOutboxId, string Endpoint);

    /// <summary>A live accepted assignment whose execution started NOW (real
    /// time = the fake clock's zero point), owned by a link endpoint with a
    /// live lease; the caller advances the fake clock past the 5-minute
    /// policy window to make the lane stale.</summary>
    private async Task<Lane> StaleLaneAsync(bool online)
    {
        var workId = await fixture.Db.SeedWork("active");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "accepted");
        // Microsecond-aligned: Postgres stores timestamptz at microsecond
        // precision, and the sweeper's episode law compares the stored
        // progress stamp against the re-read activity with exact equality -
        // an unaligned seed would round and fracture the episode.
        var startedAt = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
        await fixture.Db.SeedAttemptRow(Guid.NewGuid(), assignmentId, 1, "working", startedAt);
        var endpoint = "rt.nudge-" + Guid.NewGuid().ToString("N")[..8];
        var assignOutboxId = await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, endpoint);
        if (online)
        {
            // Lease must outlive the caller's 20-minute advance or the lane
            // silently becomes offline at sweep time.
            await fixture.Db.SeedLinkSession(endpoint, 1, DateTime.UtcNow.AddMinutes(30));
        }

        return new Lane(workId, assignmentId, assignOutboxId, endpoint);
    }
}
