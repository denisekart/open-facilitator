using System.Net;
using System.Text.Json;

using Facilitator.Kernel.Provisioning;

using Microsoft.Extensions.DependencyInjection;

namespace Facilitator.Tests.Provisioning;

/// <summary>
/// The provisioning scale law on a fake clock: no policy row means the sweep is
/// exactly nothing; a pending no-capacity decision earns ONE deterministic
/// start op + one valid envelope to the launcher channel, and a replayed
/// sweep is a no-op end to end; a nudge-capped offline assignment escalates
/// to a stalled-scaleup start; an empty LauncherEndpoint records the op but
/// emits nothing; a boot-time min shortfall starts once; an idle live
/// workload stops at exactly the policy window and the delivered stop
/// mirrors to stopped; the maxWorkers cap blocks the third start; a
/// delivered start against a live session mirrors to running; and a
/// delivered start whose fleet never comes live demotes to failed after
/// two sweeps, re-arming the min floor for a next-episode retry. Passes are
/// driven explicitly via <see cref="ProvisioningProcessor.SweepOnceAsync"/>;
/// the factory parks the hosted loop at one hour, and every test uses a
/// per-test random workload id so foreign fleet rows can never join.
/// </summary>
[Collection("postgres:work")]
public sealed class ProvisioningLawTests(PostgresFixture fixture)
{
    private const string Policy = "workload-scale";

    private static string Workload() => "rt.prov-" + Guid.NewGuid().ToString("N")[..8];

    private static string Team() => "rt-team-" + Guid.NewGuid().ToString("N")[..8];

    private static string Launcher() => "rt.launcher-" + Guid.NewGuid().ToString("N")[..8];

    private static Dictionary<string, string?> Settings(string workload, string team, string launcher)
    {
        var s = new Dictionary<string, string?>
        {
            ["Provision:SweepIntervalSeconds"] = "3600",
            ["Provision:Workloads:0:WorkloadId"] = workload,
            ["Provision:Workloads:0:Team"] = team,
            ["Provision:Workloads:0:Image"] = "registry.local/worker:1",
            ["Provision:Workloads:0:Entrypoint:0"] = "worker",
            ["Provision:Workloads:0:Network"] = "ofac-net",
            ["Provision:Workloads:0:Env:RUST_LOG"] = "info",
            ["Provision:Workloads:0:Volumes:0:Source"] = "/srv/cache",
            ["Provision:Workloads:0:Volumes:0:Target"] = "/cache",
            ["Provision:Workloads:0:Volumes:0:ReadOnly"] = "true",
            ["Provision:Workloads:0:Labels:ofac.custom"] = "x",
            ["Provision:Workloads:0:StopGraceSeconds"] = "30",
        };
        if (launcher.Length > 0)
        {
            s["Provision:LauncherEndpoint"] = launcher;
        }

        return s;
    }

    [Fact]
    public async Task NoPolicyRow_SweepIsExactlyNothing()
    {
        var (workload, team, launcher) = (Workload(), Team(), Launcher());
        using var factory = await Host(workload, team, launcher);
        var workId = await fixture.Db.SeedWork("active");
        await fixture.Db.SeedRoutingDecision($"routing:prov-{Guid.NewGuid():N}", workId, team, "no-capacity");
        try
        {
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Empty(await fixture.Db.ProvisionOpsFor(workload));
            Assert.Equal(0, await fixture.Db.OutboxCountForRecipient("link." + launcher));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
        }
    }

