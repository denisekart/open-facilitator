using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The operator's per-instance actions (Phase 8): pause, resume, skip-step,
/// retry-step. Every action is fenced by the instance lock and a status
/// guard (the conditional-update race law: a wrong-state action is a
/// refusal, never a corruption), and each one lands exactly one audit row
/// at the endpoint layer. Cancel (Phase 6) lives beside them in the core
/// file; the four join it here so the action surface reads as one law.
/// </summary>
public sealed partial class WorkflowRunner
{
    /// <summary>Outcome of an operator action: applied, absent, or refused
    /// (with the reason the endpoint maps to a Problem code).</summary>
    public enum InstanceActionResult
    {
        Applied,
        NotFound,
        Refused,
    }

    /// <summary>Refusal detail alongside a <see cref="InstanceActionResult"/>
    /// result, for the audit row and the Problem detail.</summary>
    public sealed record InstanceActionOutcome(InstanceActionResult Result, string Detail);

    /// <summary>Pause: a running or waiting (parked) instance stops taking
    /// steps — the advance loop's status guard refuses it, the sweeper's
    /// deadline pass ignores it, and a wake for a paused park falls through
    /// to <c>on.event</c> like any unresolved park. The park's deadline is
    /// KEPT (resume restores the exact posture, and a stale deadline fires
    /// the moment the run resumes). A shadowed run is not pausable: the
    /// definition's shadow flag already freezes its effects.</summary>
    public Task<InstanceActionOutcome> PauseInstanceAsync(Guid instanceId, CancellationToken ct) =>
        FlipStatusAsync(
            instanceId,
            from: ["running", "waiting"],
            to: "paused",
            keepDeadline: true,
            ct);

    /// <summary>Resume: the mirror of pause — a paused park goes back to
    /// <c>waiting</c> (deadline intact), a paused runner back to
    /// <c>running</c> (and advances: the notify at the tail makes the
    /// dispatcher pick it up exactly like a woken park).</summary>
    public async Task<InstanceActionOutcome> ResumeInstanceAsync(Guid instanceId, CancellationToken ct)
    {
        var toRunning = false;
        var flipped = await FlipWhereAsync(
            instanceId,
            i => i.Status == "paused",
            (i, now) =>
            {
                toRunning = i.DeadlineAt is null;
                i.Status = toRunning ? "running" : "waiting";
                i.UpdatedAt = now;
            },
            ct);
        if (flipped.Result != InstanceActionResult.Applied)
        {
            return flipped;
        }

        if (toRunning)
        {
            // The walk resumes NOW, driven like a wake (each leg its own
            // transaction, the notify riding the leg commits). A resumed
            // PARK just sits in `waiting`: its next wake (or the sweeper,
            // deadline permitting) takes it from there.
            await AdvanceAsync(instanceId, envelope: null, wake: false, ct);
        }

        return flipped;
    }

    /// <summary>Skip-step: the CURRENT leg ends <c>skipped</c> (the step-log
    /// CHECK already admits the status — the checklist shows the operator
    /// moved it) and the run takes the step's COMPLETED edge exactly as a
    /// landing leg would (<see cref="NextStepId"/>, branch null): the edge
    /// law is not forked for operator hands. Works on a running, waiting,
    /// or paused instance (pausing a parked run and skipping its park is
    /// the documented way past a dead approval). Shadow law: a shadowed
    /// run's operator skip records the skip and continues like its other
    /// legs — would-do only.</summary>
    public async Task<InstanceActionOutcome> SkipStepAsync(Guid instanceId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.BeginTransactionAsync(ct);
        await InstanceLockAsync(db, instanceId, ct);
        var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);
        if (instance is null)
        {
            await db.Database.CommitTransactionAsync(ct);
            return new(InstanceActionResult.NotFound, $"no instance '{instanceId}'");
        }

        if (instance.Status is not ("running" or "waiting" or "paused" or "shadowed"))
        {
            await db.Database.CommitTransactionAsync(ct);
            return new(InstanceActionResult.Refused, $"instance is {instance.Status}, nothing to skip");
        }

        var stepId = instance.CurrentStep;
        if (stepId is null)
        {
            await db.Database.CommitTransactionAsync(ct);
            return new(InstanceActionResult.Refused, "the instance sits between legs; retry re-enters one");
        }

