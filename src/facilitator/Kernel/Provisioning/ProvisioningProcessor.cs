using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Provisioning;

/// <summary>
/// The provisioning scale law (Plan 35 C): a periodic sweep that observes the
/// fleet and appends launcher operations — each one a deterministic-id row on
/// <c>facilitator.provisioning_operations</c> plus a
/// <c>command.provision.start|stop.v1</c> outbox row to
/// <c>link.&lt;Provisioning:LauncherEndpoint&gt;</c>, in ONE transaction (the
/// NudgeSweeper emission law). The policy is the <c>admin_config</c>
/// <c>workload-scale</c> row — ABSENT means manual mode: the sweep does
/// exactly nothing. A workload is managed only when it has BOTH that policy
/// row and a <c>Provision:Workloads</c> spec. Liveness authority is the
/// link-plane lease: a workload is live iff its <c>link_sessions</c> row's
/// lease is unexpired (the same online law the nudge sweeper applies).
/// Order per sweep (escalation first, never reverse):
/// <list type="number">
/// <item><b>State mirror</b> — a requested START whose command delivered and
/// whose workload is now live → running; a requested STOP delivered against
/// a non-live workload, or a command terminally REJECTED (acked receipt with
/// <c>status=rejected</c>) → stopped; a dead-lettered command → failed
/// (delivery budget spent — a fresh work-keyed op may re-fire); a delivered
/// command whose fleet opposite never materializes (worker lost between ack
/// and effect) → failed after two consecutive sweeps (effect-pending
/// demotion — a spent command must never wedge the workload's arms); a
/// running op whose workload stays without a lease across two consecutive
/// sweeps → stopped (worker-lost demotion, hysteresis so a blip does not
/// demote).</item>
/// <item><b>worker-lost</b> (the kill-9 continuity arm): an execution attempt
/// that <c>failed</c> AT the assignment's current generation (no
/// re-dispatch happened yet) whose owning endpoint is a managed, non-live
/// workload → a work-keyed START (reason <c>worker-lost</c>, tuple
/// generation = the failed attempt's) AND a fresh routing intent so the
/// funnel re-dispatches at generation+1 WITHOUT an operator call — the
/// effect command id is <c>work-redispatch:&lt;assignmentId-N&gt;:&lt;newGen&gt;</c>
/// (deterministic: exactly one dispatch pair per failed attempt, the unique
/// outbox command id absorbs replays).</item>
/// <item><b>stalled-scaleup</b>: an assignment whose latest nudge ledger row
/// at the CURRENT generation is at <see cref="NudgeSweeper.NudgeCap"/> and
/// whose owning workload is managed and NOT live → work-keyed START (the
/// nudge budget is spent; a new container re-parks at a fresh generation
/// when the funnel re-resolves).</item>
/// <item><b>no-capacity</b>: <c>routing_decisions</c> rows with
/// <c>basis='no-capacity' AND status='pending'</c> — the honest join: the
/// decision row IS the funnel's still-pending-intent marker (re-upserted per
/// attempt with <c>last_routing_attempt_at</c>, flipped to <c>routed</c> in
/// the same tx as the assign effect, so a pending row means the intent is
/// un-routed; the basis distinguishes "advertised but nobody live" from
/// <c>no-candidate</c>). The workload is the first spec whose TEAM matches
/// the decision's team; op id is work-keyed (generation literal 0).</item>
/// <item><b>min-workers</b>: managed workload, non-live, policy minWorkers
/// &gt; 0, and NO non-failed ledger row for it — a boot-time shortfall, at
/// most one op per episode (a spent bootstrap — dead-lettered or
/// effect-pending, state <c>failed</c> — leaves the arm armed for a retry
/// with the next episode id; a <c>stopped</c> row keeps it disarmed: a
/// re-launch after a stop arrives via a work-keyed arm, never a re-arm).</item>
/// <item><b>idle-shutdown</b>: live workload with NO live assignment (the
/// steer-arm read: delivered assign-family rows parsed client-side,
/// non-terminal assignment) for at least <c>idleShutdownMinutes</c> → STOP.
/// <c>minWorkers</c> is a STARTUP floor (the min arm), not a stop floor: an
/// idle stop may bring a min-1 workload to zero, and the ledger gate keeps
/// it stopped until a work-keyed arm (work arriving, a failed attempt)
/// re-launches. Idle-since lives in process memory, seeded the first sweep
/// that sees the workload idle — a kernel restart restarts the window
/// (honest, bounded: scale-down is advisory latency, never correctness).</item>
/// </list>
/// Gating laws for EVERY append: no op for the workload may sit in
/// <c>requested</c> (one command in flight at a time — replayed sweeps are
/// no-ops, not errors), and no START may push the workload's
/// <c>requested|running</c> count to <c>maxWorkers</c>. At most one append
/// per workload per sweep. Ids come from
/// <see cref="ProvisioningOperationId"/>, so an identical trigger produces an
/// identical id: the PK and the unique outbox <c>command_id</c> make the
/// at-least-once enqueue a no-op end to end. With an empty
/// <c>Provisioning:LauncherEndpoint</c> the op row is still written (the
/// honest manual-with-intent-ledger mode) but no envelope is emitted.
/// </summary>
public sealed class ProvisioningProcessor(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    EnvelopeValidator envelopeValidator,
    TimeProvider timeProvider,
    IConfiguration configuration,
    KernelMetrics metrics,
    ILogger<ProvisioningProcessor> logger,
    ProvisioningOptions options) : BackgroundService
{
    /// <summary>admin_config resource carrying the scale policy.</summary>
    public const string PolicyResource = "workload-scale";

    /// <summary>Assignment states with no execution behind them any more
    /// (mirror of <c>ExecutionLedger.AssignmentTerminal</c>, which is
    /// private to the Work feature).</summary>
    private static readonly string[] AssignmentTerminal = ["completed", "failed", "cancelled", "expired"];

    /// <summary>The 24-era assign-family command-id bases: the rows whose
    /// envelope pins an assignment's owning endpoint.</summary>
    private static readonly string[] AssignFamilyPrefixes = ["work-assign:", "work-assign-coding:", "work-redispatch:"];

    /// <summary>Idle-since per workload, seeded the first sweep that observes
    /// (live &amp;&amp; no live assignments); removed when a live assignment
    /// appears, the workload goes non-live, or a stop is appended. Process
    /// memory only: a restart restarts the idle window (documented law).</summary>
    private readonly Dictionary<string, DateTime> _idleSince = new(StringComparer.Ordinal);

    /// <summary>Consecutive sweeps a RUNNING op was observed without a live
    /// session (the two-sweep demotion hysteresis), keyed by operation id.</summary>
    private readonly Dictionary<string, int> _offlineSweeps = new(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.SweepIntervalSeconds)), stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                // The sweep is advisory: one bad pass must never kill the loop.
                logger.LogWarning(ex, "provisioning sweep pass failed");
            }
        }
    }

    /// <summary>One sweep (public for fake-clock tests): mirror op states,
    /// then evaluate the escalation arms in law order. Returns the number of
    /// ops appended this pass.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var policy = await PolicyAsync(db, ct);
        var specs = options.Workloads
            .Where(s => s.WorkloadId.Length > 0 && s.Image.Length > 0)
            .GroupBy(s => s.WorkloadId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Manual mode: no policy row, or nothing named by config => exactly nothing.
        if (policy is null || specs.Count == 0)
        {
            return 0;
        }

        var managed = specs.Keys.ToArray();

        // Liveness: the link lease is the online authority (§7.3, the nudge
        // sweeper's law). One query, all managed workloads.
        var live = (await db.LinkSessions.AsNoTracking()
            .Where(s => managed.Contains(s.LogicalEndpointId) && s.LeaseUntil > now)
            .Select(s => s.LogicalEndpointId)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        // Ledger + delivery ledger for the mirror.
        var ops = await db.ProvisioningOps
            .Where(o => managed.Contains(o.WorkloadId))
            .ToListAsync(ct);
        var byId = ops.ToDictionary(o => o.OperationId, StringComparer.Ordinal);
        var outboxBy = new Dictionary<string, OutboxEntity>(StringComparer.Ordinal);
        if (ops.Count > 0)
        {
            var ids = ops.Select(o => o.OperationId).ToArray();
            foreach (var row in await db.Outbox.AsNoTracking()
                .Where(o => ids.Contains(o.CommandId))
                .ToListAsync(ct))
            {
                outboxBy[row.CommandId] = row;
            }
        }

        var rejected = await RejectedDeliveriesAsync(db, outboxBy, ct);

        // ---- 1. state mirror -------------------------------------------------
        foreach (var op in ops)
        {
            outboxBy.TryGetValue(op.OperationId, out var delivery);
            if (op.State == "requested")
            {
                if (delivery?.Status == "dead_letter")
                {
                    op.State = "failed";
                    op.UpdatedAt = now;
                    _offlineSweeps.Remove(op.OperationId);
                    continue;
                }

                var terminallyRejected = delivery is not null && rejected.Contains(delivery.OutboxId);
                if (terminallyRejected)
                {
                    // Receiver refused for good: honest stopped, no wedge — a
                    // fresh work-keyed trigger produces a fresh id.
                    op.State = "stopped";
                    op.UpdatedAt = now;
                    _offlineSweeps.Remove(op.OperationId);
                    continue;
                }

                if (delivery?.Status == "delivered")
                {
                    var workloadLive = live.Contains(op.WorkloadId);
                    if ((op.Operation == "start") == workloadLive)
                    {
                        // The effect materialized: a delivered START found
                        // its workload leased, a delivered STOP found it
                        // gone.
                        op.State = workloadLive ? "running" : "stopped";
                        op.UpdatedAt = now;
                        _offlineSweeps.Remove(op.OperationId);
                    }
                    else
                    {
                        // Effect-pending demotion: the launcher ACKED but
                        // the fleet opposite never materialized (a worker
                        // lost between ack and create). Same two-sweep
                        // hysteresis as the running demotion — a blip does
                        // not demote — then final <c>failed</c>: the spent
                        // command must not wedge the workload's arms
                        // forever; a fresh trigger re-fires.
                        var misses = _offlineSweeps.TryGetValue(op.OperationId, out var seen) ? seen + 1 : 1;
                        if (misses >= 2)
                        {
                            op.State = "failed";
                            op.UpdatedAt = now;
                            _offlineSweeps.Remove(op.OperationId);
                        }
                        else
                        {
                            _offlineSweeps[op.OperationId] = misses;
                        }
                    }
                }
            }
            else if (op.State == "running")
            {
                if (live.Contains(op.WorkloadId))
                {
                    _offlineSweeps.Remove(op.OperationId);
                }
                else
                {
                    var misses = _offlineSweeps.TryGetValue(op.OperationId, out var seen) ? seen + 1 : 1;
                    if (misses >= 2)
                    {
                        // Worker-lost demotion: two consecutive sweeps without
                        // a lease mean the container is gone. Final for THIS
                        // op — the relaunch rides a fresh work-keyed trigger.
                        op.State = "stopped";
                        op.UpdatedAt = now;
                        _offlineSweeps.Remove(op.OperationId);
                    }
                    else
                    {
                        _offlineSweeps[op.OperationId] = misses;
                    }
                }
            }
        }

        // Post-mirror per-workload views (the tracked entities, so appends
        // made THIS sweep count immediately: one append per workload max).
        var inflight = ops.Where(o => o.State == "requested")
            .Select(o => o.WorkloadId).ToHashSet(StringComparer.Ordinal);
        var active = ops.Where(o => o.State is "requested" or "running")
            .GroupBy(o => o.WorkloadId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var rowCount = ops.GroupBy(o => o.WorkloadId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var appended = new HashSet<string>(StringComparer.Ordinal);
        int ActiveCount(string w) => active.GetValueOrDefault(w);
        bool HasInflight(string w) => inflight.Contains(w) || !appended.Add(w);

        var source = $"urn:facilitator:instance:{Uri.EscapeDataString(KernelOptions.ReplicaId(configuration))}";
        var appends = 0;

        async Task AppendAsync(ProvisioningOpEntity op, JsonObject? envelope)
        {
            if (byId.TryGetValue(op.OperationId, out var existing))
            {
                return; // this sweep already knows the row (no double-append)
            }

            byId[op.OperationId] = op;
            db.ProvisioningOps.Add(op);
            if (envelope is not null)
            {
                var json = envelope.ToJsonString();
                var verdict = envelopeValidator.Validate(json);
                if (!verdict.Ok)
                {
                    throw new InvalidOperationException($"provision envelope rejected: {verdict.ErrorClass}: {verdict.Message}");
                }

                db.Outbox.Add(new OutboxEntity
                {
                    OutboxId = Guid.NewGuid(),
                    RecipientEndpointId = $"link.{options.LauncherEndpoint}",
                    AggregateType = "work",
                    AggregateId = op.WorkId,
                    EventType = op.Operation == "start" ? ProvisionEnvelopes.StartType : ProvisionEnvelopes.StopType,
                    CommandId = op.OperationId,
                    Envelope = json,
                    WorkId = op.WorkId,
                    Status = "pending",
                    AvailableAt = now,
                    Attempts = 0,
                    CreatedAt = now,
                });
            }

            appends++;
            logger.LogInformation(
                "provisioning op appended workload={WorkloadId} op={OperationId} operation={Operation} reason={Reason} envelope={Envelope}",
                op.WorkloadId, op.OperationId, op.Operation, op.Reason, envelope is not null);
            metrics.ProvisioningOp(op.Operation, op.Reason!);
        }

        // An op row for this exact trigger already exists (any state): the
        // arm fired before; dedupe is the deterministic id itself.
        bool Fresh(string opId) => !byId.ContainsKey(opId);

        // ---- 2. worker-lost (kill-9 continuity) --------------------------------
        // Attempts that failed AT the assignment's current generation: a
        // re-dispatch would bump the line, so this exactly selects
        // not-yet-respawned failures.
        var failedAttempts = await (
            from attempt in db.ExecutionAttempts.AsNoTracking()
            join assignment in db.Assignments.AsNoTracking() on attempt.AssignmentId equals assignment.AssignmentId
            where attempt.Status == "failed" && assignment.CurrentExecutionGeneration == attempt.Generation
            select new { attempt.AssignmentId, attempt.Generation, assignment.WorkId }).ToListAsync(ct);
        foreach (var failed in failedAttempts.OrderBy(a => a.WorkId).ThenBy(a => a.AssignmentId))
        {
            var owner = await OwningWorkloadAsync(db, failed.AssignmentId, ct);
            if (owner is null || !specs.TryGetValue(owner, out var spec) || live.Contains(owner)
                || ActiveCount(owner) >= policy.Value.MaxWorkers)
            {
                continue;
            }

            var opId = ProvisioningOperationId.StartFor(failed.WorkId, failed.AssignmentId, failed.Generation);
            var newGeneration = failed.Generation + 1;
            var effectKey = $"{failed.AssignmentId:N}:{newGeneration}";
            var intentExists = await db.Outbox.AsNoTracking()
                .AnyAsync(o => o.CommandId == $"routing:work-redispatch:{effectKey}", ct);
            var opMissing = Fresh(opId) && !HasInflight(owner);
            if (!opMissing && intentExists)
            {
                continue; // fully handled: op recorded AND re-dispatch queued
            }

            if (opMissing)
            {
                await AppendAsync(
                    NewOp(opId, owner, spec.Team, "start", "worker-lost", now,
                        failed.WorkId, failed.AssignmentId, failed.Generation),
                    Emit(ProvisionEnvelopes.Start(opId, owner, spec.Team, "worker-lost", spec, source, now, AssignDeadline)));
            }

            if (!intentExists)
            {
                // The 24-era re-dispatch law, kernel-driven: a fresh intent on
                // the SAME work at the new generation, rebuilt from the
                // envelope that carried the original assign (the
                // AppendSteerDeliveryAsync arm's restart-with-context shape).
                var intent = await BuildRedispatchIntentAsync(db, failed.WorkId, failed.AssignmentId, newGeneration, ct);
                if (intent is not null)
                {
                    db.Outbox.Add(RoutingIntentEnvelope.OutboxRow(intent, now, envelopeValidator));
                }
            }
        }

        // ---- 3. stalled-scaleup -------------------------------------------------
        var stalledAssignments = await (
            from assignment in db.Assignments.AsNoTracking()
            where assignment.Status == "accepted" || assignment.Status == "running" || assignment.Status == "input_required"
            join nudge in db.Nudges.AsNoTracking()
                on new { A = assignment.AssignmentId, G = assignment.CurrentExecutionGeneration }
                equals new { A = nudge.AssignmentId, G = nudge.Generation }
            where nudge.NudgeCount >= NudgeSweeper.NudgeCap
            select new { assignment.AssignmentId, assignment.WorkId, assignment.CurrentExecutionGeneration }).ToListAsync(ct);
        foreach (var stalled in stalledAssignments.OrderBy(a => a.WorkId).ThenBy(a => a.AssignmentId).DistinctBy(a => a.AssignmentId))
        {
            var owner = await OwningWorkloadAsync(db, stalled.AssignmentId, ct);
            if (owner is null || !specs.ContainsKey(owner) || live.Contains(owner)
                || ActiveCount(owner) >= policy.Value.MaxWorkers || HasInflight(owner))
            {
                continue;
            }

            var opId = ProvisioningOperationId.StartFor(stalled.WorkId, stalled.AssignmentId, stalled.CurrentExecutionGeneration);
            if (!Fresh(opId))
            {
                continue;
            }

            await AppendAsync(
                NewOp(opId, owner, specs[owner].Team, "start", "stalled-scaleup", now,
                    stalled.WorkId, stalled.AssignmentId, stalled.CurrentExecutionGeneration),
                Emit(ProvisionEnvelopes.Start(opId, owner, specs[owner].Team, "stalled-scaleup", specs[owner], source, now, AssignDeadline)));
        }

        // ---- 4. no-capacity -----------------------------------------------------
        // Join: routing_decisions.status='pending' AND basis='no-capacity'.
        // The decision row IS the funnel's own marker that the intent is
        // still un-routed (RoutingSink re-upserts it on every attempt with
        // backoff, never dead-letters, and flips it to 'routed' in the SAME
        // tx that emits the assign effect — so a pending row is exactly "the
        // intent's outbox row is still pending and no effect row exists").
        // basis distinguishes advertised-elsewhere-but-no-live-session from
        // no-candidate (nobody ever advertised the capability).
        var noCapacity = await db.RoutingDecisions.AsNoTracking()
            .Where(d => d.Status == "pending" && d.Basis == "no-capacity")
            .OrderBy(d => d.WorkId)
            .ToListAsync(ct);
        foreach (var decision in noCapacity)
        {
            if (decision.TeamSlug is not { Length: > 0 } team)
            {
                continue; // no team on the work: no team-scoped workload can match
            }

            var match = specs.Values.FirstOrDefault(s => string.Equals(s.Team, team, StringComparison.Ordinal));
            if (match is null || live.Contains(match.WorkloadId)
                || ActiveCount(match.WorkloadId) >= policy.Value.MaxWorkers || HasInflight(match.WorkloadId))
            {
                continue;
            }

            var opId = ProvisioningOperationId.StartFor(decision.WorkId, match.WorkloadId);
            if (!Fresh(opId))
            {
                continue;
            }

            await AppendAsync(
                NewOp(opId, match.WorkloadId, match.Team, "start", "no-capacity", now,
                    decision.WorkId, assignmentId: null, generation: 0),
                Emit(ProvisionEnvelopes.Start(opId, match.WorkloadId, match.Team, "no-capacity", match, source, now, AssignDeadline)));
        }
        // ---- 5. min-workers shortfall -------------------------------------------
        if (policy.Value.MinWorkers > 0)
        {
            foreach (var spec in specs.Values.OrderBy(s => s.WorkloadId, StringComparer.Ordinal))
            {
                // A spent bootstrap (state `failed`) does NOT disarm the
                // floor — the shortfall is still real; the retry rides the
                // next episode id. Any live/queued/stopped row does.
                var nonFailed = ops.Count(o => o.WorkloadId == spec.WorkloadId && o.State != "failed");
                if (live.Contains(spec.WorkloadId) || nonFailed > 0
                    || ActiveCount(spec.WorkloadId) >= policy.Value.MaxWorkers || HasInflight(spec.WorkloadId))
                {
                    continue;
                }
                var opId = ProvisioningOperationId.StartFor(spec.WorkloadId, rowCount.GetValueOrDefault(spec.WorkloadId));
                if (!Fresh(opId))
                {
                    continue;
                }
                await AppendAsync(
                    NewOp(opId, spec.WorkloadId, spec.Team, "start", "min-workers", now,
                        workId: null, assignmentId: null, generation: 0),
                    Emit(ProvisionEnvelopes.Start(opId, spec.WorkloadId, spec.Team, "min-workers", spec, source, now, AssignDeadline)));
            }
        }

        // ---- 6. idle-shutdown ----------------------------------------------------
        var window = TimeSpan.FromMinutes(policy.Value.IdleShutdownMinutes);
        foreach (var spec in specs.Values.OrderBy(s => s.WorkloadId, StringComparer.Ordinal))
        {
            var workload = spec.WorkloadId;
            var isLive = live.Contains(workload);
            if (!isLive)
            {
                _idleSince.Remove(workload);
                continue;
            }

            if (await HasLiveAssignmentAsync(db, workload, ct))
            {
                _idleSince.Remove(workload); // working: the window restarts
                continue;
            }

            var since = _idleSince.TryGetValue(workload, out var seenAt) ? seenAt : (_idleSince[workload] = now);
            if (now - since < window || HasInflight(workload))
            {
                continue;
            }

            var stopId = ProvisioningOperationId.StopFor(workload, rowCount.GetValueOrDefault(workload));
            _idleSince.Remove(workload); // window consumed either way
            if (!Fresh(stopId))
            {
                continue;
            }

            await AppendAsync(
                NewOp(stopId, workload, spec.Team, "stop", "idle-shutdown", now,
                    workId: null, assignmentId: null, generation: rowCount.GetValueOrDefault(workload)),
                Emit(ProvisionEnvelopes.Stop(stopId, workload, spec.Team, "idle-shutdown", source, now, AssignDeadline)));
        }

        if (db.ChangeTracker.HasChanges())
        {
            // Mirror mutations and appends flush together (ONE tx), then the
            // dispatcher wake (the NudgeSweeper emission law).
            await db.SaveChangesAsync(ct);
            if (appends > 0 && options.LauncherEndpoint.Length > 0)
            {
                await PostgresNotifications.NotifyOutboxAsync(db, "provisioning", ct);
            }
        }

        return appends;
    }

    /// <summary>One-day command deadline (the ProviderEnvelopes interim law:
    //  no negotiation yet); RFC 3339 seconds-precision per the wire.</summary>
    private static readonly TimeSpan AssignDeadline = TimeSpan.FromDays(1);

    private static ProvisioningOpEntity NewOp(
        string opId, string workloadId, string team, string operation, string reason, DateTime now,
        Guid? workId, Guid? assignmentId, int? generation) => new()
        {
            OperationId = opId,
            WorkloadId = workloadId,
            Team = team,
            Operation = operation,
            State = "requested",
            Reason = reason,
            WorkId = workId,
            AssignmentId = assignmentId,
            Generation = generation,
            CreatedAt = now,
            UpdatedAt = now,
        };

    /// <summary>Envelope when the launcher is addressable; null in
    /// manual-with-intent-ledger mode (op row only, no emission).</summary>
    private JsonObject? Emit(JsonObject envelope) =>
        options.LauncherEndpoint.Length > 0 ? envelope : null;

    /// <summary>The scale policy: {minWorkers,maxWorkers,idleShutdownMinutes};
    /// null (no row or unparseable) = manual mode = the sweep is inert.</summary>
    internal static async Task<(int MinWorkers, int MaxWorkers, int IdleShutdownMinutes)?> PolicyAsync(
        FacilitatorDbContext db, CancellationToken ct)
    {
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == PolicyResource)
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (payload is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(payload);
            var min = node?["minWorkers"]?.GetValue<int>();
            var max = node?["maxWorkers"]?.GetValue<int>();
            var idle = node?["idleShutdownMinutes"]?.GetValue<int>();
            if (min is null || max is null || idle is null || min > max || max < 1)
            {
                return null;
            }

            return (min.Value, max.Value, idle.Value);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Outbox ids whose link delivery was ACKED with a REJECTED
    /// receipt (terminal refusal — a retryable rejection is still in flight
    /// and its outbox row is not yet delivered, so it is absent here).
    /// The receipt is jsonb; parsed client-side (the steer-arm law: jsonb
    /// casts do not translate).</summary>
    private static async Task<HashSet<Guid>> RejectedDeliveriesAsync(
        FacilitatorDbContext db, Dictionary<string, OutboxEntity> outboxBy, CancellationToken ct)
    {
        var ids = outboxBy.Values.Select(o => o.OutboxId).ToArray();
        var result = new HashSet<Guid>();
        if (ids.Length == 0)
        {
            return result;
        }

        var receipts = await db.LinkDeliveries.AsNoTracking()
            .Where(d => ids.Contains(d.DeliveryId) && d.State == "acked" && d.Receipt != null)
            .Select(d => new { d.DeliveryId, d.Receipt })
            .ToListAsync(ct);
        foreach (var row in receipts)
        {
            try
            {
                using var doc = JsonDocument.Parse(row.Receipt!);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("status", out var status)
                    && status.GetString() == "rejected")
                {
                    result.Add(row.DeliveryId);
                }
            }
            catch (JsonException)
            {
                // Unparseable receipt: not evidence of rejection.
            }
        }

        return result;
    }

    /// <summary>The managed workload that carried this assignment (the
    /// NudgeSweeper ownership read, scoped to managed specs): the latest
    /// delivered assign-family row's recipient, the envelope's assignmentId
    /// matched in memory (jsonb has no translatable LIKE). Returns the
    /// endpoint id only when it is a configured workload.</summary>
    private async Task<string?> OwningWorkloadAsync(FacilitatorDbContext db, Guid assignmentId, CancellationToken ct)
    {
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.EventType == "org.facilitator.command.work.assign.v1"
                && o.Status == "delivered"
                && (o.CommandId.StartsWith("work-assign:")
                    || o.CommandId.StartsWith("work-assign-coding:")
                    || o.CommandId.StartsWith("work-redispatch:")))
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new { o.Envelope, o.RecipientEndpointId })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            if (ExecutionLedger.AssignmentIdOf(row.Envelope) != assignmentId)
            {
                continue;
            }

            var endpoint = row.RecipientEndpointId.StartsWith("link.", StringComparison.Ordinal)
                ? row.RecipientEndpointId["link.".Length..] : row.RecipientEndpointId;
            return options.Workloads.Any(s => string.Equals(s.WorkloadId, endpoint, StringComparison.Ordinal))
                ? endpoint : null;
        }

        return null;
    }

    /// <summary>True when a delivered assign-family command to
    /// <c>link.&lt;workload&gt;</c> pins a NON-terminal assignment (the
    /// steer-arm read-back: parse envelopes client-side, jsonb LIKE does not
    /// translate). The idle law's "has work" observation.</summary>
    private static async Task<bool> HasLiveAssignmentAsync(FacilitatorDbContext db, string workloadId, CancellationToken ct)
    {
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.RecipientEndpointId == "link." + workloadId
                && o.EventType == "org.facilitator.command.work.assign.v1"
                && o.Status == "delivered"
                && (o.CommandId.StartsWith("work-assign:")
                    || o.CommandId.StartsWith("work-assign-coding:")
                    || o.CommandId.StartsWith("work-redispatch:")))
            .Select(o => new { o.Envelope, o.CommandId })
            .ToListAsync(ct);
        var assignmentIds = rows
            .Select(r => ExecutionLedger.AssignmentIdOf(r.Envelope))
            .OfType<Guid>()
            .Distinct()
            .ToArray();
        if (assignmentIds.Length == 0)
        {
            return false;
        }

        return await db.Assignments.AsNoTracking()
            .AnyAsync(a => assignmentIds.Contains(a.AssignmentId) && !AssignmentTerminal.Contains(a.Status), ct);
    }

    /// <summary>The worker-lost arm's re-dispatch intent: rebuilt from the
    /// envelope that carried the original assign (capability/target/objective
    /// survive; the workload rides as the endpoint preference so the fresh
    /// container binds it), at the next execution generation. The effect key
    /// <c>&lt;assignmentId-N&gt;:&lt;newGeneration&gt;</c> makes the effect
    /// row <c>work-redispatch:&lt;assignmentId-N&gt;:&lt;newGeneration&gt;</c>
    /// — deterministic, so exactly ONE dispatch pair exists per failed
    /// attempt regardless of sweep replays.</summary>
    private static async Task<RoutingIntent?> BuildRedispatchIntentAsync(
        FacilitatorDbContext db, Guid workId, Guid assignmentId, int newGeneration, CancellationToken ct)
    {
        var original = await db.Outbox.AsNoTracking()
            .Where(o => o.WorkId == workId && o.EventType == "org.facilitator.command.work.assign.v1"
                && (o.CommandId.StartsWith("work-assign:")
                    || o.CommandId.StartsWith("work-assign-coding:")
                    || o.CommandId.StartsWith("work-redispatch:")))
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (original is null)
        {
            return null;
        }

        var data = JsonNode.Parse(original.Envelope)?.AsObject()["data"]?.AsObject();
        if (data?["capabilityId"]?.GetValue<string>() is not { Length: > 0 } capabilityId)
        {
            return null;
        }

        var inputs = data["inputs"]?.AsObject() ?? new JsonObject();
        var objective = inputs["objective"]?.GetValue<string>() ?? string.Empty;
        var extraInputs = inputs.DeepClone().AsObject();
        extraInputs.Remove("objective", out _);
        extraInputs.Remove("sourceRefs", out _);
        var work = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId)
            .Select(w => new { w.Objective, TeamSlug = w.Project!.Team!.Slug }).FirstOrDefaultAsync(ct);
        var endpoint = original.RecipientEndpointId.StartsWith("link.", StringComparison.Ordinal)
            ? original.RecipientEndpointId["link.".Length..] : original.RecipientEndpointId;
        return new RoutingIntent(
            workId,
            capabilityId,
            work?.TeamSlug,
            PreferenceEndpointId: endpoint,
            TargetId: data["targetId"]?.GetValue<string>() ?? "kernel",
            CommandIdBase: "work-redispatch",
            EffectKey: $"{assignmentId:N}:{newGeneration}",
            Objective: objective.Length > 0 ? objective : work?.Objective ?? string.Empty,
            SourceRefsJson: inputs["sourceRefs"]?.ToJsonString() is { } refs && refs != "[]" ? refs : null,
            ExtraInputsJson: extraInputs.Count > 0 ? extraInputs.ToJsonString() : null,
            ActorEvidenceRef: "kernel:provisioning:worker-lost",
            ExecutionGeneration: newGeneration);
    }
}
