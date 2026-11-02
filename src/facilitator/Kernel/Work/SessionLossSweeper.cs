using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The session-loss law (the kill-9 continuity gap): when the
/// gateway that OWNS a live execution dies without a goodbye — kill-9, host
/// loss — its link-session lease expires and nobody posts the honest terminal
/// event any more (the worker's state died with the container, and the
/// gateway's own reconcile only runs on a container that comes back). Left
/// alone, the attempt sits <c>working</c> forever and the provisioning
/// worker-lost arm — which keys on a FAILED attempt at the assignment's
/// current generation — never relaunches. So the kernel synthesizes the
/// event: for every open <c>working</c> attempt at its assignment's current
/// generation whose owning endpoint has no live session lease, a second
/// consecutive offline sweep posts a kernel-authored
/// <c>event.assignment.failed.v1</c> (<c>reason=session-lease-expired</c>,
/// <c>retryable=true</c>) through the same inbox + <see cref="ResultIngress"/>
/// finalize path a real worker event rides (the DB stays the coordination
/// authority). Hysteresis: the first offline pass only marks the attempt
/// suspect (in memory); a pass that sees a live lease clears the mark — a
/// blip shorter than two sweeps never fails anything. The inbox row's
/// (source,event_id) uniqueness is the cross-replica dedupe: the event id is
/// <c>session-loss:&lt;executionId&gt;</c>, so N replicas converge on one
/// failed event per dead execution. Like the scale law itself, the gate is the
/// <c>workload-scale</c> policy row: a manual-mode deployment stays silent.
/// </summary>
public sealed class SessionLossSweeper(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    IngressStore inbox,
    IServiceScopeFactory scopeFactory,
    EnvelopeValidator envelopeValidator,
    TimeProvider timeProvider,
    KernelMetrics metrics,
    ILogger<SessionLossSweeper> logger,
    Facilitator.Kernel.Work.SessionLossSweeper.SessionLossOptions options) : BackgroundService
{
    /// <summary>Config section for the sweep loop (the nudge precedent):
    /// hosted passes fire on <c>SessionLoss:SweepIntervalSeconds</c>; test
    /// hosts park it and drive <see cref="SweepOnceAsync"/> manually.</summary>
    public sealed class SessionLossOptions
    {
        /// <summary>Config section name.</summary>
        public const string SectionName = "SessionLoss";

        /// <summary>Seconds between hosted sweep passes. Two passes form the
        /// hysteresis window, so this plus the session lease bound how fast a
        /// dead gateway fails its executions.</summary>
        public int SweepIntervalSeconds { get; set; } = 5;
    }

    /// <summary>Envelope type of the event this law posts.</summary>
    public const string FailedType = ResultIngress.FailedType;

    /// <summary>The reason wire value on every synthesized event.</summary>
    public const string Reason = "session-lease-expired";

    /// <summary>First offline sighting per execution id (hysteresis mark).
    /// Per-replica and intentionally stateless-beyond-memory: losing it on a
    /// restart costs one extra sweep, the inbox dedupe keeps the effect
    /// single-shot either way.</summary>
    private readonly Dictionary<Guid, DateTime> _suspects = [];

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
                logger.LogWarning(ex, "session-loss sweep pass failed");
            }
        }
    }

    /// <summary>One sweep (public for fake-clock tests): every open working
    /// attempt at its assignment's current generation whose owner has no
    /// live lease is marked on first sight and failed on the second. Returns
    /// the number of failed events posted (first or repeated pass).</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The gate: the kernel owns worker lifecycle exactly when the
        // workload-scale policy row exists (the scale law's own declaration,
        // admin_config 'workload-scale' — the provisioning processor's
        // manual-mode law, one source of truth). A slim/manual deployment
        // keeps worker failures manual: silent.
        if (!await db.AdminConfig.AsNoTracking().AnyAsync(
                c => c.Resource == Facilitator.Kernel.Provisioning.ProvisioningProcessor.PolicyResource, ct))
        {
            return 0;
        }

        // Candidate law: a live assignment's open attempt AT THE ASSIGNMENT'S
        // CURRENT generation — a re-dispatch already moved the line, so an old
        // generation's leftover attempt is history, not an orphan. (A
        // post-restart straggler event for it fences 422; failing it again
        // would re-run settled work.)
        var candidates = await (
            from attempt in db.ExecutionAttempts.AsNoTracking()
            join assignment in db.Assignments.AsNoTracking() on attempt.AssignmentId equals assignment.AssignmentId
            where attempt.Status == "working"
                && assignment.CurrentExecutionGeneration == attempt.Generation
                && (assignment.Status == "accepted" || assignment.Status == "running" || assignment.Status == "input_required")
            select new { attempt.ExecutionId, attempt.Generation, assignment.AssignmentId, assignment.WorkId })
            .ToListAsync(ct);

        var failed = 0;
        var seen = new HashSet<Guid>();
        foreach (var c in candidates)
        {
            seen.Add(c.ExecutionId);
            var owner = await ExecutionLedger.OwningEndpointAsync(db, c.WorkId, c.AssignmentId, ct);
            if (owner is not { Length: > 0 })
            {
                continue; // no command ever rode to an endpoint (synthetic lane)
            }

            var online = await db.LinkSessions.AsNoTracking()
                .AnyAsync(s => s.LogicalEndpointId == owner && s.LeaseUntil > now, ct);
            if (online)
            {
                _suspects.Remove(c.ExecutionId); // lease alive: not a loss
                continue;
            }

            if (!_suspects.ContainsKey(c.ExecutionId))
            {
                _suspects[c.ExecutionId] = now;
                logger.LogInformation(
                    "session-loss suspect endpoint={Endpoint} execution={ExecutionId} assignment={AssignmentId}",
                    owner, c.ExecutionId, c.AssignmentId);
                continue;
            }

            if (await PostFailedAsync(c.ExecutionId, c.Generation, c.WorkId, c.AssignmentId, now, ct))
            {
                failed++;
            }

            _suspects.Remove(c.ExecutionId);
        }
        // Attempts that left the candidate set (settled, re-dispatched, fenced
        // elsewhere) drop their mark.
        foreach (var stale in _suspects.Keys.Where(id => !seen.Contains(id)).ToArray())
        {
            _suspects.Remove(stale);
        }
        return failed;
    }

    /// <summary>Build, validate, dedupe-insert, and inline-process the
    /// synthesized failed event (the exact path an inbound worker event
    /// rides: envelope validator → inbox unique dedupe → ResultIngress
    /// finalize). Returns false when the row was already there AND already
    /// settled, or when the envelope failed validation.</summary>
    private async Task<bool> PostFailedAsync(
        Guid executionId, int generation, Guid workId, Guid assignmentId, DateTime now, CancellationToken ct)
    {
        var eventId = $"session-loss:{executionId:N}";
        var envelope = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = eventId,
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = FailedType,
            ["subject"] = $"work/{workId}/assignment/{assignmentId:N}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/event.assignment.failed.v1.json",
            ["data"] = new JsonObject
            {
                ["workId"] = workId.ToString(),
                ["assignmentId"] = assignmentId.ToString("N"),
                ["executionId"] = executionId.ToString("N"),
                ["executionGeneration"] = generation,
                ["reason"] = Reason,
                ["retryable"] = true,
            },
        };

        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            // A self-authored envelope that fails the wire law is a kernel bug,
            // not runtime state: log loudly, do not poison the loop.
            logger.LogError("session-loss envelope rejected: {ErrorClass}: {Message}", verdict.ErrorClass, verdict.Message);
            return false;
        }

        var root = JsonDocument.Parse(json).RootElement.Clone();
        var (inboxId, duplicate) = await inbox.InsertInboxAsync(
            Guid.NewGuid(), EnvelopeFactory.KernelSource, eventId, null, json, workId, "pending", null, ct);
        // Run the arm even on a duplicate: a prior pass may have inserted and
        // then crashed before finalizing, and the finalize is idempotent
        // (terminal-at-this-generation lands Duplicate-accepted).
        ResultArm arm;
        using (var scope = scopeFactory.CreateScope())
        {
            arm = await scope.ServiceProvider.GetRequiredService<ResultIngress>().HandleFailedAsync(root, ct);
        }

        await inbox.MarkProcessedAsync(inboxId, arm.Refused ? arm.Status : null, ct);
        if (!duplicate)
        {
            metrics.SessionLoss(Reason);
            logger.LogInformation(
                "session-loss failed posted endpoint lost: work={WorkId} assignment={AssignmentId} execution={ExecutionId} gen={Generation}",
                workId, assignmentId, executionId, generation);
        }

        return !duplicate;
    }
}