    [Fact]
    public async Task NoCapacity_EmitsOneDeterministicStart_ThenReplayIsANoOp()
    {
        var (workload, team, launcher) = (Workload(), Team(), Launcher());
        using var factory = await Host(workload, team, launcher);
        await fixture.Db.SeedAdminConfig(Policy, """{"minWorkers":1,"maxWorkers":2,"idleShutdownMinutes":15}""");
        var workId = await fixture.Db.SeedWork("active");
        await fixture.Db.SeedRoutingDecision($"routing:prov-{Guid.NewGuid():N}", workId, team, "no-capacity");
        try
        {
            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            var opId = ProvisioningOperationId.StartFor(workId, workload);
            var op = Assert.Single(await fixture.Db.ProvisionOpsFor(workload));
            Assert.Equal(opId, op.OperationId);
            Assert.Equal("start", op.Operation);
            Assert.Equal("requested", op.State);
            Assert.Equal("no-capacity", op.Reason);
            Assert.Equal(workId.ToString(), op.WorkId);
            Assert.Equal(0, op.Generation);

            // The full wire envelope: valid, addressed to the launcher
            // channel, spec mapped 1:1 with labels merged over the default.
            var envelope = await fixture.Db.OutboxEnvelopeByCommand(opId);
            Assert.NotNull(envelope);
            KernelOps.AssertEnvelopesValid([envelope]);
            Assert.True(await fixture.Db.OutboxRowByCommand(opId, "pending"), "op envelope must sit pending");
            var doc = JsonDocument.Parse(envelope).RootElement;
            Assert.Equal("org.facilitator.command.provision.start.v1", doc.GetProperty("type").GetString());
            Assert.Equal($"workload/{workload}", doc.GetProperty("subject").GetString());
            Assert.Equal("https://schemas.openfacilitator.org/v1/command.provision.start.v1.json",
                doc.GetProperty("dataschema").GetString());
            Assert.StartsWith("urn:facilitator:instance:", doc.GetProperty("source").GetString());
            var data = doc.GetProperty("data");
            Assert.Equal(opId, data.GetProperty("commandId").GetString());
            Assert.Equal(opId, data.GetProperty("operationId").GetString());
            Assert.Equal(workload, data.GetProperty("workloadId").GetString());
            Assert.Equal(team, data.GetProperty("team").GetString());
            Assert.Equal("kernel:provisioning:no-capacity", data.GetProperty("actorEvidenceRef").GetString());
            var spec = data.GetProperty("spec");
            Assert.Equal("registry.local/worker:1", spec.GetProperty("image").GetString());
            Assert.Equal("ofac-net", spec.GetProperty("network").GetString());
            Assert.Equal("info", spec.GetProperty("env").GetProperty("RUST_LOG").GetString());
            Assert.Equal("worker", spec.GetProperty("entrypoint")[0].GetString());
            Assert.True(spec.GetProperty("volumes")[0].GetProperty("readOnly").GetBoolean());
            Assert.Equal(30, spec.GetProperty("stopGraceSeconds").GetInt32());
            Assert.Equal(workload, spec.GetProperty("labels").GetProperty("ofac.workload").GetString());
            Assert.Equal("x", spec.GetProperty("labels").GetProperty("ofac.custom").GetString());

            // Replay: the trigger is unchanged, so the SAME id collides and
            // nothing new lands — one op row, one outbox row, zero appends.
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Single(await fixture.Db.ProvisionOpsFor(workload));
            Assert.Equal(1, await fixture.Db.OutboxCountForRecipient("link." + launcher));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
        }
    }

    [Fact]
    public async Task StalledAtCap_OfflineWorkload_EscalatesToStalledScaleupStart()
    {
        var (workload, team, launcher) = (Workload(), Team(), Launcher());
        using var factory = await Host(workload, team, launcher);
        await fixture.Db.SeedAdminConfig(Policy, """{"minWorkers":1,"maxWorkers":2,"idleShutdownMinutes":15}""");
        var workId = await fixture.Db.SeedWork("active");
        var assignmentId = await fixture.Db.SeedAssignmentRow(workId, "accepted", generation: 2);
        await fixture.Db.SeedAssignLedgerRow(workId, assignmentId, workload, generation: 2);
        await fixture.Db.SeedNudgeRow(assignmentId, 2, episode: 1, count: 3);
        try
        {
            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            var opId = ProvisioningOperationId.StartFor(workId, assignmentId, 2);
            var op = Assert.Single(await fixture.Db.ProvisionOpsFor(workload));
            Assert.Equal(opId, op.OperationId);
            Assert.Equal("stalled-scaleup", op.Reason);
            Assert.Equal(workId.ToString(), op.WorkId);
            Assert.Equal(assignmentId.ToString(), op.AssignmentId);
            Assert.Equal(2, op.Generation);
            Assert.True(await fixture.Db.OutboxRowByCommand(opId, "pending"));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
        }
    }