        var definition = await LoadDefinitionAsync(db, instance, ct);
        if (definition is null)
        {
            await db.Database.CommitTransactionAsync(ct);
            return new(InstanceActionResult.Refused, "the definition version is gone");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        using var doc = JsonDocument.Parse(definition.Payload);
        var steps = doc.RootElement.GetProperty("steps").EnumerateArray().ToList();

        await db.WorkflowStepLog
            .Where(l => l.InstanceId == instanceId && l.StepId == stepId && l.Status == "running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, "skipped")
                .SetProperty(l => l.UpdatedAt, now), ct);

        var next = NextStepId(steps, stepId, completed: true, decisionBranch: null);
        instance.DeadlineAt = null;
        if (next is null || next == "end")
        {
            await CompleteAsync(db, instance, doc.RootElement, now, ct);
        }
        else
        {
            instance.CurrentStep = next;
            // A paused or parked run keeps its posture until the leg it
            // skipped INTO decides otherwise (a park parks, a shadow run
            // stays would-do); a plain runner just walks.
            if (instance.Status is "running" or "waiting")
            {
                instance.Status = "running";
            }
        }

        instance.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await db.Database.CommitTransactionAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, instanceId.ToString(), ct);
        return new(InstanceActionResult.Applied, $"skipped {stepId}");
    }

    /// <summary>Retry-step: a FAILED run re-enters a leg — the operator
    /// names the step (the failed instance's CurrentStep was cleared by
    /// Terminate; the step log names the leg that fell). The re-run leg
    /// gets a fresh seq, so a fresh step-command id: the outbox law holds,
    /// and the executor's own idempotency (deterministic work/fork ids
    /// where they exist) makes a re-run converge, not duplicate. The
    /// instance returns to <c>running</c> with the named step current and
    /// advances immediately (the same drive a wake does).</summary>
    public async Task<InstanceActionOutcome> RetryStepAsync(Guid instanceId, string stepId, CancellationToken ct)
    {
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            await db.Database.BeginTransactionAsync(ct);
            await InstanceLockAsync(db, instanceId, ct);
            var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);
            if (instance is null)
            {
                await db.Database.CommitTransactionAsync(ct);
                return new(InstanceActionResult.NotFound, $"no instance '{instanceId}'");
            }

            if (instance.Status != "failed")
            {
                await db.Database.CommitTransactionAsync(ct);
                return new(InstanceActionResult.Refused, $"instance is {instance.Status}, only a failed run retries");
            }

            var definition = await LoadDefinitionAsync(db, instance, ct);
            var known = definition is not null && JsonDocument.Parse(definition.Payload).RootElement
                .GetProperty("steps").EnumerateArray()
                .Any(s => string.Equals(s.GetProperty("id").GetString(), stepId, StringComparison.Ordinal));
            if (!known)
            {
                await db.Database.CommitTransactionAsync(ct);
                return new(InstanceActionResult.Refused, $"'{stepId}' is not a step of {instance.DefId}@{instance.DefVersion}");
            }

            instance.Status = "running";
            instance.CurrentStep = stepId;
            instance.DeadlineAt = null;
            instance.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await db.Database.CommitTransactionAsync(ct);
        }

        // Drive it now (not via the notify): the operator stays on the
        // response and the console reloads the checklist the same request
        // cycle. Legs are per-step transactions, so a crash here leaves a
        // consistent failed/parked run, never a half-leg.
        await AdvanceAsync(instanceId, envelope: null, wake: false, ct);
        return new(InstanceActionResult.Applied, $"re-entered {stepId}");
    }

    private Task<InstanceActionOutcome> FlipStatusAsync(
        Guid instanceId, string[] from, string to, bool keepDeadline, CancellationToken ct) =>
        FlipWhereAsync(
            instanceId,
            i => from.Contains(i.Status),
            (i, now) =>
            {
                i.Status = to;
                if (!keepDeadline)
                {
                    i.DeadlineAt = null;
                }

                i.UpdatedAt = now;
            },
            ct);

    private async Task<InstanceActionOutcome> FlipWhereAsync(
        Guid instanceId,
        System.Linq.Expressions.Expression<Func<WorkflowInstanceEntity, bool>> guard,
        Action<WorkflowInstanceEntity, DateTime> mutate,
        CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.BeginTransactionAsync(ct);
        await InstanceLockAsync(db, instanceId, ct);
        var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);
        if (instance is null)
        {
            await db.Database.CommitTransactionAsync(ct);
            return new(InstanceActionResult.NotFound, $"no instance '{instanceId}'");
        }

        // The guard runs on the locked entity: the lock serializes against
        // the sink/runner/sweeper, so no re-read can disagree.
        var passes = guard.Compile()(instance);
        if (!passes)
        {
            await db.Database.CommitTransactionAsync(ct);
            return new(InstanceActionResult.Refused, $"instance is {instance.Status}");
        }

        mutate(instance, timeProvider.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        await db.Database.CommitTransactionAsync(ct);
        return new(InstanceActionResult.Applied, instance.Status);
    }
}
