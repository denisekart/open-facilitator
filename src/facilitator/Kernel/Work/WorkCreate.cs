using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Persistence;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The provider-labeled Work creation, ONE law with two execution modes
/// (the strangler seam of the assign-rules port, the WorkFork posture).
/// Before the swap the issue arm executed it
/// (<c>ProviderEventStore.CreateWorkFromProviderAsync</c> →
/// <see cref="Apply"/>); after the swap the workflow plane's
/// <c>work-create</c> step executes the SAME <see cref="Apply"/> with a
/// deterministic work id (<c>WorkflowIdentity.WorkCreateWorkId</c>, a
/// function of (instance, stepId)) — a redelivered trigger cannot mint a
/// second work. The rows: the Work, its <c>created</c> timeline row, the
/// <c>provider-ingress</c> project (find-or-create, team-scoped), the
/// conversation binding (when the trigger is a conversation), the
/// <c>work.progress</c> note back to the provider endpoint, and the
/// routing intent (the funnel law verbatim — the kernel never addresses
/// an endpoint directly).
/// </summary>
public static class WorkCreate
{
    /// <summary>Project name both execution modes find-or-create under the
    /// owning team (one project per team for provider-origin work).</summary>
    public const string ProviderProjectName = ProviderEventStore.ProviderProjectName;

    /// <summary>Everything one execution writes, fully determined by the
    /// trigger + the deterministic work id. The arm computes it from the
    /// workload principal + the matched rule; the plane computes it from
    /// the step payload + the pinned event — both feed the same applier.</summary>
    public sealed record Plan(
        Guid WorkId,
        string Objective,
        Guid? TeamId,
        string? TeamSlug,
        string? Origin,
        string? OwnerActor,
        // Residence document (where the work lives, as observed).
        string Provider,
        string Channel,
        string Root,
        string Kind,
        // Conversation binding: set when the trigger conversation should
        // route to this work from now on (the arm's binding row).
        string? BindingMessageId,
        string BindingIdempotencyKey,
        // Routing intent contribution.
        string CapabilityId,
        string? PreferenceEndpointId,
        string TargetId,
        string CommandIdBase,
        string? ExtraInputsJson,
        string ActorEvidenceRef,
        // Timeline actor + progress note (the arm's `provider:<p>` posture).
        string TimelineActor,
        string ProgressNote,
        Guid? WorkflowInstanceId,
        // Provider endpoint the progress note addresses (null = the
        // kernel-log fallback: observable, consumed, never a silent drop).
        string? ProgressEndpointId)
    {
        /// <summary>The step-output / would-do record.</summary>
        public JsonObject ToJson() => new()
        {
            ["workId"] = WorkId.ToString(),
            ["capability"] = CapabilityId,
            ["targetId"] = TargetId,
            ["effectCommandId"] = EffectCommandId,
        };

        /// <summary>The assign effect row's command id (the read-back
        /// ledger key the re-dispatch arm scans).</summary>
        public string EffectCommandId => $"{CommandIdBase}:{WorkId:N}";
    }

    /// <summary>The residence document of a reference (the arm's
    /// <c>ActiveWhereOf</c> law, verbatim): where the work currently lives,
    /// as observed on the wire (never inferred).</summary>
    public static string ActiveWhereOf(string provider, string channel, string root, string kind) =>
        new JsonObject
        {
            ["provider"] = provider,
            ["channel"] = channel,
            ["root"] = root,
            ["kind"] = kind,
        }.ToJsonString();

    /// <summary>Apply the plan (the arm's law, field for field): the
    /// provider-ingress project (find-or-create when the work has a team),
    /// the Work, the <c>created</c> row, the binding, the progress note,
    /// and the routing intent row. One implementation for both execution
    /// modes; every write is a plain insert inside the caller's
    /// transaction (the plane's step, the arm's event transaction).</summary>
    public static void Apply(FacilitatorDbContext db, EnvelopeValidator envelopeValidator, Plan plan, DateTime now)
    {
        Guid? projectId = null;
        if (plan.TeamId is { } teamId)
        {
            var project = db.Projects.Local.FirstOrDefault(p => p.TeamId == teamId && p.Name == ProviderProjectName)
                ?? db.Projects.FirstOrDefault(p => p.TeamId == teamId && p.Name == ProviderProjectName);
            if (project is null)
            {
                project = new ProjectEntity
                {
                    ProjectId = Guid.NewGuid(),
                    TeamId = teamId,
                    Name = ProviderProjectName,
                    Config = "{}",
                    CreatedAt = now,
                };
                db.Projects.Add(project);
            }

            projectId = project.ProjectId;
        }

        db.Works.Add(new WorkEntity
        {
            WorkId = plan.WorkId,
            ProjectId = projectId,
            Objective = plan.Objective,
            State = "open",
            Version = 1,
            TeamId = plan.TeamId,
            OwnerActor = plan.OwnerActor,
            Origin = plan.Origin,
            ActiveWhere = ActiveWhereOf(plan.Provider, plan.Channel, plan.Root, plan.Kind),
            WorkflowInstanceId = plan.WorkflowInstanceId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = plan.WorkId,
            WorkVersion = 1,
            Kind = "created",
            Actor = plan.TimelineActor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, string> { ["objective"] = plan.Objective }),
            CreatedAt = now,
        });
        db.CommunicationBindings.Add(new CommunicationBindingEntity
        {
            BindingId = Guid.NewGuid(),
            Provider = plan.Provider,
            Channel = plan.Channel,
            RootMessageId = plan.Root,
            ParentMessageId = plan.BindingMessageId,
            WorkId = plan.WorkId,
            IdempotencyKey = plan.BindingIdempotencyKey,
            CreatedAt = now,
        });

        // Say so on the wire (the arm's progress row): work.progress back to
        // the provider's endpoint — the conversation that just got a worker.
        var progress = ProviderEnvelopes.Progress(plan.WorkId, plan.ProgressNote, now);
        var progressJson = progress.ToJsonString();
        var verdict = envelopeValidator.Validate(progressJson);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException(
                $"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            // The arm's AppendCommand posture: the data carries no
            // idempotencyKey, the row's command id is a fresh guid — the
            // envelope's own id is the honest name of the same fact.
            CommandId = progress["id"]!.GetValue<string>(),
            RecipientEndpointId = plan.ProgressEndpointId is { Length: > 0 } endpoint
                ? $"link.{endpoint}"
                : "kernel.log",
            AggregateType = "work",
            AggregateId = plan.WorkId,
            EventType = EnvelopeFactory.WorkProgressType,
            Envelope = progressJson,
            WorkId = plan.WorkId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });

        if (plan.CapabilityId.Length == 0)
        {
            // The arm's old no-capability posture: the Work and its
            // binding exist, but nothing routes (no intent without a
            // capability to route for).
            return;
        }

        var intent = new RoutingIntent(
            plan.WorkId,
            plan.CapabilityId,
            plan.TeamSlug,
            PreferenceEndpointId: plan.PreferenceEndpointId,
            TargetId: plan.TargetId,
            CommandIdBase: plan.CommandIdBase,
            EffectKey: plan.WorkId.ToString("N"),
            Objective: plan.Objective,
            SourceRefsJson: "[]",
            ExtraInputsJson: plan.ExtraInputsJson,
            ActorEvidenceRef: plan.ActorEvidenceRef);
        db.Outbox.Add(RoutingIntentEnvelope.OutboxRow(intent, now, envelopeValidator));
    }
}
