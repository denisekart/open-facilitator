using System.Text.Json.Nodes;

using Facilitator.Kernel.Work;

namespace Facilitator.Kernel.Integrations;

/// <summary>
/// Kernel-emitted envelopes for the provider plane: the work.progress note,
/// the decision-7 interim <c>work.assign</c> (the seed-lib SQL pattern —
/// capability targeting to a named link endpoint until capability routing), and
/// the <c>work.steer</c> delivery to the owning gateway. Every
/// envelope passes <c>EnvelopeValidator</c> at insert.
/// </summary>
public static class ProviderEnvelopes
{
    /// <summary>Interim deadline: one day (no routing negotiation yet).</summary>
    public static readonly TimeSpan AssignDeadline = TimeSpan.FromDays(1);

    /// <summary>The wire-consistent progress note (kernel source, assignmentId "kernel").</summary>
    public static JsonObject Progress(Guid workId, string note, DateTime now)
    {
        var envelope = EnvelopeFactory.WorkProgress(workId, note, causationId: null);
        envelope["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return envelope;
    }

    /// <summary>work.assign envelope addressed to a resolved link endpoint:
    /// deterministic shape mirroring the e2e tier's assignEnvelope — objective
    /// in, one deliverable expected. Capability + targetId arrive resolved (a
    /// routing intent's pair); the actor evidence defaults to the kernel
    /// mention pointer, overridable by the routing intent's own attribution.</summary>
    public static JsonObject Assign(Guid workId, string assignmentId, string objective, string capabilityId, string targetId, DateTime now, JsonObject? extraInputs = null, string? commandId = null, string? actorEvidenceRef = null, int executionGeneration = 1)
    {
        var objectiveRef = objective.Length <= 512 ? objective : objective[..512];
        var inputs = new JsonObject
        {
            ["objective"] = objectiveRef,
            ["sourceRefs"] = new JsonArray(),
        };
        if (extraInputs is not null)
        {
            foreach (var property in extraInputs.AsObject())
            {
                inputs[property.Key] = property.Value?.DeepClone();
            }
        }

        return new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = "org.facilitator.command.work.assign.v1",
            ["subject"] = $"work/{workId}/assignment/{assignmentId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
            ["data"] = new JsonObject
            {
                ["commandId"] = commandId ?? $"work-assign:{workId}",
                ["workId"] = workId.ToString(),
                ["targetId"] = targetId,
                ["capabilityId"] = capabilityId,
                ["expectedContextVersion"] = 1,
                ["inputs"] = inputs,
                ["expectedDeliverables"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "analysis.report",
                    ["mediaType"] = "text/markdown",
                }),
                ["actorEvidenceRef"] = actorEvidenceRef ?? $"kernel:provider-ingest:{workId}",
                ["deadline"] = (now + AssignDeadline).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["assignmentId"] = assignmentId,
                ["executionGeneration"] = executionGeneration,
            },
        };
    }

    /// <summary>The 24(1b) steer delivery envelope: schema-shaped
    /// <c>command.work.steer.v1</c> addressed to the assignment's owning
    /// gateway, where <c>onInteraction</c> delivers it to the bound child or
    /// honestly rejects (<c>no-binding</c> / <c>child-disconnected</c>) — a
    /// rejected steer is a normal result; the timeline row already exists.</summary>
    public static JsonObject Steer(Guid workId, string assignmentId, string targetId, string directive, long expectedContextVersion, DateTime now, string commandId, int executionGeneration = 1) =>
        new()
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = "org.facilitator.command.work.steer.v1",
            ["subject"] = $"work/{workId}/assignment/{assignmentId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.work.steer.v1.json",
            ["data"] = new JsonObject
            {
                ["commandId"] = commandId,
                ["workId"] = workId.ToString(),
                ["targetId"] = targetId,
                ["actorEvidenceRef"] = $"kernel:steer:{commandId}",
                ["deadline"] = (now + AssignDeadline).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["assignmentId"] = assignmentId,
                ["directive"] = directive.Length <= 16384 ? directive : directive[..16384],
                ["expectedContextVersion"] = expectedContextVersion,
                ["executionGeneration"] = executionGeneration,
            },
        };

    /// <summary>Kernel→connector dispatch: a manifest-declared
    /// provider operation rides a plain <c>work.assign</c> to the owning
    /// gateway, where the team's policy routes the op capability to its
    /// connector unit. Opaque inputs; deliverable kind names the intent.</summary>
    public static JsonObject ConnectorCommand(
        Guid workId, string capabilityId, string targetId, string provider,
        JsonObject inputs, string deliverableKind, DateTime now, string commandId) =>
        new()
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = "org.facilitator.command.work.assign.v1",
            ["subject"] = $"work/{workId}/assignment/{Guid.NewGuid():N}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.work.assign.v1.json",
            ["data"] = new JsonObject
            {
                ["commandId"] = commandId,
                ["workId"] = workId.ToString(),
                ["targetId"] = targetId,
                ["capabilityId"] = capabilityId,
                ["expectedContextVersion"] = 1,
                ["inputs"] = inputs,
                ["expectedDeliverables"] = new JsonArray(new JsonObject
                {
                    ["kind"] = deliverableKind,
                    ["mediaType"] = "text/markdown",
                }),
                ["actorEvidenceRef"] = $"kernel:{provider}-dispatch:{workId}",
                ["deadline"] = (now + AssignDeadline).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["assignmentId"] = Guid.NewGuid().ToString("N"),
                ["executionGeneration"] = 1,
            },
        };

    /// <summary>The bounded nudge (§6.4) — an informational
    /// command to the owning gateway; it never re-runs tools and never
    /// escalates authority. Command id <c>nudge:&lt;assignmentId&gt;:&lt;generation&gt;:&lt;episode&gt;</c>
    /// makes one nudge per stall episode single-shot on the outbox ledger.</summary>
    public static JsonObject Nudge(Guid workId, string assignmentId, string targetId, int stallEpisode, int executionGeneration, DateTime now, string commandId) =>
        new()
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = "org.facilitator.command.work.nudge.v1",
            ["subject"] = $"work/{workId}/assignment/{assignmentId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = "https://schemas.openfacilitator.org/v1/command.work.nudge.v1.json",
            ["data"] = new JsonObject
            {
                ["commandId"] = commandId,
                ["workId"] = workId.ToString(),
                ["targetId"] = targetId,
                ["actorEvidenceRef"] = $"kernel:nudge:{commandId}",
                ["deadline"] = (now + AssignDeadline).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["assignmentId"] = assignmentId,
                ["stallEpisode"] = stallEpisode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["executionGeneration"] = executionGeneration,
            },
        };
}
