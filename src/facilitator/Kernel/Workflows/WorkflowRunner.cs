using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Facilitator.Kernel.Ai;
using Facilitator.Kernel.Notifications;
using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Work;

using Microsoft.EntityFrameworkCore;

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
    NotificationLedger notifications)
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
                var (primaryWork, projectId, teamSlug) = ResolveWorkContext(db, trigger.Envelope);
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
    /// {wake, sweeper timeout, cancel} flips the park; losers no-op.</summary>
    public async Task<bool> ResumeAsync(WorkflowWake wake, CancellationToken ct)
    {
        await using (var db = await contextFactory.CreateDbContextAsync(ct))
        {
            await db.Database.BeginTransactionAsync(ct);
            await InstanceLockAsync(db, wake.InstanceId, ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var flipped = await db.WorkflowInstances
                .Where(i => i.InstanceId == wake.InstanceId && i.Status == "waiting")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Status, "running")
                    .SetProperty(i => i.DeadlineAt, (DateTime?)null)
                    .SetProperty(i => i.UpdatedAt, now), ct);
            if (flipped == 0)
            {
                // Park already resolved (a cancel or duplicate wake raced):
                // consumed as a no-op, never requeued.
                await db.Database.CommitTransactionAsync(ct);
                return true;
            }

            await db.Database.CommitTransactionAsync(ct);
        }

        await AdvanceAsync(wake.InstanceId, wake.Envelope, wake: true, ct);
        return true;
    }

    /// <summary>Drive an instance step by step until it parks, terminates,
    /// or the iteration budget (a graph-pathology guard) is reached. Each
    /// leg is its own transaction.</summary>
    public async Task AdvanceAsync(Guid instanceId, JsonElement? envelope, bool wake, CancellationToken ct)
    {
        for (var i = 0; i < MaxAdvanceSteps; i++)
        {
            if (!await AdvanceOnceAsync(instanceId, i == 0 ? envelope : null, wake && i == 0, ct))
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
        if (instance is null || instance.Status is not ("running" or "waiting"))
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

        // A wake RESOLVES the parked step: it does not re-execute it. An
        // approval decision of anything but `approved` (denied, expired,
        // revoked) takes the FAILED edge; any other wake (a matching wait
        // event, an approved decision) continues along the completed edge.
        var outcome = wake ? WakeOutcome(envelope!.Value) : await ExecuteStepKindAsync();

        StepOutcome UnexecutableStep() => StepOutcome.LegFailed(
            new JsonObject { ["error"] = $"step kind '{kind}' is not executable by this interpreter phase" });

        Task<StepOutcome> ExecuteStepKindAsync() => kind switch
        {
            "select" => Task.FromResult(StepOutcome.Continue(SelectProject(payload, context))),
            "emit" => ExecuteEmitAsync(db, instance, payload, context, commandId, now, ct),
            "decide" => ExecuteDecideAsync(payload, context, ct),
            "wait" => Task.FromResult(ParkOutcome(context, payload.GetProperty("correlate"), "wait",
                now + ParseDuration(payload.GetProperty("timeout").GetString()!))),
            "timer" => Task.FromResult(new StepOutcome(OutcomeKind.Parked,
                new JsonObject { ["parked"] = "timer" }, null, null,
                Deadline: now + ParseDuration(payload.GetProperty("for").GetString()!))),
            "approval" => ExecuteApprovalAsync(db, instance, stepId, payload, context, now, ct),
            "escalate" => ExecuteEscalateAsync(db, instance, payload, context, ct),
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
        var shadow = definition.Shadow;
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
                        await ProjectOutputAsync(db, instance, doc.RootElement, now);
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
            // executor, so re-read.
            keepGoing = instance.Status == "running";
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
        else if (timedOut)
        {
            target = FailedEdgeId(steps, stepId); // approval expiry: the failed edge
        }

        instance.UpdatedAt = now;
        instance.DeadlineAt = null;
        if (target is null or { Length: 0 } or "end")
        {
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
        CancellationToken ct)
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

            var seq = commandId[(commandId.LastIndexOf(':') + 1)..];
            var intent = new RoutingIntent(
                routedWork,
                capability.GetString()!,
                work.Team,
                PreferenceEndpointId: null,
                TargetId: ResolveString(payload.TryGetProperty("to", out var toRef) ? toRef : default, context)
                           ?? $"{instance.DefId}-flow",
                CommandIdBase: $"wf-{instance.InstanceId:N}",
                EffectKey: $"{stepSegment(instance.CurrentStep)}:{seq}",
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

            db.Outbox.Add(intentRow);
            Activity.Current?.SetTag("ofac.outbox.id", intentRow.OutboxId.ToString());
            Activity.Current?.SetTag("ofac.outbox.command_id", intentRow.CommandId);
            Activity.Current?.SetTag("ofac.workflow.correlation_id", instance.InstanceId.ToString("N"));
            return StepOutcome.Done(
                new JsonObject { ["effect"] = "routing-intent", ["commandId"] = intent.EffectCommandId },
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
            data["target"] = ProjectTarget(target);
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
            var hit = guard.TryGetProperty("equals", out var equals)
                ? equals.ValueKind == JsonValueKind.Null
                    ? actual is null || actual.Value.ValueKind == JsonValueKind.Null
                    : actual is not null && WorkflowMatcher.Matches(equals, actual)
                : actual is not null
                    && guard.GetProperty("in").EnumerateArray().Any(term => WorkflowMatcher.Matches(term, actual));
            var output = new JsonObject { ["guard"] = field, ["hit"] = hit };
            return hit
                ? StepOutcome.WithBranch(StepOutcome.Continue(output), FirstBranch(payload))
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

    private async Task<StepOutcome> ExecuteApprovalAsync(
        FacilitatorDbContext db,
        WorkflowInstanceEntity instance,
        string stepId,
        JsonElement payload,
        JsonObject context,
        DateTime now,
        CancellationToken ct)
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
        FacilitatorDbContext db, WorkflowInstanceEntity instance, JsonElement payload, JsonObject context, CancellationToken ct)
    {
        var text = ResolveString(payload.GetProperty("message"), context) ?? payload.GetProperty("message").GetString()!;
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

    private static string RecipientFor(string commandType, JsonObject data)
    {
        // Communication commands address the provider's link endpoint
        // (the channel prefix is the dispatcher's routing key); work
        // commands ride the kernel work plane.
        // Phase 1: every effect rides the kernel log channel — observable,
        // consumed, never a silent drop. Endpoint-addressed delivery of
        // plane effects lands with the review-fork swap (Phase 3).
        _ = (commandType, data);
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

    private static JsonNode ProjectTarget(JsonNode? candidate)
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
        _ = ct;
        await ProjectOutputAsync(db, instance, definitionRoot, now);
        Terminate(db, instance, "completed", null, now);
    }

    private Task ProjectOutputAsync(
        FacilitatorDbContext db, WorkflowInstanceEntity instance, JsonElement definitionRoot, DateTime now)
    {
        _ = now;
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
        return Task.CompletedTask;
    }

    private static void Terminate(
        FacilitatorDbContext db, WorkflowInstanceEntity instance, string status, string? endReason, DateTime now)
    {
        _ = db;
        instance.Status = status;
        instance.CurrentStep = null;
        instance.DeadlineAt = null;
        instance.EndReason = endReason;
        instance.UpdatedAt = now;
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

        return context;
    }

    /// <summary>Template law: a string starting with <c>$</c> is a dotted
    /// path over the context document (<c>$event.data.x</c>,
    /// <c>$steps.&lt;id&gt;.output.&lt;path&gt;</c>, <c>$work.&lt;field&gt;</c>,
    /// <c>$input.&lt;name&gt;</c>); an unresolved ref stays the literal (visible,
    /// debuggable); anything else is a literal.</summary>
    private static JsonNode? ResolveTemplate(JsonNode? value, JsonObject context)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.StartsWith('$'))
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
        var decision = wakeEnvelope.TryGetProperty("data", out var data)
                       && data.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;
        return decision is not null && decision != "approved"
            ? new StepOutcome(OutcomeKind.LegFailed, output, null, null, EndReason: "step-failed")
            : StepOutcome.Continue(output);
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

    private static JsonObject SelectProject(JsonElement payload, JsonObject context)
    {
        var project = new JsonObject();
        var document = JsonSerializer.SerializeToElement(context);
        foreach (var field in payload.GetProperty("project").EnumerateObject())
        {
            var found = WorkflowMatcher.ResolvePath(document, field.Value.GetString()!);
            project[field.Name] = found is null ? null : JsonNode.Parse(found.Value.GetRawText());
        }

        return project;
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
            new(OutcomeKind.LegFailed, output, null, null, EndReason: "step-failed");
    }
}
