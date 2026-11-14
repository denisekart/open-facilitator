using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Workflows;

using Microsoft.EntityFrameworkCore;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The successor fork, ONE law with two execution modes (the strangler
/// seam of the review-fork port). Before the swap the arm executed it
/// (<c>ProviderEventStore.ForkTerminalAsync</c> → <see cref="ApplyAsync"/>);
/// after the swap the workflow plane's <c>fork-work</c> step executes the
/// SAME <see cref="ApplyAsync"/> with a deterministic successor id
/// (<see cref="WorkflowIdentity.ForkWorkId"/>, a function of
/// (instance, stepId)) — the arm's <c>successor:{workId:N}</c> reply
/// command id is then reproducible from the delivery alone, and a
/// redelivered fork cannot mint a second successor. Timeline rows carry
/// actor <c>provider:&lt;p&gt;</c> on BOTH paths: the swap is
/// timeline-identical (the plane legitimately adds <c>workflow.stage</c>
/// rows on top).
/// </summary>
public static class WorkFork
{
    /// <summary>The link kind both writers use (the terminal law's name).</summary>
    public const string LinkKind = "successor";

    /// <summary>The link note both writers use.</summary>
    public const string LinkNote = "input arrived after the work closed";

    /// <summary>The review wire event the swap knob listens for.</summary>
    public const string ReviewEventType = "org.facilitator.event.provider.review.v1";

    /// <summary>What one fork execution does, fully determined by the
    /// subject work + the successor identity + the trigger event:
    /// everything the shadow report reconciles, and everything the applier
    /// writes. The reply target hangs off the call, not the plan: it comes
    /// from the binding, exactly as the arm reads it.</summary>
    public sealed record Plan(
        Guid PredecessorWorkId,
        Guid SuccessorWorkId,
        string Objective,
        string PredecessorShort,
        string SuccessorShort,
        string ReplyText,
        string ReplyCommandId,
        string LinkKind,
        string LinkNote,
        string CreatedEventDetail)
    {
        /// <summary>The step-output / would-do record.</summary>
        public JsonObject ToJson() => new()
        {
            ["predecessor"] = PredecessorWorkId.ToString(),
            ["successor"] = SuccessorWorkId.ToString(),
            ["objective"] = Objective,
            ["replyText"] = ReplyText,
            ["replyCommandId"] = ReplyCommandId,
            ["linkKind"] = LinkKind,
            ["linkNote"] = LinkNote,
        };
    }

    /// <summary>Compute the plan: successor identity, objective and reply
    /// sentences, link rows — the arm's sentences verbatim.</summary>
    public static Plan ComputePlan(WorkEntity predecessor, Guid successorId, string note)
    {
        var predecessorShort = predecessor.WorkId.ToString("N")[..8];
        var successorShort = successorId.ToString("N")[..8];
        var objective = $"Follow-up to {predecessorShort}: {note.Split('\n')[0]}";
        var replyText = $"Work {predecessorShort} is closed - this continues as work {successorShort}.";
        var createdDetail = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["objective"] = objective,
            ["successorOf"] = predecessor.WorkId.ToString(),
        });
        return new Plan(
            predecessor.WorkId,
            successorId,
            objective,
            predecessorShort,
            successorShort,
            replyText,
            $"successor:{successorId:N}",
            LinkKind,
            LinkNote,
            createdDetail);
    }

    /// <summary>The arm's target law for the fork reply: the repointed
    /// conversation's thread, keyed off the binding (tenant/subject/actor
    /// dropped — the arm builds the same record).</summary>
    public static ConversationTarget? ReplyTargetOf(CommunicationBindingEntity? binding) =>
        binding is null
            ? null
            : new ConversationTarget(
                binding.Provider, null, binding.Channel, binding.RootMessageId, binding.ParentMessageId,
                null, "thread", null);

    /// <summary>Apply the plan (the arm's law, field for field): successor
    /// work, <c>created</c> with <c>successorOf</c>, the predecessor
    /// <c>work.linked</c> + link row, the binding following the live item,
    /// and the reply naming the successor when the conversation and its
    /// endpoint exist. Every row's actor is <c>provider:&lt;p&gt;</c> so
    /// the swap is timeline-identical.</summary>
    public static async Task ApplyAsync(
        FacilitatorDbContext db,
        EnvelopeValidator envelopeValidator,
        ProviderOptions? providerOptions,
        Plan plan,
        WorkEntity predecessor,
        string provider,
        Guid? workflowInstanceId,
        DateTime now,
        CancellationToken ct)
    {
        var actor = $"provider:{provider}";
        db.Works.Add(new WorkEntity
        {
            WorkId = plan.SuccessorWorkId,
            ProjectId = predecessor.ProjectId,
            Objective = plan.Objective,
            State = "open",
            Version = 1,
            TeamId = predecessor.TeamId,
            OwnerActor = predecessor.OwnerActor,
            Origin = predecessor.Origin,
            ActiveWhere = predecessor.ActiveWhere,
            WorkflowInstanceId = workflowInstanceId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = plan.SuccessorWorkId,
            WorkVersion = 1,
            Kind = "created",
            Actor = actor,
            Detail = plan.CreatedEventDetail,
            CreatedAt = now,
        });
        db.WorkLinks.Add(new WorkLinkEntity
        {
            LinkId = Guid.NewGuid(),
            WorkId = plan.PredecessorWorkId,
            RefWorkId = plan.SuccessorWorkId,
            Kind = plan.LinkKind,
            Note = plan.LinkNote,
            CreatedAt = now,
        });
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = plan.PredecessorWorkId,
            WorkVersion = predecessor.Version,
            Kind = "work.linked",
            Actor = actor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["refWorkId"] = plan.SuccessorWorkId.ToString(),
                ["kind"] = plan.LinkKind,
                ["note"] = plan.LinkNote,
            }),
            CreatedAt = now,
        });
        await db.CommunicationBindings
            .Where(b => b.WorkId == plan.PredecessorWorkId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(b => b.WorkId, plan.SuccessorWorkId), ct);

        // Say so, in the place the input came from (name the new item) —
        // the arm's sequence: read the binding back BY SUCCESSOR after the
        // repoint; a conversation AND its endpoint must exist.
        var replyTarget = ReplyTargetOf(await db.CommunicationBindings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.WorkId == plan.SuccessorWorkId, ct));
        if (replyTarget is not null && providerOptions?.EndpointId is { Length: > 0 } endpoint)
        {
            var reply = ProviderEventStore.ReplyEnvelope(
                replyTarget, plan.ReplyText, plan.ReplyCommandId, $"work/{plan.SuccessorWorkId}", now);
            AppendValidated(db, envelopeValidator, reply, $"link.{endpoint}", plan.SuccessorWorkId, now);
        }
    }

    /// <summary>The input-side leg of the terminal law (what
    /// <c>AppendWorkInputAsync</c> wrote after forking): the
    /// <c>provider.input</c> row (auditable note), the optional media row,
    /// and the progress event back to the provider's endpoint. One
    /// implementation for both execution modes.</summary>
    public static void AddInputRows(
        FacilitatorDbContext db,
        EnvelopeValidator envelopeValidator,
        ProviderOptions? providerOptions,
        string provider,
        Guid workId,
        long workVersion,
        string note,
        JsonArray? sourceRefs,
        DateTime now)
    {
        var actor = $"provider:{provider}";
        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = workVersion,
            Kind = "provider.input",
            Actor = actor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, string> { ["note"] = note }),
            CreatedAt = now,
        });
        if (sourceRefs is { Count: > 0 })
        {
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = workVersion,
                Kind = "provider.attachment",
                Actor = actor,
                Detail = new JsonObject
                {
                    ["note"] = note,
                    ["sourceRefs"] = (JsonNode)sourceRefs.DeepClone(),
                }.ToJsonString(),
                CreatedAt = now,
            });
        }

        // The progress echo needs the provider's endpoint (the arm's
        // AppendCommand threw without options: a registered provider is an
        // ingest-gate precondition, so this only fires on a plane bug).
        if (providerOptions?.EndpointId is { Length: > 0 } endpoint)
        {
            var progress = ProviderEnvelopes.Progress(workId, Trim(note), now);
            AppendValidated(db, envelopeValidator, progress, $"link.{endpoint}", workId, now);
        }
    }

    /// <summary>The arm's note trim (progress + display law).</summary>
    public static string Trim(string text) =>
        text.Length <= 2000 ? text : text[..2000];

    private static void AppendValidated(
        FacilitatorDbContext db,
        EnvelopeValidator envelopeValidator,
        JsonObject envelope,
        string recipient,
        Guid workId,
        DateTime now)
    {
        var json = envelope.ToJsonString();
        var verdict = envelopeValidator.Validate(json);
        if (!verdict.Ok)
        {
            // Kernel-emitted envelopes must be contract-valid; fail closed
            // at insert rather than poison the outbox (the arm's law).
            throw new InvalidOperationException(
                $"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        db.Outbox.Add(new OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            CommandId = envelope["data"]?["idempotencyKey"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
            RecipientEndpointId = recipient,
            AggregateType = "work",
            AggregateId = workId,
            EventType = envelope["type"]!.GetValue<string>(),
            Envelope = json,
            WorkId = workId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        });
    }
}
