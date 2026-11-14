using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Notifications;
using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Shared.Storage;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;

using Microsoft.Extensions.DependencyInjection;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The advance loop: one step per transaction, following
/// <see cref="EfCoordinationStore"/>'s pattern — advisory lock on the
/// instance (<c>pg_advisory_xact_lock(hashtextextended(instance,0))</c>),
/// read the pinned definition, execute, write the step-log row + outbox
/// effects + <c>workflow.stage</c> timeline row, commit. Duplicate law: the
/// deterministic instance id is READ BACK before insert (existence =
/// delivered); the PK is the backstop only.
/// Step law (Phase 1 scope): <c>emit</c> (verbatim wire command, or a
/// capability routing intent — the <c>RoutingIntent</c> law verbatim),
/// <c>select</c> (pure projection), <c>decide</c> (guard = deterministic
/// predicate; model = <see cref="IDecisionGateway"/> over a closed option
/// set, degrade-never-fail to <c>fallback</c>), <c>wait</c> /
/// <c>approval</c> / <c>timer</c> (park), <c>escalate</c> (notification
/// ledger). <c>fork-work</c> executes with the shared WorkFork law in
/// Phase 3; <c>work-create</c>, <c>memory</c> and <c>invoke</c> are
/// enable-time-refused until Phases 5/6 (they reach here only from a
/// <see cref="WorkflowDefinitionValidator.ExecutableKinds"/> gap).
/// </summary>
public sealed partial class WorkflowRunner(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    TimeProvider timeProvider,
    EnvelopeValidator envelopeValidator,
    IEnumerable<IDecisionGateway> decisionGateways,
    NotificationLedger notifications,
    Facilitator.Kernel.Integrations.ProviderRegistry providers,
    IServiceScopeFactory scopeFactory)
{
    /// <summary>Timeline actor of every plane row.</summary>
    public const string Actor = "workflow:kernel";

    private const int MaxAdvanceSteps = 64;

    [GeneratedRegex(@"^P(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+(?:\.\d+)?)S)?)?$")]
    private static partial Regex DurationPattern();

    /// <summary>Start (or idempotently read back) the instance a trigger
    /// names, then advance it to park-or-terminal. Returns the
    /// deterministic instance id.</summary>
    public async Task<Guid> StartOrResumeAsync(WorkflowTrigger trigger, CancellationToken ct)
    {
        var instanceId = WorkflowIdentity.InstanceIdFor(trigger.DefinitionId, trigger.DefinitionVersion, trigger.TriggerRowId);

        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            await db.Database.BeginTransactionAsync(ct);
            await InstanceLockAsync(db, instanceId, ct);
            var exists = await db.WorkflowInstances.AsNoTracking().AnyAsync(i => i.InstanceId == instanceId, ct);
            if (!exists)
            {
                var definition = await db.WorkflowDefinitions.AsNoTracking()
                    .FirstOrDefaultAsync(d => d.DefId == trigger.DefinitionId && d.Version == trigger.DefinitionVersion, ct);
                if (definition is not { Enabled: true })
                {
                    // Definition removed/disabled between emit and delivery:
                    // consume the row (a disabled plane must not DLQ).
                    return instanceId;
                }

                var now = timeProvider.GetUtcNow().UtcDateTime;
                // Stamp law: the row's subject stamp (the emitter resolved it
                // when the event was accepted, the acting arm may have
                // refined it) outranks any re-lookup — a live binding may
                // have moved since (the review fork repoints it).
                var (work0, projectId, teamSlug) = ResolveWorkContext(db, trigger.Envelope);
                var primaryWork = trigger.SubjectWork ?? work0;
                db.WorkflowInstances.Add(new WorkflowInstanceEntity
                {
                    InstanceId = instanceId,
                    DefId = trigger.DefinitionId,
                    DefVersion = trigger.DefinitionVersion,
                    Team = teamSlug,
                    ProjectId = projectId,
                    PrimaryWorkId = primaryWork,
                    Status = definition.Shadow ? "shadowed" : "running",
                    StartedAt = now,
                    UpdatedAt = now,
                    Depth = 0,
                    InvocationPath = $"{trigger.DefinitionId}@{trigger.DefinitionVersion}",
                });
                db.Audit.Add(new AuditEventEntity
                {
                    AuditId = Guid.NewGuid(),
                    OccurredAt = now,
                    Actor = Actor,
                    Action = "workflow.started",
                    Resource = $"workflow/{instanceId}",
                    Decision = "allow",
                    Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
                    {
                        ["defId"] = trigger.DefinitionId,
                        ["version"] = trigger.DefinitionVersion.ToString(),
                        ["trigger"] = trigger.TriggerRowId.ToString("N"),
                    }),
                });
                await db.SaveChangesAsync(ct);
                await db.Database.CommitTransactionAsync(ct);
            }
        }

        await AdvanceAsync(instanceId, trigger.Envelope, wake: false, ct);
        return instanceId;
    }

    /// <summary>Resume a parked instance from a wake envelope, then advance.
    /// The conditional UPDATE is the race law: exactly one of
    /// {wake, sweeper timeout, cancel} flips the park; losers no-op.
    /// Returns TRUE only when THIS call flipped the park; false means no
    /// park was found (resolved or never existed) — the sink's
    /// fall-through signal.</summary>
    public async Task<bool> ResumeAsync(WorkflowWake wake, CancellationToken ct)
    {
        // A completion hand-off names the CHILD (its terminal row is the
        // commit proof, its id the idempotency seed), but the PARK lives on
        // the PARENT's invoke step: resolve the parent whose parked invoke
        // step names this child. A root/detached child's event (or a child
        // whose parent never parked on it) resolves to nothing — the sink's
        // fall-through law takes the envelope to on.event.
        var parked = wake.CompletionStatus is null
            ? wake.InstanceId
            : await ParkedParentAsync(wake, ct);
        if (parked is null)
        {
            return false;
        }

        wake = wake with { InstanceId = parked.Value };
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            await db.Database.BeginTransactionAsync(ct);
            await InstanceLockAsync(db, wake.InstanceId, ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var flipped = await db.WorkflowInstances
                // A park is waiting-with-a-deadline, or shadowed-with-a-
                // deadline (a TERMINATED shadow run carries no deadline, so
                // a late wake finds no park and no-ops).
                .Where(i => i.InstanceId == wake.InstanceId
                            && i.DeadlineAt != null
                            && (i.Status == "waiting" || i.Status == "shadowed"))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, i => i.Status == "shadowed" ? "shadowed" : "running")
                    .SetProperty(i => i.DeadlineAt, (DateTime?)null)
                    .SetProperty(i => i.UpdatedAt, now), ct);
            if (flipped == 0)
            {
                // Park already resolved (a cancel or duplicate wake raced)
                // or never existed: NOT resumed — the sink applies the
                // fall-through law (offer the envelope to on.event).
                await db.Database.CommitTransactionAsync(ct);
                return false;
            }

            await db.Database.CommitTransactionAsync(ct);
        }

        await AdvanceAsync(wake.InstanceId, wake.Envelope, wake: true, ct);
        return true;
    }

    /// <summary>Drive an instance step by step until it parks, terminates,
    /// or the iteration budget (a graph-pathology guard) is reached. Each
    /// leg is its own transaction. The delivery's envelope is the run
    /// context for EVERY leg of this advance (step templates read
    /// <c>$event.*</c>); a leg reached after a restart carries the wake
    /// envelope instead — the event that actually resumed the flow.</summary>
    public async Task AdvanceAsync(Guid instanceId, JsonElement? envelope, bool wake, CancellationToken ct)
    {
        for (var i = 0; i < MaxAdvanceSteps; i++)
        {
            if (!await AdvanceOnceAsync(instanceId, envelope, wake && i == 0, ct))
            {
                return;
            }
        }
    }

    /// <summary>One step transaction. True = keep advancing; false = parked
    /// or terminal (or the instance vanished).</summary>
    private async Task<bool> AdvanceOnceAsync(Guid instanceId, JsonElement? envelope, bool wake, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.BeginTransactionAsync(ct);
        await InstanceLockAsync(db, instanceId, ct);

        var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);

        // 'shadowed' advances exactly like 'running': the flag is carried by
        // the DEFINITION, the step executors decide what to write (would-do
        // records only), so the loop law is identical.
        if (instance is null || instance.Status is not ("running" or "waiting" or "shadowed"))
        {
            await db.Database.CommitTransactionAsync(ct);
            return false;
        }

        var definition = await LoadDefinitionAsync(db, instance, ct);
        if (definition is null)
        {
            Terminate(db, instance, "failed", null, timeProvider.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            await db.Database.CommitTransactionAsync(ct);
            return false;
        }

        using var doc = JsonDocument.Parse(definition.Payload);
        var steps = doc.RootElement.GetProperty("steps").EnumerateArray().ToList();
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Where we are: the entry leg (the first step — schema law: exactly
        // one, its `on` absent or "always"), or the current pointer.
        var current = instance.CurrentStep is null
            ? steps[0]
            : steps.FirstOrDefault(s => s.GetProperty("id").GetString() == instance.CurrentStep);
        if (current.ValueKind != JsonValueKind.Object)
        {
            await CompleteAsync(db, instance, doc.RootElement, now, ct);
            await db.SaveChangesAsync(ct);
            await db.Database.CommitTransactionAsync(ct);
            await PostgresNotifications.NotifyOutboxAsync(db, instanceId.ToString(), ct);
            return false;
        }

        var stepId = current.GetProperty("id").GetString()!;
        var (kind, payload) = WorkflowDefinitionValidator.Payload(current).First();
        var seq = await NextSeqAsync(db, instanceId, ct);
        var commandId = WorkflowIdentity.StepCommandId(instance.DefId, instance.DefVersion, instanceId, stepId, seq);
        var context = BuildContext(db, instance, envelope);

        using var activity = KernelActivity.Source.StartActivity($"workflow.step.{kind}", ActivityKind.Internal);
        activity?.SetTag("ofac.workflow.instance", instanceId.ToString());
        activity?.SetTag("ofac.workflow.definition", $"{instance.DefId}@{instance.DefVersion}");
        activity?.SetTag("ofac.workflow.step", stepId);
        if (instance.ParentInstanceId is { } parentSpan)
        {
            activity?.SetTag("ofac.workflow.parent", parentSpan.ToString());
        }

        // A wake RESOLVES the parked step: it does not re-execute it. An
        // approval decision of anything but `approved` (denied, expired,
        // revoked) takes the FAILED edge; any other wake (a matching wait
        // event, an approved decision) continues along the completed edge.
        var shadowMode = definition.Shadow;
        var outcome = wake ? WakeOutcome(envelope!.Value) : await ExecuteStepKindAsync();

        StepOutcome UnexecutableStep() => StepOutcome.LegFailed(
            new JsonObject { ["error"] = $"step kind '{kind}' is not executable by this interpreter phase" });

        // The shadow law reaches into the steps: an executor whose effect
        // would write computes it and reports the would-do, but writes
        // nothing (no outbox, no timeline, no approval row).
        Task<StepOutcome> ExecuteStepKindAsync() => kind switch
        {
            "select" => ExecuteSelectAsync(payload, context, ct),
            "fan-out" => ExecuteFanOutAsync(db, instance, payload, context, commandId, now, ct, shadowMode),
            "emit" => ExecuteEmitAsync(db, instance, payload, context, commandId, now, ct, shadowMode),
            "decide" => ExecuteDecideAsync(payload, context, ct),
            "wait" => Task.FromResult(ParkOutcome(context, payload.GetProperty("correlate"), "wait",
                now + ParseDuration(payload.GetProperty("timeout").GetString()!))),
            "timer" => Task.FromResult(new StepOutcome(OutcomeKind.Parked,
                new JsonObject { ["parked"] = "timer" }, null, null,
                Deadline: now + ParseDuration(payload.GetProperty("for").GetString()!))),
            "approval" => ExecuteApprovalAsync(db, instance, stepId, payload, context, now, ct, shadowMode),
            "escalate" => ExecuteEscalateAsync(db, instance, payload, context, ct, shadowMode),
            "work-create" => ExecuteWorkCreateAsync(db, instance, stepId, payload, context, envelope, now, ct, shadowMode),
            "fork-work" => ExecuteForkAsync(db, instance, stepId, payload, context, envelope, now, ct, shadowMode),
            "invoke" => ExecuteInvokeAsync(db, instance, stepId, payload, context, now, ct, shadowMode),
            "signal" => ExecuteSignalAsync(db, instance, stepId, payload, context, commandId, now, ct, shadowMode),
            _ => Task.FromResult(UnexecutableStep()),
        };


        // A wake closes the parked leg (status done) before its successor
        // leg is written; the timeout path does the same in the sweeper.
        if (wake)
        {
            await db.WorkflowStepLog
                .Where(l => l.InstanceId == instanceId && l.StepId == stepId && l.Status == "running")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.Status, "done")
                    .SetProperty(l => l.UpdatedAt, now), ct);
        }

        // Shadow law (the strangler flag): would-do only — a step-log row
        // with status 'shadow', no effect command id, no outbox rows, no
        // timeline rows (the arm owns the real behavior).
        var shadow = shadowMode;
        db.WorkflowStepLog.Add(new WorkflowStepLogEntity
        {
            InstanceId = instanceId,
            Seq = seq,
            StepId = stepId,
            Status = shadow ? "shadow" : outcome.Kind switch
            {
                OutcomeKind.Parked => "running",
                OutcomeKind.LegFailed => "failed",
                _ => "done",
            },
            EffectCommandId = shadow ? null : outcome.EffectCommandId,
            EvidenceRef = $"kernel:workflow:{instanceId:N}",
            Output = outcome.Output.ToJsonString(),
            StartedAt = now,
            UpdatedAt = now,
        });

        if (!shadow && outcome.TimelineNote is { } note && instance.PrimaryWorkId is { } stageWork)
        {
            AppendStageTimeline(db, stageWork, instanceId, stepId, note);
        }

        // Instance transition.
        instance.UpdatedAt = now;
        var keepGoing = false;

        // A step reached by the bare `failed` selector is the flow's
        // failure leg: it cleans up, but the run it cleans up after
        // failed — reaching a terminal state through it records `failed`
        // (the story lives in the step log; end_reason is the
        // composition vocabulary of the CHECK).
        var onFailureLeg = SelectorOf(current) == "failed";
        switch (outcome.Kind)
        {
            case OutcomeKind.Parked:
                instance.Status = shadow ? "shadowed" : "waiting";
                instance.CurrentStep = stepId;
                instance.DeadlineAt = shadow ? null : outcome.Deadline;
                break;
            case OutcomeKind.Continue:
                var next = NextStepId(steps, stepId, completed: true, outcome.Branch);
                if (next is null || next == "end")
                {
                    if (onFailureLeg)
                    {
                        await ProjectOutputAsync(db, instance, doc.RootElement, now, ct);
                        Terminate(db, instance, "failed", null, now);
                    }
                    else
                    {
                        await CompleteAsync(db, instance, doc.RootElement, now, ct);
                    }
                }
                else
                {
                    instance.CurrentStep = next;
                    keepGoing = true;
                }

                break;
            case OutcomeKind.LegFailed:
                var failureEdge = FailedEdgeId(steps, stepId);
                if (failureEdge is null or "end")
                {
                    // end_reason stays the composition vocabulary of the
                    // CHECK: a plain leg failure records none (the step log
                    // carries the story); an executor-set composition
                    // reason (preemption, invoke-timeout) survives Terminate.
                    Terminate(db, instance, "failed", null, now);
                }
                else
                {
                    instance.CurrentStep = failureEdge;
                    // The leg ran; the flow still ends failed — the step log rows
                    // carry which leg and why, end_reason stays the composition
                    // vocabulary of the CHECK constraint.
                    keepGoing = true;
                }

                break;
            default:
                Terminate(db, instance, "failed", null, now);
                break;
        }

        if (keepGoing && steps.Any(s => s.GetProperty("id").GetString() == instance.CurrentStep))
        {
            // Enter leg of the successor: a park may flip status inside its
            // executor, so re-read. (A shadowed run continues the same way.)
            keepGoing = instance.Status is "running" or "shadowed";
        }
        else
        {
            keepGoing = false;
        }

        await db.SaveChangesAsync(ct);
        await db.Database.CommitTransactionAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, instanceId.ToString(), ct);
        return keepGoing;
    }

    /// <summary>The sweeper's deadline pass: every waiting instance past its
    /// deadline closes its parked leg as timed out and continues — a
    /// <c>timer</c> completes, a <c>wait</c>/<c>approval</c> takes its
    /// timeout/failure edge. Returns the count fired.</summary>
    public async Task<int> SweepDeadlinesAsync(CancellationToken ct)
    {
        List<Guid> due;
        await using (var scan = await contextFactory.CreateDbContextAsync(ct))
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            due = await scan.WorkflowInstances
                .Where(i => i.Status == "waiting" && i.DeadlineAt != null && i.DeadlineAt <= now)
                .Select(i => i.InstanceId)
                .ToListAsync(ct);
        }

        var fired = 0;
        foreach (var instanceId in due)
        {
            if (await TimeoutLegAsync(instanceId, ct))
            {
                await AdvanceAsync(instanceId, envelope: null, wake: false, ct);
                fired++;
            }
        }

        return fired;
    }

    /// <summary>Close a due park. The conditional UPDATE is the race law: a
    /// wake that landed first wins, the timeout no-ops.</summary>
    private async Task<bool> TimeoutLegAsync(Guid instanceId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.BeginTransactionAsync(ct);
        await InstanceLockAsync(db, instanceId, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);
        if (instance is null || instance.Status != "waiting" || instance.DeadlineAt > now)
        {
            await db.Database.CommitTransactionAsync(ct);
            return false;
        }

        var definition = await LoadDefinitionAsync(db, instance, ct);
        var stepId = instance.CurrentStep ?? string.Empty;
        var steps = new List<JsonElement>();
        JsonElement step = default;
        if (definition is not null)
        {
            // Clone: the parsed document dies with this block, the step
            // elements live to the branch selection below.
            using var doc = JsonDocument.Parse(definition.Payload);
            var root = doc.RootElement.Clone();
            steps.AddRange(root.GetProperty("steps").EnumerateArray());
            step = steps.FirstOrDefault(s => s.GetProperty("id").GetString() == stepId);
        }

        await db.WorkflowStepLog
            .Where(l => l.InstanceId == instanceId && l.StepId == stepId && l.Status == "running")
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, "done")
                .SetProperty(l => l.UpdatedAt, now), ct);

        var seq = await NextSeqAsync(db, instanceId, ct);
        var timedOut = step.ValueKind == JsonValueKind.Object;
        var isTimer = timedOut && step.TryGetProperty("timer", out _);
        db.WorkflowStepLog.Add(new WorkflowStepLogEntity
        {
            InstanceId = instanceId,
            Seq = seq,
            StepId = stepId,
            Status = "failed",
            Output = new JsonObject { ["timeout"] = true }.ToJsonString(),
            StartedAt = now,
            UpdatedAt = now,
        });

        string? target = null;
        if (isTimer)
        {
            target = NextStepId(steps, stepId, completed: true, null);
        }
        else if (timedOut && step.TryGetProperty("wait", out var wait))
        {
            target = wait.GetProperty("onTimeout").GetString();
            if (target is { Length: > 0 } and not "end" && !steps.Any(s => s.GetProperty("id").GetString() == target))
            {
                target = null; // dangling edge: the park ends as a failure
            }
        }
        else if (timedOut && step.TryGetProperty("invoke", out var invoke))
        {
            // Guard 4 (age + death): the awaited child's subtree is
            // cancelled, the onTimeout edge (or the failed edge) is taken,
            // and the run records `invoke-timeout` — the chain never waits
            // forever on a child that stopped making progress.
            target = invoke.TryGetProperty("onTimeout", out var ot) ? ot.GetString() : null;
            if (target is { Length: > 0 } and not "end" && !steps.Any(s => s.GetProperty("id").GetString() == target))
            {
                target = null;
            }

            target ??= FailedEdgeId(steps, stepId);
            instance.EndReason ??= "invoke-timeout";
            CancelSubtreeCore(db, instanceId, now);
        }
        else if (timedOut)
        {
            target = FailedEdgeId(steps, stepId); // approval expiry: the failed edge
        }

        instance.UpdatedAt = now;
        instance.DeadlineAt = null;
        if (target is null or { Length: 0 } or "end")
        {
            if (isTimer)
            {
                // A completing timer is a completion: the output contract
                // rides the terminal transition (same as CompleteAsync).
                using var rootDoc = JsonDocument.Parse(definition!.Payload);
                await ProjectOutputAsync(db, instance, rootDoc.RootElement, now, ct);
            }

            Terminate(db, instance, isTimer ? "completed" : "failed", null, now);
        }
        else
        {
            instance.Status = "running";
            instance.CurrentStep = target;
            if (timedOut && !isTimer && !step.TryGetProperty("wait", out _))
            {
                // Approval expiry routes to the failure leg: the leg runs,
                // but the flow still ends failed — an unanswered consent is
                // a human silence, never a success.
                // end_reason is the composition vocabulary (the CHECK);
                // the timeout story lives in the step log's output.
            }
        }

        await db.SaveChangesAsync(ct);
        await db.Database.CommitTransactionAsync(ct);
        return true;
    }

    // ---- step executors ------------------------------------------------------

    private async Task<StepOutcome> ExecuteEmitAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        JsonElement payload,
        JsonObject context,
        string commandId,
        DateTime now,
        CancellationToken ct,
        bool shadow = false)
    {
        var inputs = new JsonObject();
        if (payload.TryGetProperty("inputs", out var declared) && declared.ValueKind == JsonValueKind.Object)
        {
            foreach (var input in declared.EnumerateObject())
            {
                inputs[input.Name] = ResolveTemplate(JsonNode.Parse(input.Value.GetRawText()), context);
            }
        }

        if (payload.TryGetProperty("capability", out var capability))
        {
            // Capability dispatch = a routing intent from the subject work
            // (the RoutingIntent law verbatim). Workless runs cannot route.
            if (instance.PrimaryWorkId is not { } routedWork)
            {
                return StepOutcome.LegFailed(new JsonObject { ["error"] = "capability emit needs a subject work" });
            }

            var work = await db.Works.AsNoTracking()
                .Where(w => w.WorkId == routedWork)
                .Select(w => new { w.Objective, Team = w.Team != null ? w.Team.Slug : null })
                .FirstOrDefaultAsync(ct);
            if (work is null)
            {
                return StepOutcome.LegFailed(new JsonObject { ["error"] = "subject work vanished" });
            }

            // Effect identity: everything the step-command id carries past
            // the instance segment (`<stepId>:<n>[:<element>]`) — a fan-out
            // leg's per-element suffix rides the effect key verbatim.
            var keySegments = commandId.Split(':');
            var effectKey = string.Join(':', keySegments[3..]);
            var intent = new RoutingIntent(
                routedWork,
                capability.GetString()!,
                work.Team,
                PreferenceEndpointId: null,
                TargetId: ResolveString(payload.TryGetProperty("to", out var toRef) ? toRef : default, context)
                           ?? $"{instance.DefId}-flow",
                CommandIdBase: $"wf-{instance.InstanceId:N}",
                EffectKey: effectKey,
                Objective: work.Objective,
                SourceRefsJson: null,
                ExtraInputsJson: inputs.Count == 0 ? null : inputs.ToJsonString(),
                ActorEvidenceRef: $"kernel:workflow:{instance.InstanceId:N}");
            var intentRow = RoutingIntentEnvelope.OutboxRow(intent, now, envelopeValidator);
            if (Activity.Current is { } intentSpan)
            {
                // Same fresh-side stamp as the command leg (an accepted
                // envelope extension — the dispatcher re-stamps link rows
                // the same way post-validation).
                var stamped = JsonNode.Parse(intentRow.Envelope)!.AsObject();
                stamped["traceparent"] = intentSpan.Id;
                intentRow.Envelope = stamped.ToJsonString();
            }

            if (shadow)
            {
                db.Entry(intentRow).State = EntityState.Detached;
                return StepOutcome.Done(new JsonObject
                {
                    ["wouldDo"] = "routing-intent",
                    ["commandId"] = intent.EffectCommandId,
                    ["capability"] = capability.GetString(),
                }, null);
            }

            db.Outbox.Add(intentRow);
            Activity.Current?.SetTag("ofac.outbox.id", intentRow.OutboxId.ToString());
            Activity.Current?.SetTag("ofac.outbox.command_id", intentRow.CommandId);
            Activity.Current?.SetTag("ofac.workflow.correlation_id", instance.InstanceId.ToString("N"));
            var marker = await ParkBindingAsync(db, instance, payload, context, now, ct);
            return StepOutcome.Done(
                new JsonObject { ["effect"] = "routing-intent", ["commandId"] = intent.EffectCommandId, ["binding"] = marker },
                commandId,
                note: $"flow effect: routing intent {capability.GetString()}");
        }

        var commandType = payload.GetProperty("command").GetString()!;
        var data = new JsonObject
        {
            ["commandId"] = commandId,
            ["idempotencyKey"] = commandId,
        };
        foreach (var input in inputs)
        {
            data[input.Key] = input.Value?.DeepClone();
        }

        if (WorkScopedCommands.Contains(commandType))
        {
            if (instance.PrimaryWorkId is not { } scopedWork)
            {
                return StepOutcome.LegFailed(new JsonObject { ["error"] = $"{commandType} needs a subject work" });
            }

            data["workId"] = scopedWork.ToString();
        }

        if (TargetScopedCommands.Contains(commandType))
        {
            // data.target: the `to` ref, or the trigger envelope's own
            // conversation (a flow started from a thread answers there).
            // Only the conversationTarget keys survive — the wire schema is
            // closed.
            JsonNode? target = payload.TryGetProperty("to", out var toRef)
                ? ResolveTemplate(JsonNode.Parse(toRef.GetRawText()), context)
                : ConversationTargetFromEvent(context);
            data["target"] = ProjectTarget(target, commandType);
            if (!data.ContainsKey("text"))
            {
                data["text"] = "workflow step";
            }
        }

        if (commandType.StartsWith("org.facilitator.command.work.", StringComparison.Ordinal))
        {
            // Required work-command fields the flow did not declare get
            // kernel defaults (the plane is the actor; a stalled-clock
            // deadline a day out, the instance names the assignment leg).
            if (!data.ContainsKey("targetId"))
            {
                data["targetId"] = instance.InstanceId.ToString("N");
            }

            if (!data.ContainsKey("actorEvidenceRef"))
            {
                data["actorEvidenceRef"] = $"kernel:workflow:{instance.InstanceId:N}";
            }

            if (!data.ContainsKey("deadline"))
            {
                data["deadline"] = now.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
            }

            if (!data.ContainsKey("assignmentId"))
            {
                data["assignmentId"] = instance.InstanceId.ToString("N");
            }
        }

        var wireEnvelope = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = commandType,
            ["subject"] = instance.PrimaryWorkId is { } w ? $"work/{w}" : $"workflow/{instance.InstanceId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = $"https://schemas.openfacilitator.org/v1/{commandType["org.facilitator.".Length..]}.json",
            ["data"] = data,
        };

        // Fresh-side law (the dispatcher's own): the creating trace is
        // stamped into the envelope so the dispatch span continues THIS
        // trace — the instance correlation is read out of the outbox row.
        if (Activity.Current is { } stepSpan && !wireEnvelope.ContainsKey("traceparent"))
        {
            wireEnvelope["traceparent"] = stepSpan.Id;
        }

        var json = wireEnvelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            // Fail closed as a leg failure: an invalid emission fails the
            // step (visible in the step log), never the outbox.
            return StepOutcome.LegFailed(new JsonObject { ["error"] = $"envelope rejected: {verdict.ErrorClass}: {verdict.Message}" });
        }

        var effectRow = new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            CommandId = commandId,
            RecipientEndpointId = RecipientFor(commandType, data),
            AggregateType = "work",
            AggregateId = instance.PrimaryWorkId,
            EventType = commandType,
            Envelope = json,
            WorkId = instance.PrimaryWorkId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        };
        if (shadow)
        {
            // Would-do record: the exact envelope the swap will append,
            // digested (the reconciliation report compares digests, so a
            // byte-level plan match is provable, not hand-waved).
            db.Entry(effectRow).State = EntityState.Detached;
            var digest = "sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            return StepOutcome.Done(new JsonObject
            {
                ["wouldDo"] = "command",
                ["commandId"] = commandId,
                ["commandType"] = commandType,
                ["envelopeDigest"] = digest,
            }, null);
        }

        db.Outbox.Add(effectRow);

        // Trace-correlation law: the effect's identity rides the step span
        // (and the dispatcher's child continues the trace through the row's
        // W3C context), so a timeline row points at the effect that caused
        // it without a join.
        Activity.Current?.SetTag("ofac.outbox.id", effectRow.OutboxId.ToString());
        Activity.Current?.SetTag("ofac.outbox.command_id", commandId);
        Activity.Current?.SetTag("ofac.workflow.correlation_id", instance.InstanceId.ToString("N"));
        return StepOutcome.Done(
            new JsonObject { ["effect"] = "command", ["commandId"] = commandId },
            commandId,
            note: $"flow effect: {commandType}");
    }

    private async Task<StepOutcome> ExecuteDecideAsync(JsonElement payload, JsonObject context, CancellationToken ct)
    {
        _ = ct;
        var source = payload.GetProperty("source").GetString();
        var fallback = payload.GetProperty("fallback").GetString()!;
        var document = JsonSerializer.SerializeToElement(context);

        if (source == "guard")
        {
            var guard = payload.GetProperty("guard");
            var field = guard.GetProperty("field").GetString()!;
            var actual = WorkflowMatcher.ResolvePath(document, field);
            var hit = guard.TryGetProperty("present", out var present)
                ? present.GetBoolean()
                    == (actual is not null && actual.Value.ValueKind != JsonValueKind.Null)
                : guard.TryGetProperty("equals", out var equals)
                    ? equals.ValueKind == JsonValueKind.Null
                        ? actual is null || actual.Value.ValueKind == JsonValueKind.Null
                        : actual is not null && WorkflowMatcher.Matches(equals, actual)
                    : actual is not null
                        && guard.GetProperty("in").EnumerateArray().Any(term => WorkflowMatcher.Matches(term, actual));
            // A HIT has no declared branch: the guard says only whether the
            // flow continues, so the completed-edge law chooses the successor
            // (the array-adjacent chain or an explicit `<stepId>:completed`
            // selector). A MISS takes the declared fallback.
            var output = new JsonObject { ["guard"] = field, ["hit"] = hit };
            return hit
                ? StepOutcome.Continue(output)
                : StepOutcome.WithBranch(StepOutcome.Continue(output), fallback);
        }

        // Model leg: closed options through the decision gateway; low
        // confidence or outage = the declared fallback (the RoutingSink
        // degrade law — a flow never fails on AI).
        var question = payload.GetProperty("question");
        var options = question.GetProperty("options").EnumerateArray()
            .Select(o => (Value: o.GetProperty("value").GetString()!, Branch: o.GetProperty("branch").GetString()!))
            .ToList();
        var gateway = decisionGateways.FirstOrDefault();
        if (gateway is null)
        {
            return StepOutcome.WithBranch(
                StepOutcome.Continue(new JsonObject { ["decision"] = "fallback", ["why"] = "no decision gateway" }), fallback);
        }

        try
        {
            var answers = await gateway.DecideAsync(
                new DecisionRequest("decision", DescribeContext(context),
                    [new ChoiceQuestion("branch", question.GetProperty("prompt").GetString() ?? "branch", options.Select(o => o.Value).ToList())]),
                ct);
            var answer = answers.FirstOrDefault(a => a.Name == "branch");
            if (answer?.Choice is { Length: > 0 } choice && answer.Confidence >= 0.5)
            {
                var picked = options.FirstOrDefault(o => o.Value == choice);
                if (picked.Branch is { Length: > 0 })
                {
                    return StepOutcome.WithBranch(
                        StepOutcome.Continue(new JsonObject { ["decision"] = choice }), picked.Branch);
                }
            }

            return StepOutcome.WithBranch(
                StepOutcome.Continue(new JsonObject { ["decision"] = "fallback", ["why"] = "low confidence or refusal" }), fallback);
        }
        catch (AiUnavailableException)
        {
            return StepOutcome.WithBranch(
                StepOutcome.Continue(new JsonObject { ["decision"] = "fallback", ["why"] = "ai-unavailable" }), fallback);
        }
    }

    /// <summary>The `fork-work` step: the terminal-input law through
    /// <see cref="Facilitator.Kernel.Work.WorkFork"/> — deterministic
    /// successor identity, the arm's sentences, the binding following the
    /// live item. Shadow mode computes the full plan and records it in the
    /// step output (the reconciliation source), writing nothing.</summary>
    /// <summary>
    /// The work-create step: the shared <see cref="WorkCreate"/> law with
    /// the provider arm's labeled-issue branch, executed by the plane. The
    /// subject work id is deterministic (instance, stepId) — a redelivered
    /// trigger cannot mint a second work. Conversation identity, the
    /// binding's idempotency key, the origin and the owner all derive from
    /// the TRIGGER ENVELOPE (the residence is as observed, never inferred);
    /// team resolution follows the arm's fail-open law (a declared slug
    /// must be rostered, else the instance's team, else workless-team).
    /// The step STAMPS the instance's subject work: the leg after it (the
    /// ack emit) runs with a subject, like the arm's inline ack.
    /// </summary>
    private async Task<StepOutcome> ExecuteWorkCreateAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        string stepId,
        JsonElement payload,
        JsonObject context,
        JsonElement? envelope,
        DateTime now,
        CancellationToken ct,
        bool shadow)
    {
        if (envelope is not { } trigger
            || !trigger.TryGetProperty("data", out var triggerData)
            || triggerData.ValueKind != JsonValueKind.Object
            || !triggerData.TryGetProperty("reference", out var reference)
            || reference.ValueKind != JsonValueKind.Object)
        {
            return StepOutcome.LegFailed(new JsonObject { ["error"] = "work-create needs a trigger event with a reference" });
        }

        var provider = reference.GetProperty("provider").GetString()!;
        var channel = reference.GetProperty("channelOrResourceId").GetString()!;
        var root = reference.GetProperty("rootThreadId").GetString() ?? channel;
        var kind = reference.TryGetProperty("conversationKind", out var k) ? k.GetString() ?? "issue" : "issue";
        var messageId = reference.TryGetProperty("messageId", out var m) ? m.GetString() : null;
        var data = triggerData;
        var deliveryId = data.TryGetProperty("providerDeliveryId", out var d) ? d.GetString() ?? instance.InstanceId.ToString("N") : instance.InstanceId.ToString("N");
        var ownerActor = data.TryGetProperty("actorProviderAccountId", out var acct) && acct.ValueKind == JsonValueKind.String
            ? $"provider:{provider}:{acct.GetString()}"
            : null;

        // Objective: the event's own text (the arm's posture), the
        // reference-shaped fallback when it carries none.
        var objectiveText = ResolveString(payload.GetProperty("objectiveRef"), context);
        var objective = WorkFork.Trim(string.IsNullOrEmpty(objectiveText) ? $"Issue {channel}#{root}" : objectiveText!);

        // Team law (arm's fail-open, anchored): a declared slug wins when
        // rostered; else the provider adapter's own enrolled team (the
        // plane's honest mirror of the arm's `workload.TeamId` — the
        // adapter that posted this event is enrolled, and enrollments are
        // unique per endpoint); else the instance's team; else teamless.
        string? teamSlug = null;
        Guid? teamId = null;
        if (payload.TryGetProperty("team", out var declaredTeam)
            && await db.Teams.AsNoTracking().Where(t => t.Slug == declaredTeam.GetString())
                    .Select(t => (Guid?)t.TeamId).FirstOrDefaultAsync(ct) is { } rostered)
        {
            teamSlug = declaredTeam.GetString();
            teamId = rostered;
        }
        else if (providers.Find(provider)?.EndpointId is { Length: > 0 } adapterEndpoint
            && await db.LinkEnrollments.AsNoTracking()
                .Where(e => e.EndpointId == adapterEndpoint)
                .Select(e => (Guid?)e.TeamId).FirstOrDefaultAsync(ct) is { } enrolled)
        {
            teamId = enrolled;
            teamSlug = await db.Teams.AsNoTracking().Where(t => t.TeamId == enrolled)
                .Select(t => t.Slug).FirstOrDefaultAsync(ct);
        }
        else if (instance.Team is { Length: > 0 } instanceTeam)
        {
            teamSlug = instanceTeam;
            teamId = await db.Teams.AsNoTracking().Where(t => t.Slug == instanceTeam)
                .Select(t => (Guid?)t.TeamId).FirstOrDefaultAsync(ct);
        }

        var capability = payload.GetProperty("capability").GetString()!;
        var commandBase = payload.TryGetProperty("commandBase", out var cb)
            ? cb.GetString()!
            : $"wf-{instance.InstanceId:N}";
        var extraInputs = new JsonObject();
        if (payload.TryGetProperty("inputs", out var declared) && declared.ValueKind == JsonValueKind.Object)
        {
            foreach (var input in declared.EnumerateObject())
            {
                extraInputs[input.Name] = ResolveTemplate(JsonNode.Parse(input.Value.GetRawText()), context);
            }
        }

        var workId = WorkflowIdentity.WorkCreateWorkId(instance.InstanceId, stepId);
        var plan = new WorkCreate.Plan(
            workId,
            objective,
            teamId,
            teamSlug,
            Origin: trigger.GetProperty("type").GetString(),
            ownerActor,
            provider,
            channel,
            root,
            kind,
            messageId,
            deliveryId,
            capability,
            PreferenceEndpointId: payload.TryGetProperty("preference", out var pref) ? pref.GetString() : null,
            TargetId: payload.TryGetProperty("target", out var targetRef)
                ? targetRef.GetString()!
                : $"{instance.DefId}-flow",
            commandBase,
            extraInputs.Count == 0 ? null : extraInputs.ToJsonString(),
            payload.TryGetProperty("actor", out var actor)
                ? actor.GetString()!
                : commandBase.StartsWith("work-assign", StringComparison.Ordinal)
                    ? $"kernel:provider-ingest:{workId}" // the rule posture: the arm's own evidence law
                    : $"kernel:workflow:{instance.InstanceId:N}",
            // The arm's timeline actor verbatim (link:<provider>:<bot>),
            // from the same config the arm reads: the swap is
            // timeline-identical.
            $"link:{provider}:{providers.Find(provider)?.BotUserId ?? "provider"}",
            $"work created (issue #{root})",
            instance.InstanceId,
            providers.Find(provider)?.EndpointId);

        if (shadow)
        {
            // Would-do only (the swap posture): the exact plan, applied by
            // nobody; the arm performed the real rows.
            return StepOutcome.Done(
                new JsonObject { ["wouldDo"] = "work-create", ["plan"] = plan.ToJson() },
                null);
        }

        WorkCreate.Apply(db, envelopeValidator, plan, now);

        // Subject stamp: the instance now RUNS ON the work it created (the
        // arm created the work and acked it inline; the plane's next leg
        // does). The advance loop reloads the row per leg.
        instance.PrimaryWorkId = workId;
        if (plan.TeamId is { } owned)
        {
            instance.ProjectId = db.Projects.Local
                .FirstOrDefault(p => p.TeamId == owned && p.Name == WorkCreate.ProviderProjectName)?.ProjectId
                ?? instance.ProjectId;
        }

        return StepOutcome.Done(
            new JsonObject { ["effect"] = "work-create", ["workId"] = workId.ToString(), ["effectCommandId"] = plan.EffectCommandId },
            null,
            note: $"flow effect: work {workId:N} + routing intent {capability}");
    }

    /// <summary>
    /// The composition step (Phase 6): a child instance of a callable
    /// (`on: { invoked: {} }`) definition, created IN THE PARENT'S
    /// TRANSACTION under the preemption law — a denial has zero side
    /// effects. Identity: child = UUIDv5(invoke namespace,
    /// `parentInstance:stepId`), so a redelivered invoke re-derives the
    /// same child and collides on the PK (exactly-once child, the outbox
    /// law verbatim). The child's inputs ride its initial step-log row
    /// (`step_id = "input"`), which <see cref="BuildContext"/> hangs as
    /// <c>$input.&lt;name&gt;</c>. Awaited invokes park the parent (the
    /// park and the child row commit together — no wake-before-park
    /// window); the hand-off is the child's terminal completion event
    /// (see <see cref="AppendCompletionEventAsync"/>).
    /// </summary>
    private async Task<StepOutcome> ExecuteInvokeAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        string stepId,
        JsonElement payload,
        JsonObject context,
        DateTime now,
        CancellationToken ct,
        bool shadow = false)
    {
        var targetId = payload.GetProperty("workflow").GetString()!;
        var pinned = payload.TryGetProperty("version", out var pv) && pv.ValueKind == JsonValueKind.Number
            ? pv.GetInt32()
            : (int?)null;

        // Live snapshot law: the pinned version (or the callee's live
        // enabled version) is resolved AT INVOKE TIME and pinned into the
        // child row — a mid-flight parent version can never morph the
        // child. Disabled/vanished targets deny as a leg failure.
        var target = pinned is { } v
            ? await db.WorkflowDefinitions.AsNoTracking()
                .FirstOrDefaultAsync(d => d.DefId == targetId && d.Version == v, ct)
            : await db.WorkflowDefinitions.AsNoTracking()
                .Where(d => d.DefId == targetId && d.Enabled)
                .OrderByDescending(d => d.Version)
                .FirstOrDefaultAsync(ct);
        if (target is not { Enabled: true })
        {
            return new StepOutcome(
                OutcomeKind.LegFailed,
                new JsonObject { ["error"] = $"invoke target '{targetId}' is not an enabled definition" },
                null,
                null,
                EndReason: "invoke-target-unavailable");
        }

        var targetIdVer = $"{target.DefId}@{target.Version}";
        var reentrant = false;
        var maxInvocations = WorkflowLimits.DefaultMaxInvocations;
        using (var targetDoc = JsonDocument.Parse(target.Payload))
        {
            if (targetDoc.RootElement.TryGetProperty("composition", out var comp))
            {
                reentrant = comp.TryGetProperty("reentrant", out var r) && r.ValueKind == JsonValueKind.True;
                maxInvocations = comp.TryGetProperty("maxInvocations", out var m) && m.ValueKind == JsonValueKind.Number
                    ? m.GetInt32()
                    : WorkflowLimits.DefaultMaxInvocations;
            }
        }

        var childPath = $"{instance.InvocationPath}>{targetIdVer}";
        var childDepth = instance.Depth + 1;
        var decision = WorkflowPreemption.TryAdmit(childPath, childDepth, targetIdVer, reentrant, maxInvocations);
        if (!decision.Admitted)
        {
            // Guard 3: a denial is a branch, not a crash — the failed edge
            // is taken, the composition reason is the recorded end_reason,
            // the story is one timeline row. No child row, no outbox row.
            instance.EndReason = decision.Denial!.EndReason;
            return new StepOutcome(
                OutcomeKind.LegFailed,
                new JsonObject
                {
                    ["invoke"] = "denied",
                    ["reason"] = decision.Denial.EndReason,
                    ["detail"] = decision.Denial.Detail,
                },
                null,
                $"invoke denied: {decision.Denial.Detail}",
                EndReason: decision.Denial.EndReason);
        }

        var childId = WorkflowIdentity.ChildInstanceId(instance.InstanceId, stepId);
        var inputs = new JsonObject();
        if (payload.TryGetProperty("inputs", out var declared) && declared.ValueKind == JsonValueKind.Object)
        {
            foreach (var input in declared.EnumerateObject())
            {
                inputs[input.Name] = ResolveTemplate(JsonNode.Parse(input.Value.GetRawText()), context);
            }
        }

        var awaited = !payload.TryGetProperty("await", out var awaitFlag) || awaitFlag.ValueKind != JsonValueKind.False;
        if (shadow)
        {
            // Would-do only: the exact child spec the swap would create,
            // no child row, no start row, no park (a shadow run never
            // waits for a child it did not create).
            return StepOutcome.Continue(new JsonObject
            {
                ["invoke"] = "would-do",
                ["child"] = childId.ToString(),
                ["definition"] = targetIdVer,
                ["await"] = awaited,
                ["inputs"] = inputs,
            });
        }

        var exists = await db.WorkflowInstances.AsNoTracking().AnyAsync(i => i.InstanceId == childId, ct);
        if (!exists)
        {
            db.WorkflowInstances.Add(new WorkflowInstanceEntity
            {
                InstanceId = childId,
                DefId = target.DefId,
                DefVersion = target.Version,
                Team = instance.Team,
                ProjectId = instance.ProjectId,
                PrimaryWorkId = instance.PrimaryWorkId, // the flow's subject work is shared
                TriggerEventId = instance.TriggerEventId, // shared provenance
                Status = "running",
                ParentInstanceId = instance.InstanceId,
                Depth = childDepth,
                InvocationPath = childPath,
                StartedAt = now,
                UpdatedAt = now,
            });
            db.WorkflowStepLog.Add(new WorkflowStepLogEntity
            {
                InstanceId = childId,
                Seq = 1,
                StepId = "input",
                Status = "done",
                Output = inputs.ToJsonString(),
                StartedAt = now,
                UpdatedAt = now,
            });
            var startEnvelope = StartEnvelope(childId, targetIdVer, now);
            if (Activity.Current is { } stepSpan)
            {
                startEnvelope["traceparent"] = stepSpan.Id;
            }

            db.Outbox.Add(new OutboxEntity
            {
                OutboxId = Guid.NewGuid(),
                CommandId = $"wf-start:{childId:N}",
                RecipientEndpointId = WorkflowTrigger.Recipient,
                AggregateType = "work",
                AggregateId = null,
                EventType = StartRequestType,
                Envelope = startEnvelope.ToJsonString(),
                WorkId = instance.PrimaryWorkId,
                Status = "pending",
                AvailableAt = now,
                Attempts = 0,
                CreatedAt = now,
            });
        }

        var output = new JsonObject
        {
            ["invoke"] = exists ? "already-started" : "started",
            ["child"] = childId.ToString(),
            ["definition"] = targetIdVer,
            ["depth"] = childDepth,
        };

        // Composition observability: the child id names the lineage on
        // the step span, and the start row rides the step's trace (the
        // trace-correlation law of every plane effect) so the child's
        // spans continue THIS trace.
        Activity.Current?.SetTag("ofac.workflow.child", childId.ToString());
        return awaited
            ? new StepOutcome(
                OutcomeKind.Parked,
                new JsonObject { ["parked"] = "invoke", ["child"] = childId.ToString(), ["definition"] = targetIdVer },
                null,
                null,
                Deadline: now + ParseDuration(payload.GetProperty("timeout").GetString()!))
            : StepOutcome.Done(output, null);
    }

    /// <summary>The child-start row's internal channel type (the
    /// <c>decode.kernel</c> precedent: an internal payload with no wire
    /// schema; the sink consumes it by command id, nothing else reads
    /// it).</summary>
    public const string StartRequestType = "org.facilitator.internal.workflow.start";

    private static JsonObject StartEnvelope(Guid childId, string targetIdVer, DateTime now) => new()
    {
        ["specversion"] = "1.0",
        ["id"] = childId.ToString("N"),
        ["source"] = EnvelopeFactory.KernelSource,
        ["type"] = StartRequestType,
        ["subject"] = $"workflow/{childId}",
        ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["datacontenttype"] = "application/json",
        ["data"] = new JsonObject
        {
            ["child"] = childId.ToString(),
            ["definition"] = targetIdVer,
        },
    };

    private async Task<StepOutcome> ExecuteForkAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        string stepId,
        JsonElement payload,
        JsonObject context,
        JsonElement? envelope,
        DateTime now,
        CancellationToken ct,
        bool shadow)
    {
        _ = ct;
        if (instance.PrimaryWorkId is not { } subjectId)
        {
            return StepOutcome.LegFailed(new JsonObject { ["error"] = "fork-work needs a subject work" });
        }

        var predecessor = await db.Works.AsNoTracking().FirstOrDefaultAsync(
            w => w.WorkId == subjectId, ct);
        if (predecessor is null)
        {
            return StepOutcome.LegFailed(new JsonObject { ["error"] = "subject work vanished" });
        }

        if (predecessor.State is not ("completed" or "cancelled" or "failed"))
        {
            // The terminal law is the step's precondition (the flow's guard
            // decides who gets here; a race between guard and step fails the
            // leg rather than resurrecting the work).
            return StepOutcome.LegFailed(new JsonObject { ["error"] = $"fork-work needs a terminal subject (state {predecessor.State})" });
        }

        // The note is a template (the interpolation law): the review arm's
        // exact sentence shape is declarable with one `${...|...}` ref.
        var noteNode = ResolveTemplate(JsonValue.Create(payload.GetProperty("noteRef").GetString()), context);
        var note = noteNode is JsonValue nv && nv.TryGetValue<string>(out var rendered) ? rendered : string.Empty;
        note = note.Length <= 2000 ? note : note[..2000];
        // The provider token of the trigger event (the arm's actor law:
        // provider.input / created / work.linked rows all carry
        // provider:<p> on BOTH execution modes; an event without one
        // lands provider:kernel, visible and debuggable).
        var provider = "kernel";
        if (envelope is { } triggerEnvelope
            && triggerEnvelope.TryGetProperty("data", out var triggerData)
            && triggerData.TryGetProperty("provider", out var providerElement)
            && providerElement.ValueKind == JsonValueKind.String)
        {
            provider = providerElement.GetString()!;
        }

        var plan = Facilitator.Kernel.Work.WorkFork.ComputePlan(
            predecessor, WorkflowIdentity.ForkWorkId(instance.InstanceId, stepId), note);

        if (shadow)
        {
            return StepOutcome.Done(
                new JsonObject { ["wouldDo"] = plan.ToJson(), ["note"] = note }, null);
        }

        // The shared law (WorkFork, one implementation with the arm):
        // successor + created + work.linked + link + binding repoint +
        // reply naming the successor, then the input-side rows
        // (provider.input, optional media, the progress echo). Same
        // sentences, kinds, actors and command ids as the arm's path —
        // the swap is timeline-identical (the plane adds workflow.stage
        // rows on top, the superset law).
        await Facilitator.Kernel.Work.WorkFork.ApplyAsync(
            db, envelopeValidator, providers.Find(provider), plan, predecessor,
            provider, instance.InstanceId, now, ct);
        Facilitator.Kernel.Work.WorkFork.AddInputRows(
            db, envelopeValidator, providers.Find(provider), provider,
            plan.SuccessorWorkId, 1, note, sourceRefs: null, now);

        Activity.Current?.SetTag("ofac.workflow.correlation_id", instance.InstanceId.ToString("N"));
        return StepOutcome.Done(
            plan.ToJson(),
            plan.ReplyCommandId,
            note: $"successor work {plan.SuccessorShort} opened");
    }

    private async Task<StepOutcome> ExecuteApprovalAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        string stepId,
        JsonElement payload,
        JsonObject context,
        DateTime now,
        CancellationToken ct,
        bool shadow = false)
    {
        _ = ct;
        var approvalId = WorkflowIdentity.Uuid5(WorkflowIdentity.ForkNamespace, $"{instance.InstanceId:N}:approval:{stepId}");
        var eligible = (ResolveString(payload.GetProperty("approvers"), context) ?? "kernel:workflow")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var action = new JsonObject
        {
            ["kind"] = "workflow.approval",
            ["instance"] = instance.InstanceId.ToString(),
            ["step"] = stepId,
            ["question"] = ResolveString(payload.GetProperty("question"), context),
        };
        var expiresAt = now.AddDays(1);
        var digest = ApprovalDigest.Compute(
            approvalId.ToString(),
            JsonSerializer.Deserialize<JsonElement>(action.ToJsonString()),
            eligible,
            expiresAt,
            now);
        if (shadow)
        {
            // Would-do: the approval row the swap would park on. A shadow
            // run does not block on effects it will not write (a park
            // without its row could never wake).
            return StepOutcome.Done(
                new JsonObject
                {
                    ["wouldDo"] = "approval",
                    ["approvalId"] = approvalId.ToString(),
                    ["expiresAt"] = expiresAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                },
                null);
        }

        db.Approvals.Add(new ApprovalEntity
        {
            ApprovalId = approvalId,
            WorkId = instance.PrimaryWorkId,
            ActionDigest = digest,
            State = "pending",
            Nonce = Guid.NewGuid().ToString("N"),
            EligibleActors = JsonSerializer.Serialize(eligible),
            ExpiresAt = expiresAt,
            CreatedAt = now,
            RequestedBy = Actor,
            RuleId = $"workflow:{instance.InstanceId}:{stepId}",
        });
        return new StepOutcome(
            OutcomeKind.Parked,
            new JsonObject
            {
                ["parked"] = "approval",
                ["correlationKey"] = approvalId.ToString("N"),
                ["approvalId"] = approvalId.ToString(),
            },
            EffectCommandId: null,
            TimelineNote: null,
            Deadline: expiresAt);
    }

    private async Task<StepOutcome> ExecuteEscalateAsync(
        FacilitatorDbContext db, WorkflowInstanceEntity instance, JsonElement payload, JsonObject context, CancellationToken ct, bool shadow = false)
    {
        var text = ResolveString(payload.GetProperty("message"), context) ?? payload.GetProperty("message").GetString()!;
        if (shadow)
        {
            return StepOutcome.Done(
                new JsonObject { ["wouldDo"] = "escalate", ["text"] = text }, null);
        }

        // The ledger is THE human-egress path: its row + outbox row join
        // this step's transaction (the promise and the attempt, atomically).
        var (notificationId, state) = await notifications.EmitAsync(
            db,
            new NotificationRequest(
                Audience: instance.PrimaryWorkId is null ? "operators" : "team",
                Text: text,
                Level: "warning",
                WorkId: instance.PrimaryWorkId,
                TeamSlug: instance.Team,
                CommandId: $"wf:{instance.InstanceId:N}:{instance.CurrentStep}"),
            ct);
        return StepOutcome.Done(
            new JsonObject { ["notification"] = notificationId.ToString(), ["state"] = state },
            effectCommandId: null,
            note: $"flow escalation: {text[..Math.Min(80, text.Length)]}");
    }

    // ---- helpers ---------------------------------------------------------------

    /// <summary>Commands that address a Work (the runner injects
    /// <c>data.workId</c> from the primary work).</summary>
    private static readonly HashSet<string> WorkScopedCommands = new(StringComparer.Ordinal)
    {
        "org.facilitator.command.work.steer.v1",
        "org.facilitator.command.work.nudge.v1",
        "org.facilitator.command.work.cancel.v1",
        "org.facilitator.command.work.status.request.v1",
        "org.facilitator.command.work.assign.v1",
    };

    private static readonly HashSet<string> TargetScopedCommands = new(StringComparer.Ordinal)
    {
        "org.facilitator.command.communication.post.v1",
        "org.facilitator.command.communication.reply.v1",
        "org.facilitator.command.communication.react.v1",
        "org.facilitator.command.communication.update.v1",
        "org.facilitator.command.communication.acknowledge.v1",
    };

    private string RecipientFor(string commandType, JsonObject data)
    {
        // Communication commands whose target names a REGISTERED provider
        // address that adapter's link endpoint (the arm's AppendCommand
        // posture — the thread that triggered the flow hears back on the
        // wire, not into the log). Anything else — work commands, targets
        // with no registered adapter — rides the kernel log channel:
        // observable, consumed, never a silent drop.
        if (commandType.StartsWith("org.facilitator.command.communication.", StringComparison.Ordinal)
            && data["target"] is JsonObject target
            && target["provider"] is JsonValue provider
            && provider.TryGetValue<string>(out var providerToken)
            && providers.Find(providerToken)?.EndpointId is { Length: > 0 } endpoint)
        {
            return $"link.{endpoint}";
        }

        return "kernel.log";
    }

    // The copied trigger envelope's data.reference, when it has one —
    // the natural reply target of a flow started from a conversation.
    private static JsonNode? ConversationTargetFromEvent(JsonObject context) =>
        context["event"] is JsonObject envelope
        && envelope["data"] is JsonObject data
        && data["reference"] is JsonObject reference
            ? reference.DeepClone()
            : null;

    private static readonly string[] TargetKeys =
    [
        "provider", "tenantOrOrganizationId", "channelOrResourceId", "rootThreadId",
        "parentMessageId", "subjectRef", "conversationKind", "actorProviderAccountId",
    ];

    private static JsonNode ProjectTarget(JsonNode? candidate, string commandType)
    {
        // conversationTarget law: provider + conversationKind required;
        // foreign keys (providerInstanceId, authorProviderAccountId — the
        // reference shape's extras) dropped, not rejected.
        var projected = new JsonObject();
        if (candidate is JsonObject obj)
        {
            foreach (var key in TargetKeys)
            {
                if (obj.TryGetPropertyValue(key, out var value) && value is not null)
                {
                    projected[key] = value.DeepClone();
                }
            }
        }

        if (!projected.ContainsKey("provider"))
        {
            projected["provider"] = "kernel";
        }

        if (!projected.ContainsKey("conversationKind"))
        {
            projected["conversationKind"] = "channel";
        }

        // B4 as the schema spells it: a POST addresses a dm or a channel —
        // a thread/comment trigger posted to is a top-level post into that
        // conversation's channel, so the kind coerces (keys kept: a channel
        // post still lands on the right resource).
        if (commandType == "org.facilitator.command.communication.post.v1"
            && projected["conversationKind"] is JsonValue kind
            && kind.TryGetValue<string>(out var kindText)
            && kindText is not ("dm" or "channel"))
        {
            projected["conversationKind"] = "channel";
        }

        return projected;
    }

    private static string FirstBranch(JsonElement payload) =>
        payload.GetProperty("question").GetProperty("options")[0].GetProperty("branch").GetString()!;

    /// <summary>The edge law of the definition schema: a decide branch is a
    /// step id or <c>end</c>; a completed step continues into the
    /// array-adjacent step whose <c>on</c> is absent or <c>always</c> (the
    /// chain), else into any step declaring <c>&lt;stepId&gt;:completed</c>;
    /// <c>null</c> = no edge, the flow ends.</summary>
    private static string? NextStepId(
        IReadOnlyList<JsonElement> steps, string stepId, bool completed, string? decisionBranch)
    {
        if (!completed)
        {
            return null;
        }

        if (decisionBranch is { Length: > 0 })
        {
            return decisionBranch;
        }

        var index = StepIndex(steps, stepId);
        if (index + 1 < steps.Count && SelectorOf(steps[index + 1]) is not { Length: > 0 } successorSelector
            || SelectorOf(steps[Math.Min(index + 1, steps.Count - 1)]) == "always" && index + 1 < steps.Count)
        {
            return steps[index + 1].GetProperty("id").GetString();
        }

        var explicitEdge = steps.FirstOrDefault(s => SelectorOf(s) == $"{stepId}:completed");
        return explicitEdge.ValueKind == JsonValueKind.Object ? explicitEdge.GetProperty("id").GetString() : null;
    }

    /// <summary>The failed edge: the first step selecting the bare
    /// <c>failed</c> selector (the flow's failure leg — in v1 only an
    /// approval expiry routes there, every other failure branch names its
    /// target explicitly, so the selector has exactly one referent).</summary>
    private static string? FailedEdgeId(IReadOnlyList<JsonElement> steps, string stepId)
    {
        _ = stepId;
        var leg = steps.FirstOrDefault(s => SelectorOf(s) == "failed");
        return leg.ValueKind == JsonValueKind.Object ? leg.GetProperty("id").GetString() : null;
    }

    private static int StepIndex(IReadOnlyList<JsonElement> steps, string stepId)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].GetProperty("id").GetString() == stepId)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? SelectorOf(JsonElement step) =>
        step.TryGetProperty("on", out var edge) && edge.ValueKind == JsonValueKind.String
            ? edge.GetString()
            : null;

    private async Task CompleteAsync(
        FacilitatorDbContext db, WorkflowInstanceEntity instance, JsonElement definitionRoot, DateTime now, CancellationToken ct)
    {
        await ProjectOutputAsync(db, instance, definitionRoot, now, ct);
        Terminate(db, instance, "completed", null, now);
    }

    private async Task ProjectOutputAsync(
        FacilitatorDbContext db, WorkflowInstanceEntity instance, JsonElement definitionRoot, DateTime now, CancellationToken ct)
    {
        _ = now;
        // The final leg's log row is written in THIS transaction: flush
        // before projecting, or a one-step flow projects over a step log
        // that lacks its own last leg.
        await db.SaveChangesAsync(ct);
        string? output = null;
        if (definitionRoot.TryGetProperty("output", out var outputSpec)
            && outputSpec.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object)
        {
            var document = JsonSerializer.SerializeToElement(BuildContext(db, instance, envelope: null));
            var projected = new JsonObject();
            foreach (var field in project.EnumerateObject())
            {
                var found = WorkflowMatcher.ResolvePath(document, field.Value.GetString()!);
                projected[field.Name] = found is null ? null : JsonNode.Parse(found.Value.GetRawText());
            }

            output = projected.ToJsonString();
        }

        instance.Output = output;
    }

    /// <summary>The awaited parent of a completion hand-off: the instance
    /// parked (waiting or shadowed, deadline set) on an invoke step whose
    /// parked output names this child. Deterministic single lookup; a
    /// parent that resolved its park first (timeout, cancel) is absent —
    /// the race law decides, the loser no-ops.</summary>
    private async Task<Guid?> ParkedParentAsync(WorkflowWake wake, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var childText = wake.InstanceId.ToString();
        var parents = await db.WorkflowInstances
            .Where(i => i.Status == "waiting" || i.Status == "shadowed")
            .Where(i => i.DeadlineAt != null)
            .Select(i => new { i.InstanceId, i.CurrentStep })
            .ToListAsync(ct);
        foreach (var candidate in parents)
        {
            var parked = await db.WorkflowStepLog.AsNoTracking()
                .Where(l => l.InstanceId == candidate.InstanceId
                            && l.StepId == candidate.CurrentStep
                            && l.Status == "running")
                .Select(l => l.Output)
                .FirstOrDefaultAsync(ct);
            if (parked is not { Length: > 0 })
            {
                continue;
            }

            using var doc = JsonDocument.Parse(parked);
            if (doc.RootElement.TryGetProperty("parked", out var kind)
                && kind.GetString() == "invoke"
                && doc.RootElement.TryGetProperty("child", out var child)
                && child.GetString() == childText)
            {
                return candidate.InstanceId;
            }
        }

        return null;
    }

    /// <summary>Guard 4's cascade (sync core): every non-terminal
    /// descendant — direct or transitive, awaited or detached — ends
    /// <c>cancelled</c> with <c>parent-cancelled</c> FIRST (bottom-up
    /// order: a cancelled child's own completion event carries
    /// <c>cancelled</c>, which the parent's resolved-or-failing park
    /// consumes as a no-op), and only the works a cancelled instance
    /// CREATED ride along (a work the flow adopted is not ours to
    /// cancel). Called from <see cref="Terminate"/> on every real
    /// failed/cancelled transition and from the invoke-timeout leg.</summary>
    private void CancelSubtreeCore(FacilitatorDbContext db, Guid instanceId, DateTime now)
    {
        var children = db.WorkflowInstances
            .Where(i => i.ParentInstanceId == instanceId)
            .ToList();
        foreach (var child in children)
        {
            if (child.Status is "completed" or "failed" or "cancelled" or "shadowed")
            {
                continue;
            }

            CancelSubtreeCore(db, child.InstanceId, now); // grandchildren first
            Terminate(db, child, "cancelled", "parent-cancelled", now);
            if (child.PrimaryWorkId is { } createdWork)
            {
                // Created-vs-adopted: a child INHERITS the flow's subject
                // work (shared provenance) — only a work the child's own
                // work-create leg named is ours to cancel.
                var workText = createdWork.ToString();
                var createdHere = db.WorkflowStepLog.Any(l => l.InstanceId == child.InstanceId
                                                             && l.Output != null
                                                             && l.Output.Contains("\"work-create\"", StringComparison.Ordinal)
                                                             && l.Output.Contains(workText, StringComparison.Ordinal));
                if (!createdHere)
                {
                    continue;
                }

                var work = db.Works.FirstOrDefault(w => w.WorkId == createdWork);
                if (work is not null && work.State is not ("completed" or "cancelled" or "failed"))
                {
                    work.State = "cancelled";
                    work.Version += 1;
                    work.UpdatedAt = now;
                    db.WorkEvents.Add(new WorkEventEntity
                    {
                        EventId = Guid.NewGuid(),
                        WorkId = createdWork,
                        WorkVersion = work.Version,
                        Kind = "state.transition",
                        Actor = Actor,
                        Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
                        {
                            ["from"] = "workflow cascade",
                            ["to"] = "cancelled",
                            ["reason"] = "parent-cancelled",
                        }),
                        CorrelationId = instanceId.ToString("N"),
                        CreatedAt = now,
                    });
                }
            }
        }
    }

    /// <summary>The async twin of <see cref="CancelSubtreeCore"/> (the
    /// sweeper's invoke-timeout leg: its own transaction, its own save).</summary>
    public async Task<int> CancelSubtreeAsync(FacilitatorDbContext db, Guid instanceId, DateTime now, CancellationToken ct)
    {
        _ = ct;
        var before = await db.WorkflowInstances.CountAsync(
            i => i.ParentInstanceId != null && i.Status != "cancelled", ct);
        CancelSubtreeCore(db, instanceId, now);
        await db.SaveChangesAsync(ct);
        _ = before;
        return await Task.FromResult(0);
    }

    /// <summary>The operator cancel (Phase 6): the instance ends
    /// <c>cancelled</c> with <c>operator-cancel</c>, the whole subtree
    /// cascades, and every wake row aimed at it resolves as a no-op (the
    /// conditional UPDATE of Resume/TimeoutLeg finds no park).</summary>
    public async Task<bool> CancelInstanceAsync(Guid instanceId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.BeginTransactionAsync(ct);
        await InstanceLockAsync(db, instanceId, ct);
        var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.InstanceId == instanceId, ct);
        if (instance is null || instance.Status is "completed" or "failed" or "cancelled")
        {
            await db.Database.CommitTransactionAsync(ct);
            return false;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        CancelSubtreeCore(db, instanceId, now); // descendants first
        Terminate(db, instance, "cancelled", "operator-cancel", now);
        await db.SaveChangesAsync(ct);
        await db.Database.CommitTransactionAsync(ct);
        await PostgresNotifications.NotifyOutboxAsync(db, instanceId.ToString(), ct);
        return true;
    }

    private void Terminate(
        FacilitatorDbContext db, WorkflowInstanceEntity instance, string status, string? endReason, DateTime now)
    {
        // Shadow law, terminal leg: a would-do run ends 'shadowed' (never
        // 'completed' — the arm decided the real outcome), with the
        // terminated shape: no current step, no deadline.
        var real = instance.Status != "shadowed";
        instance.Status = real ? status : "shadowed";
        instance.CurrentStep = null;
        instance.DeadlineAt = null;
        // An end_reason set mid-run by an executor (a preemption denial)
        // is the composition story of THIS run: a later termination never
        // erases it.
        instance.EndReason ??= endReason;
        instance.UpdatedAt = now;

        // The completion hand-off (Phase 6): a REAL terminal transition
        // publishes the wire event IN THE SAME TRANSACTION as the terminal
        // row — the wake can never arrive before the child is terminal, and
        // a crash rolls both back together. A shadowed run publishes
        // nothing (a would-do run has no outcome to hand off).
        if (real)
        {
            // Detached-child lifecycle (guard 4): a FAILED or CANCELLED
            // run takes its whole non-terminal subtree with it (a
            // completed parent leaves detached children to finish —
            // fire-and-forget is the point of await: false).
            if (status is "failed" or "cancelled")
            {
                CancelSubtreeCore(db, instance.InstanceId, now);
            }

            AppendCompletionEvent(db, instance, status, now);
        }
    }

    /// <summary>The composition completion event: the child's projected
    /// output, addressed to <c>workflow.kernel</c> with command id
    /// <c>wf-wake:&lt;child&gt;:&lt;status&gt;</c> — the command-id uniqueness
    /// makes the resume exactly-once (the hand-off re-delivery law of the
    /// outbox: one row, one wake, however often the notify fires).</summary>
    public const string CompletedEventType = "org.facilitator.event.workflow.completed.v1";

    private void AppendCompletionEvent(
        FacilitatorDbContext db, WorkflowInstanceEntity instance, string status, DateTime now)
    {
        var data = new JsonObject
        {
            ["instanceId"] = instance.InstanceId.ToString(),
            ["parentInstanceId"] = instance.ParentInstanceId?.ToString(),
            ["definitionId"] = instance.DefId,
            ["definitionVersion"] = instance.DefVersion,
            ["status"] = status,
            ["output"] = instance.Output is { Length: > 0 } outputJson
                ? JsonNode.Parse(outputJson)
                : new JsonObject(),
        };
        var envelope = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = $"wfc-{instance.InstanceId:N}-{status}",
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = CompletedEventType,
            ["subject"] = $"workflow/{instance.InstanceId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/event.workflow.completed.v1.json",
            ["data"] = data,
        };
        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = WorkflowIdentity.Uuid5(WorkflowIdentity.InvokeNamespace, $"complete:{instance.InstanceId:N}:{status}"),
            CommandId = $"wf-wake:{instance.InstanceId:N}:{status}",
            RecipientEndpointId = WorkflowTrigger.Recipient,
            AggregateType = "work",
            AggregateId = instance.PrimaryWorkId,
            EventType = CompletedEventType,
            Envelope = envelope.ToJsonString(),
            WorkId = instance.PrimaryWorkId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }

    private void AppendStageTimeline(FacilitatorDbContext db, Guid workId, Guid instanceId, string stepId, string note)
    {
        var version = db.Works.AsNoTracking().Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefault();
        if (version is null)
        {
            return; // the subject work vanished: the plane never blocks on its timeline
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version.Value,
            Kind = "workflow.stage",
            Actor = Actor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["instance"] = instanceId.ToString(),
                ["step"] = stepId,
                ["note"] = note,
            }),
            CorrelationId = instanceId.ToString("N"),
            CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
        });
    }

    private static (Guid? Work, Guid? Project, string? Team) ResolveWorkContext(
        FacilitatorDbContext db, JsonElement envelope)
    {
        // 1) envelope-carried workId context; 2) the communication binding
        // the arms use (provider + channel + root); 3) workless.
        var data = envelope.TryGetProperty("data", out var d) ? d : default;
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("workId", out var wid) && wid.ValueKind == JsonValueKind.String
            && Guid.TryParse(wid.GetString(), out var direct))
        {
            return Load(db, direct);
        }

        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("reference", out var reference) && reference.ValueKind == JsonValueKind.Object
            && reference.TryGetProperty("provider", out var provider)
            && reference.TryGetProperty("channelOrResourceId", out var channel)
            && reference.TryGetProperty("rootThreadId", out var root))
        {
            var binding = db.CommunicationBindings.AsNoTracking()
                .FirstOrDefault(b =>
                    b.Provider == provider.GetString()
                    && b.Channel == channel.GetString()
                    && b.RootMessageId == root.GetString()
                    && b.WorkId != null);
            if (binding?.WorkId is { } bound)
            {
                return Load(db, bound);
            }
        }

        return (null, null, null);

        static (Guid?, Guid?, string?) Load(FacilitatorDbContext db, Guid workId)
        {
            var work = db.Works.AsNoTracking()
                .Where(w => w.WorkId == workId)
                .Select(w => new { w.WorkId, w.ProjectId, TeamSlug = w.Team != null ? w.Team.Slug : null })
                .FirstOrDefault();
            return work is null
                ? (null, null, null)
                : (work.WorkId, work.ProjectId, work.TeamSlug);
        }
    }

    private static async Task<WorkflowDefinitionEntity?> LoadDefinitionAsync(
        FacilitatorDbContext db, WorkflowInstanceEntity instance, CancellationToken ct) =>
        await db.WorkflowDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DefId == instance.DefId && d.Version == instance.DefVersion, ct);

    private JsonObject BuildContext(FacilitatorDbContext db, WorkflowInstanceEntity instance, JsonElement? envelope)
    {
        var context = new JsonObject { ["instance"] = instance.InstanceId.ToString() };
        var stepsOut = new JsonObject();
        foreach (var log in db.WorkflowStepLog.AsNoTracking()
                     .Where(l => l.InstanceId == instance.InstanceId)
                     .OrderBy(l => l.Seq)
                     .ToList())
        {
            if (log.Output is { Length: > 0 } outputJson)
            {
                // The law's shape: $steps.<id>.output.<path> — the log's
                // output document hangs under an `output` segment.
                stepsOut[log.StepId] = new JsonObject { ["output"] = JsonNode.Parse(outputJson) };
            }
        }

        context["steps"] = stepsOut;

        // Invoke inputs: the child's initial input row (step_id "input")
        // hangs as $input.<name> (the invoke law: inputs ride the child's
        // first log row, shared provenance otherwise).
        if (stepsOut.TryGetPropertyValue("input", out var inputNode)
            && inputNode is JsonObject inputObj
            && inputObj["output"] is { } inputOut)
        {
            context["input"] = inputOut.DeepClone();
        }

        if (envelope is not null)
        {
            context["event"] = JsonNode.Parse(envelope.Value.GetRawText());
        }

        if (instance.PrimaryWorkId is { } workId)
        {
            var work = db.Works.AsNoTracking()
                .Where(w => w.WorkId == workId)
                .Select(w => new { id = w.WorkId, state = w.State, objective = w.Objective, version = w.Version, owner = w.OwnerActor })
                .FirstOrDefault();
            if (work is not null)
            {
                context["work"] = JsonSerializer.SerializeToNode(new
                {
                    id = work.id.ToString(),
                    state = work.state,
                    objective = work.objective,
                    version = work.version,
                    owner = work.owner,
                });
            }
        }

        if (envelope is not null)
        {
            // As-built assignment context: the capability the completing
            // assignment rode, read the way the arms read it (the assign
            // envelope outbox row IS the ledger — no runtime join onto a
            // capability column). `assign.capability` guards then scope a
            // flow to the completing assignment's capability, provider-style.
            var eventData = envelope.Value.TryGetProperty("data", out var d) ? d : default;
            if (eventData.ValueKind == JsonValueKind.Object
                && instance.PrimaryWorkId is { } subjectWork
                && eventData.TryGetProperty("assignmentId", out var wireAssignment)
                && wireAssignment.ValueKind == JsonValueKind.String)
            {
                var assignRow = db.Outbox.AsNoTracking()
                    .Where(o => o.WorkId == subjectWork
                                && o.EventType == "org.facilitator.command.work.assign.v1")
                    .ToList()
                    .FirstOrDefault(o => JsonDocument.Parse(o.Envelope).RootElement
                        .GetProperty("data").TryGetProperty("assignmentId", out var a)
                        && a.GetString() == wireAssignment.GetString());
                if (assignRow is not null)
                {
                    var capability = JsonDocument.Parse(assignRow.Envelope).RootElement
                        .GetProperty("data").GetProperty("capabilityId").GetString();
                    context["assign"] = new JsonObject { ["capability"] = capability };
                }
            }
        }

        return context;
    }

    /// <summary>Template law: a string starting with <c>$</c> is a dotted
    /// path over the context document (<c>$event.data.x</c>,
    /// <c>$steps.&lt;id&gt;.output.&lt;path&gt;</c>, <c>$work.&lt;field&gt;</c>,
    /// <c>$input.&lt;name&gt;</c>); an unresolved ref stays the literal (visible,
    /// debuggable); anything else is a literal.</summary>
    /// <summary>Scalar projection of a dotted path over the context (a
    /// `|`-separated list tries each candidate in order); strings pass
    /// through raw, other scalars stringify; a container or a miss is
    /// null (the interpolation law's empty segment).</summary>
    private static string? ResolveScalar(JsonObject context, string dottedPaths)
    {
        foreach (var path in dottedPaths.Split('|'))
        {
            JsonNode? node = context;
            foreach (var segment in path.Split('.'))
            {
                node = node switch
                {
                    JsonObject o when o.TryGetPropertyValue(segment, out var next) => next,
                    _ => null,
                };
                if (node is null)
                {
                    break;
                }
            }

            if (node is JsonValue val)
            {
                return val.TryGetValue<string>(out var raw) ? raw : val.ToString();
            }
        }

        return null;
    }

    private static JsonNode? ResolveTemplate(JsonNode? value, JsonObject context)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.Contains("${", StringComparison.Ordinal))
        {
            // Interpolation law: `${dotted.path}` segments resolve against the
            // context (first `|`-separated path that resolves wins, so a
            // fallback source is declarable); an unresolved segment becomes
            // the empty string; everything else is literal text.
            var rendered = new System.Text.StringBuilder();
            var cursor = 0;
            while (cursor < text.Length)
            {
                var open = text.IndexOf("${", cursor, StringComparison.Ordinal);
                if (open < 0)
                {
                    rendered.Append(text[cursor..]);
                    break;
                }

                rendered.Append(text, cursor, open - cursor);
                var close = text.IndexOf('}', open + 2);
                if (close < 0)
                {
                    rendered.Append(text[open..]);
                    break;
                }

                var path = text[(open + 2)..close];
                rendered.Append(ResolveScalar(context, path) ?? string.Empty);
                cursor = close + 1;
            }

            return JsonValue.Create(rendered.ToString());
        }

        if (value is JsonValue v2 && v2.TryGetValue<string>(out text) && text.StartsWith('$'))
        {
            JsonNode? node = context;
            foreach (var segment in text[1..].Split('.'))
            {
                if (node is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
                {
                    node = next;
                }
                else
                {
                    return JsonValue.Create(text);
                }
            }

            return node?.DeepClone();
        }

        if (value is JsonObject o)
        {
            var copy = new JsonObject();
            foreach (var child in o)
            {
                copy[child.Key] = ResolveTemplate(child.Value, context);
            }

            return copy;
        }

        if (value is JsonArray a)
        {
            var copy = new JsonArray();
            foreach (var child in a)
            {
                copy.Add(ResolveTemplate(child?.DeepClone(), context));
            }

            return copy;
        }

        return value?.DeepClone();
    }

    /// <summary>Wake semantics: an approval decision whose data says
    /// anything but <c>approved</c> takes the failed edge (denied, expired,
    /// revoked are all human "no"); every other wake continues.</summary>
    private static StepOutcome WakeOutcome(JsonElement wakeEnvelope)
    {
        var output = new JsonObject
        {
            ["resumedBy"] = wakeEnvelope.GetProperty("type").GetString(),
            ["wake"] = JsonNode.Parse(wakeEnvelope.GetRawText()),
        };
        var data = wakeEnvelope.TryGetProperty("data", out var d) ? d : default;
        var decision = data.ValueKind == JsonValueKind.Object
                       && data.TryGetProperty("decision", out var dec) && dec.ValueKind == JsonValueKind.String
            ? dec.GetString()
            : null;
        if (decision is not null && decision != "approved")
        {
            return new StepOutcome(OutcomeKind.LegFailed, output, null, null);
        }

        // The completion hand-off: a child that ended failed or cancelled
        // wakes the parked invoke step as a FAILURE (the parent decides how
        // to grieve it — its failed edge); a completed child resumes with
        // the child's projected output hanging under `output`.
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            && status.GetString() is "failed" or "cancelled")
        {
            return new StepOutcome(OutcomeKind.LegFailed, output, null, null);
        }

        return StepOutcome.Continue(output);
    }

    private static string? ResolveString(JsonElement refOrLiteral, JsonObject context) =>
        refOrLiteral.ValueKind != JsonValueKind.String
            ? null
            : ResolveTemplate(JsonValue.Create(refOrLiteral.GetString()), context) is JsonValue s
                ? s.GetValue<string>()
                : null;

    private static StepOutcome ParkOutcome(JsonObject context, JsonElement correlateRef, string parkedKind, DateTime deadline)
    {
        var key = ResolveString(correlateRef, context);
        return new StepOutcome(
            OutcomeKind.Parked,
            new JsonObject
            {
                ["parked"] = parkedKind,
                ["correlationKey"] = key ?? string.Empty,
            },
            EffectCommandId: null,
            TimelineNote: null,
            Deadline: deadline);
    }

    private static string stepSegment(string? stepId) => stepId ?? "entry";

    private async Task<StepOutcome> ExecuteSelectAsync(JsonElement payload, JsonObject context, CancellationToken ct)
    {
        var source = payload.TryGetProperty("source", out var src) ? src.GetString() : "event";
        JsonElement document;
        if (source == "manifest")
        {
            // The manifest-source law: the completed event carries a ref +
            // digest, the record arm verified the digest pre-commit, and
            // the STORED bytes are the source (the ResultIngress law of the
            // verified artifact, not the wire restatement). A missing ref or
            // a vanished artifact fails the leg (visible, requeueable).
            var manifest = await ManifestFromContextAsync(context, ct);
            if (manifest is null)
            {
                return StepOutcome.LegFailed(new JsonObject { ["error"] = "manifest source unreadable" });
            }

            document = manifest.Value;
        }
        else
        {
            document = JsonSerializer.SerializeToElement(context);
        }

        var project = new JsonObject();
        foreach (var field in payload.GetProperty("project").EnumerateObject())
        {
            var found = WorkflowMatcher.ResolvePath(document, field.Value.GetString()!);
            project[field.Name] = found is null ? null : JsonNode.Parse(found.Value.GetRawText());
        }

        if (payload.TryGetProperty("defaults", out var defaults))
        {
            // Fill-only law: a default lands where the projection resolved
            // to null, over the run context (never introduces a new field).
            foreach (var field in defaults.EnumerateObject())
            {
                if (project.TryGetPropertyValue(field.Name, out var current) && current is null)
                {
                    project[field.Name] = ResolveTemplate(JsonNode.Parse(field.Value.GetRawText()), context);
                }
            }
        }

        return StepOutcome.Continue(project);
    }

    /// <summary>
    /// The signal step (Phase 6.6): a wire event appended to
    /// <c>workflow.kernel</c> addressed to ONE instance — point-to-point,
    /// no lineage, no budget (a signal can neither spawn nor fail
    /// anything). Command id is the step-command id verbatim (a
    /// redelivered leg re-derives it, the outbox law); the sink resolves
    /// <c>data.target</c> at delivery into a wake row for the target
    /// (the target's own park race law decides), falling through to
    /// on.event matching when the target holds no matching park. Fire-
    /// and-forget: <c>done</c> when the row is appended — no timeout, no
    /// onTimeout.
    /// </summary>
    private async Task<StepOutcome> ExecuteSignalAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        string stepId,
        JsonElement payload,
        JsonObject context,
        string commandId,
        DateTime now,
        CancellationToken ct,
        bool shadow = false)
    {
        _ = ct;
        var target = ResolveString(payload.GetProperty("target"), context);
        if (target is not { Length: > 0 } || !Guid.TryParse(target, out var targetId))
        {
            return StepOutcome.LegFailed(
                new JsonObject { ["error"] = $"signal target '{target}' is not a resolvable instance id" });
        }

        var eventType = payload.GetProperty("event").GetProperty("type").GetString()!;
        var data = new JsonObject();
        if (payload.GetProperty("event").TryGetProperty("data", out var declared)
            && declared.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in declared.EnumerateObject())
            {
                data[field.Name] = ResolveTemplate(JsonNode.Parse(field.Value.GetRawText()), context);
            }
        }

        var envelope = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = $"wfs-{Guid.NewGuid():N}",
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = eventType,
            // Both directions of a coordination pair queryable: the
            // subject names the sender instance.
            ["subject"] = $"workflow/{instance.InstanceId}",
            // The routing plane-extension rides the envelope top level,
            // NOT the data (every wire event schema is closed — the
            // trigger-row law: plane routing stays outside the data so
            // the data stays schema-clean).
            ["target"] = targetId.ToString(),
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = $"https://schemas.openfacilitator.org/v1/{eventType["org.facilitator.".Length..]}.json",
            ["data"] = data,
        };
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            return StepOutcome.LegFailed(
                new JsonObject { ["error"] = $"envelope rejected: {verdict.ErrorClass}: {verdict.Message}" });
        }

        if (shadow)
        {
            return StepOutcome.Done(new JsonObject
            {
                ["wouldDo"] = "signal",
                ["target"] = targetId.ToString(),
                ["eventType"] = eventType,
            }, null);
        }

        // Signal command id: the step-command law in the plane's signal
        // namespace — wf-signal:<senderInstance>:<stepId>:<seq>. A
        // redelivered leg re-derives it; the outbox uniqueness converges.
        var segments = commandId.Split(':');
        var signalCommand = $"wf-signal:{segments[2]}:{segments[3]}:{segments[4]}";
        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            CommandId = signalCommand,
            RecipientEndpointId = WorkflowTrigger.Recipient,
            AggregateType = "work",
            AggregateId = instance.PrimaryWorkId,
            EventType = eventType,
            Envelope = json,
            WorkId = instance.PrimaryWorkId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
        return StepOutcome.Done(
            new JsonObject { ["signal"] = "sent", ["target"] = targetId.ToString(), ["eventType"] = eventType },
            signalCommand,
            note: $"flow signal: {eventType} -> {targetId:D}");
    }

    /// <summary>The stored manifest the trigger event names (data-scoped
    /// ref + digest, read through a kernel-local scope: the ledger is a
    /// scoped service). Null: no ref, no artifact, or a digest the stored
    /// bytes refuse (the leg fails honestly either way).</summary>
    private async Task<JsonElement?> ManifestFromContextAsync(JsonObject context, CancellationToken ct)
    {
        if (context["event"]?.AsObject() is not { } envelopeNode
            || envelopeNode["data"]?.AsObject() is not { } dataNode
            || dataNode["resultManifestRef"]?.GetValueKind() != JsonValueKind.String
            || dataNode["resultDigest"]?.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        var wireRef = dataNode["resultManifestRef"]!.GetValue<string>();
        var digest = dataNode["resultDigest"]!.GetValue<string>();
        var slash = wireRef.LastIndexOf('/');
        if (slash < 0 || !Guid.TryParse(wireRef[(slash + 1)..], out var artifactId))
        {
            return null;
        }

        using var scope = scopeFactory.CreateScope();
        var ledger = scope.ServiceProvider.GetRequiredService<IResultManifestLedger>();
        using var stored = await ledger.ReadStoredAsync(artifactId, ct);
        if (stored is null
            || ApprovalDigest.CanonicalDocument(stored.RootElement.Clone()) != digest)
        {
            return null;
        }

        return stored.RootElement.Clone();
    }

    /// <summary>The emit side effect of a declarable binding marker (the
    /// pending-PR marker law): ON CONFLICT DO NOTHING, so a redelivered
    /// flow leg is inert. Returns the root it parked (audit-visible), null
    /// when the step declares none.</summary>
    private async Task<string?> ParkBindingAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        JsonElement payload,
        JsonObject context,
        DateTime now,
        CancellationToken ct)
    {
        if (!payload.TryGetProperty("binding", out var binding))
        {
            return null;
        }

        var provider = ResolveString(binding.GetProperty("provider"), context);
        var channel = ResolveString(binding.GetProperty("channel"), context);
        var root = ResolveString(binding.GetProperty("root"), context);
        if (provider is not { Length: > 0 } || channel is not { Length: > 0 } || root is not { Length: > 0 })
        {
            return null;
        }

        // Idempotency: the event's own result digest names the marker (the
        // arm's `pr-request:<digest>` law verbatim) — a manifest revision
        // parks one marker, a redelivered leg is inert, and a genuinely new
        // revision of the same branch may park a fresh one.
        var wireDigest = context["event"]?.AsObject()["data"]?["resultDigest"]?.GetValue<string>()
                         ?? instance.InstanceId.ToString("N");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO facilitator.communication_bindings
                 (binding_id, provider, channel, root_message_id, work_id, idempotency_key, created_at)
             VALUES (gen_random_uuid(), {provider}, {channel}, {root}, {instance.PrimaryWorkId}, {"wf-binding:" + wireDigest + ":" + root}, {now})
             ON CONFLICT DO NOTHING
             """, ct);
        return root;
    }

    private async Task<StepOutcome> ExecuteFanOutAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        JsonElement payload,
        JsonObject context,
        string commandId,
        DateTime now,
        CancellationToken ct,
        bool shadow)
    {
        // Fan-out law: resolve `over` against the context, run the inline
        // emit per element with the element hung at `$item`; the per-element
        // suffix rides the leg's command id (and through it the effect-key
        // law), so element effects are individually replay-idempotent.
        var overRef = payload.GetProperty("over").GetString()!;
        if (ResolveTemplate(JsonValue.Create(overRef), context) is not JsonArray items)
        {
            return StepOutcome.LegFailed(new JsonObject { ["error"] = $"fan-out over '{overRef}': not an array" });
        }

        var inline = payload.GetProperty("step").GetProperty("emit");
        var effects = new JsonArray();
        var index = 0;
        foreach (var element in items)
        {
            context["item"] = element?.DeepClone();
            var legCommandId = $"{commandId}:{index}";
            var outcome = await ExecuteEmitAsync(db, instance, inline, context, legCommandId, now, ct, shadow);
            if (outcome.Kind != OutcomeKind.Continue)
            {
                return StepOutcome.LegFailed(new JsonObject
                {
                    ["error"] = $"fan-out element {index} failed",
                    ["elements"] = effects,
                });
            }

            effects.Add(outcome.Output?.DeepClone());
            index++;
        }

        context["item"] = null;
        // The leg's own id rides the output: element effects suffix it
        // (`<base>:<n>`), so a reader recomputes every per-element effect
        // command id from the log row alone.
        return StepOutcome.Done(
            new JsonObject { ["commandId"] = commandId, ["elements"] = effects, ["count"] = index }, commandId);
    }

    private static TimeSpan ParseDuration(string iso)
    {
        var match = DurationPattern().Match(iso);
        if (!match.Success)
        {
            return TimeSpan.FromHours(1);
        }

        return new TimeSpan(
                int.Parse(match.Groups[1].Success ? match.Groups[1].Value : "0"),
                int.Parse(match.Groups[2].Success ? match.Groups[2].Value : "0"),
                int.Parse(match.Groups[3].Success ? match.Groups[3].Value : "0"))
            + TimeSpan.FromSeconds(double.Parse(match.Groups[4].Success ? match.Groups[4].Value : "0", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<int> NextSeqAsync(FacilitatorDbContext db, Guid instanceId, CancellationToken ct)
    {
        var max = await db.WorkflowStepLog.AsNoTracking()
            .Where(l => l.InstanceId == instanceId)
            .Select(l => (int?)l.Seq)
            .MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    private static string DescribeContext(JsonObject context)
    {
        var text = context.ToJsonString();
        return text.Length > 4000 ? text[..4000] : text;
    }

    private static async Task InstanceLockAsync(
        FacilitatorDbContext db, Guid instanceId, CancellationToken ct)
    {
        _ = ct;
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))",
            [instanceId.ToString()], ct);
    }

    // ---- outcome algebra -------------------------------------------------------

    private enum OutcomeKind
    {
        Continue,
        Parked,
        LegFailed,
    }

    private sealed record StepOutcome(
        OutcomeKind Kind,
        JsonObject Output,
        string? EffectCommandId,
        string? TimelineNote,
        DateTime? Deadline = null,
        string? Branch = null,
        string? EndReason = null)
    {
        public static StepOutcome Continue(JsonObject output) => new(OutcomeKind.Continue, output, null, null);

        public static StepOutcome Done(JsonObject output, string? effectCommandId, string? note = null) =>
            new(OutcomeKind.Continue, output, effectCommandId, note);

        public static StepOutcome WithBranch(StepOutcome outcome, string branch) =>
            outcome with { Branch = branch };

        public static StepOutcome ParkedCore(JsonObject output, DateTime deadline) =>
            new(OutcomeKind.Parked, output, null, null, deadline);

        public static StepOutcome LegFailed(JsonObject output) =>
            new(OutcomeKind.LegFailed, output, null, null);
    }
}