    [Fact]
    public async Task OfflineLauncher_RecordsTheOpButEmitsNoEnvelope()
    {
        var (workload, team) = (Workload(), Team());
        using var factory = await Host(workload, team, launcher: "");
        await fixture.Db.SeedAdminConfig(Policy, """{"minWorkers":1,"maxWorkers":2,"idleShutdownMinutes":15}""");
        try
        {
            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            var op = Assert.Single(await fixture.Db.ProvisionOpsFor(workload));
            Assert.Equal(ProvisioningOperationId.StartFor(workload, 0), op.OperationId);
            Assert.Equal("min-workers", op.Reason);
            Assert.Null(await fixture.Db.OutboxStatusByCommand(op.OperationId)); // zero emission, anywhere
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
        }
    }

    [Fact]
    public async Task MinShortfall_StartsOnce_AndAdminSurfaceShowsTheOp()
    {
        var (workload, team, launcher) = (Workload(), Team(), Launcher());
        using var factory = await Host(workload, team, launcher);
        using var kernel = factory.CreateAuthedClient();
        await fixture.Db.SeedAdminConfig(Policy, """{"minWorkers":1,"maxWorkers":2,"idleShutdownMinutes":15}""");
        try
        {
            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            var opId = ProvisioningOperationId.StartFor(workload, 0);
            Assert.True(await fixture.Db.OutboxRowByCommand(opId, "pending"));

            // The requested op is in flight: the second pass adds nothing.
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Single(await fixture.Db.ProvisionOpsFor(workload));

            var surface = await KernelOps.GetJsonAsync(kernel, "/api/admin/provisioning", HttpStatusCode.OK);
            var row = surface.GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("operationId").GetString() == opId);
            Assert.Equal(workload, row.GetProperty("workloadId").GetString());
            Assert.Equal("min-workers", row.GetProperty("reason").GetString());
            Assert.Equal("requested", row.GetProperty("state").GetString());
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
        }
    }

    [Fact]
    public async Task IdleLiveWorkload_StopsAtExactlyTheWindow_DeliveredStopMirrorsStopped()
    {
        var (workload, team, launcher) = (Workload(), Team(), Launcher());
        using var factory = await Host(workload, team, launcher);
        await fixture.Db.SeedAdminConfig(Policy, """{"minWorkers":1,"maxWorkers":2,"idleShutdownMinutes":15}""");
        // A running start op is on the ledger; the workload is live but has
        // NO live assignment, so the idle window starts on this first sweep.
        await fixture.Db.SeedProvisionOp(ProvisioningOperationId.StartFor(workload, 0), workload, team, "start", "running", "min-workers", generation: 0);
        await fixture.Db.SeedLinkSession(workload, 1, DateTime.UtcNow.AddMinutes(30));
        try
        {
            // Pass 1 seeds idle-since; 14 minutes is not yet the window.
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            factory.Clock!.Advance(TimeSpan.FromMinutes(14));
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));

            // Exactly idleShutdownMinutes: the stop lands (episode counter 1).
            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            var stopId = ProvisioningOperationId.StopFor(workload, 1);
            var stop = Assert.Single(await fixture.Db.ProvisionOpsFor(workload), o => o.Operation == "stop");
            Assert.Equal(stopId, stop.OperationId);
            Assert.Equal("idle-shutdown", stop.Reason);
            Assert.Equal("requested", stop.State);
            var envelope = await fixture.Db.OutboxEnvelopeByCommand(stopId);
            Assert.NotNull(envelope);
            KernelOps.AssertEnvelopesValid([envelope]);

            // The launcher kills the container: the command delivers and the
            // session lease dies -> the mirror settles the stop as stopped.
            await fixture.Db.MarkOutboxDeliveredByCommand(stopId);
            await fixture.Db.ExpireSessionLease(workload);
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Equal("stopped", Assert.Single(await fixture.Db.ProvisionOpsFor(workload), o => o.Operation == "stop").State);
            // Demotion hysteresis: ONE offline sweep is a blip, not a death.
            Assert.Equal("running", Assert.Single(await fixture.Db.ProvisionOpsFor(workload), o => o.Operation == "start").State);

            // Second consecutive offline sweep: the worker is gone, the
            // running op demotes to stopped.
            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Equal("stopped", Assert.Single(await fixture.Db.ProvisionOpsFor(workload), o => o.Operation == "start").State);
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(workload);
        }
    }

    [Fact]
    public async Task MaxWorkersCap_BlocksTheThirdStart()
    {
        var (workload, team, launcher) = (Workload(), Team(), Launcher());
        using var factory = await Host(workload, team, launcher);
        await fixture.Db.SeedAdminConfig(Policy, """{"minWorkers":1,"maxWorkers":2,"idleShutdownMinutes":15}""");
        await fixture.Db.SeedProvisionOp("cap-seed-1", workload, team, "start", "running", "min-workers", generation: 0);
        await fixture.Db.SeedProvisionOp("cap-seed-2", workload, team, "start", "running", "min-workers", generation: 1);
        var workId = await fixture.Db.SeedWork("active");
        await fixture.Db.SeedRoutingDecision($"routing:prov-{Guid.NewGuid():N}", workId, team, "no-capacity");
        try
        {
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Equal(2, (await fixture.Db.ProvisionOpsFor(workload)).Count);
            Assert.Equal(0, await fixture.Db.OutboxCountForRecipient("link." + launcher));
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
        }
    }

    [Fact]
    public async Task DeliveredStart_LiveSession_MirrorsToRunning()
    {
        var (workload, team, launcher) = (Workload(), Team(), Launcher());
        using var factory = await Host(workload, team, launcher);
        await fixture.Db.SeedAdminConfig(Policy, """{"minWorkers":1,"maxWorkers":2,"idleShutdownMinutes":15}""");
        var opId = ProvisioningOperationId.StartFor(workload, 0);
        await fixture.Db.SeedProvisionOp(opId, workload, team, "start", "requested", "min-workers", generation: 0);
        await fixture.Db.SeedLauncherDeliveryRow(opId, workload, "org.facilitator.command.provision.start.v1");
        await fixture.Db.SeedLinkSession(workload, 1, DateTime.UtcNow.AddMinutes(30));
        try
        {
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Equal("running", Assert.Single(await fixture.Db.ProvisionOpsFor(workload)).State);
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
            await fixture.Db.ExpireSessionLease(workload);
        }
    }

    [Fact]
    public async Task DeliveredStart_NeverGoesLive_DemotesToFailedAndReArmsMinFloor()
    {
        var (workload, team, launcher) = (Workload(), Team(), Launcher());
        using var factory = await Host(workload, team, launcher);
        await fixture.Db.SeedAdminConfig(Policy, """{"minWorkers":1,"maxWorkers":2,"idleShutdownMinutes":15}""");
        var firstOp = ProvisioningOperationId.StartFor(workload, 0);
        await fixture.Db.SeedProvisionOp(firstOp, workload, team, "start", "requested", "min-workers", generation: 0);
        await fixture.Db.SeedLauncherDeliveryRow(firstOp, workload, "org.facilitator.command.provision.start.v1");
        try
        {
            // Sweep 1: delivered, fleet never leased — hysteresis keeps the
            // op in flight (a blip does not demote), nothing appends.
            Assert.Equal(0, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            Assert.Equal("requested", Assert.Single(await fixture.Db.ProvisionOpsFor(workload)).State);

            // Sweep 2: second consecutive miss demotes the spent command to
            // failed, and the SAME sweep re-arms the min floor under the
            // next episode id — one fresh op + envelope, no wedge.
            Assert.Equal(1, await Sweeper(factory).SweepOnceAsync(CancellationToken.None));
            var rows = await fixture.Db.ProvisionOpsFor(workload);
            Assert.Equal(2, rows.Count);
            Assert.Equal("failed", rows.Single(r => r.OperationId == firstOp).State);
            var retry = ProvisioningOperationId.StartFor(workload, 1);
            Assert.Contains(retry, rows.Select(r => r.OperationId));
            Assert.Equal("requested", rows.Single(r => r.OperationId == retry).State);
            Assert.True(await fixture.Db.OutboxRowByCommand(retry, "pending"), "retry envelope must sit pending");
        }
        finally
        {
            await fixture.Db.AdminConfigPurge(Policy);
        }
    }

    private async Task<KernelTestFactory> Host(string workload, string team, string launcher)
    {
        var factory = new KernelTestFactory(
            fixture, noDispatcher: true, settings: Settings(workload, team, launcher), fakeClock: true);
        // The kernel entrypoint migrates the schema before it serves, so one
        // health roundtrip proves the tables exist before any probe seeding.
        using var warmup = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await warmup.GetAsync("/health")).StatusCode);
        return factory;
    }

    private static ProvisioningProcessor Sweeper(KernelTestFactory factory) =>
        factory.Services.GetRequiredService<ProvisioningProcessor>();
}
