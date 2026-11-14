using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Persistence;
using Facilitator.Kernel.Workflows;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The successor fork, ONE law with two execution modes (the strangler
/// seam of the review-fork port). The arm executed this law since E4
/// (<c>ProviderEventStore.ForkTerminalAsync</c>); the workflow plane's
/// <c>fork-work</c> step computes the same plan from the trigger event —
/// in shadow mode the plan is only recorded, after the swap the plan is
/// applied and the arm defers. Deterministic identity (item 14): the
/// successor work id is <see cref="WorkflowIdentity.ForkWorkId"/>, a
/// function of (instance, stepId), so the reply's
/// <c>successor:{workId:N}</c> command id is reproducible by the plane and
/// a redelivered fork cannot mint a second successor.
/// </summary>
public static class WorkFork
{
    /// <summary>The link kind both writers use (the terminal law's name).</summary>
    public const string LinkKind = "successor";

    /// <summary>The link note both writers use.</summary>
    public const string LinkNote = "input arrived after the work closed";

    /// <summary>What one fork execution does, fully determined by the
    /// subject work + trigger event (no clock, no randomness): everything
    /// the shadow report reconciles against the arm's real effects.</summary>
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
        string CreatedEventDetail,
        JsonObject ReplyTarget)
    {
        /// <summary>The would-do record logged in the step output.</summary>
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

    /// <summary>Compute the plan: successor identity, objective, reply
    /// sentence and link rows — the arm's sentences verbatim.</summary>
    public static Plan ComputePlan(
        Guid instanceId,
        string stepId,
        WorkEntity predecessor,
        Guid? projectId,
        Guid? teamId,
        string? ownerActor,
        string? origin,
        string? activeWhere,
        string note,
        JsonElement triggerEnvelope)
    {
        var successorId = WorkflowIdentity.ForkWorkId(instanceId, stepId);
        var predecessorShort = predecessor.WorkId.ToString("N")[..8];
        var successorShort = successorId.ToString("N")[..8];
        var objective = $"Follow-up to {predecessorShort}: {note.Split('\n')[0]}";
        var replyText = $"Work {predecessorShort} is closed - this continues as work {successorShort}.";
        var createdDetail = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["objective"] = objective,
            ["successorOf"] = predecessor.WorkId.ToString(),
        });

        // The reply answers where the input came from: the trigger's own
        // conversation reference, projected to the conversationTarget keys
        // (the arm reads the same fields off the binding it repoints).
        var target = new JsonObject();
        if (triggerEnvelope.TryGetProperty("data", out var data)
            && data.TryGetProperty("reference", out var reference) && reference.ValueKind == JsonValueKind.Object)
        {
            Copy(reference, target, "provider");
            Copy(reference, target, "channelOrResourceId");
            Copy(reference, target, "rootThreadId");
            Copy(reference, target, "parentMessageId");
        }

        // The arm's law: the reply is a THREAD reply on the root, whatever
        // conversation kind the trigger event itself carried.
        target["conversationKind"] = "thread";

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
            createdDetail,
            target);

        static void Copy(JsonElement from, JsonObject to, string name)
        {
            if (from.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                to[name] = v.GetString();
            }
        }
    }
}
