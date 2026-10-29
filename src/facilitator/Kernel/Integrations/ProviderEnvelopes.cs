using System.Text.Json.Nodes;

using Facilitator.Kernel.Work;

namespace Facilitator.Kernel.Integrations;

/// <summary>
/// Kernel-emitted envelopes for the provider plane: the work.progress note and
/// the decision-7 interim <c>work.assign</c> (the seed-lib SQL pattern —
/// capability targeting to a named link endpoint until Plan 25 routing).
/// Every envelope passes <c>EnvelopeValidator</c> at insert (Plan 02 law).
/// </summary>
public static class ProviderEnvelopes
{
    /// <summary>Interim deadline: one day (no routing negotiation yet).</summary>
    public static readonly TimeSpan AssignDeadline = TimeSpan.FromDays(1);

    /// <summary>The plan-consistent progress note (kernel source, assignmentId "kernel").</summary>
    public static JsonObject Progress(Guid workId, string note, DateTime now)
    {
        var envelope = EnvelopeFactory.WorkProgress(workId, note, causationId: null);
        envelope["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return envelope;
    }

    /// <summary>Interim work.assign envelope to <c>options.AssignEndpointId</c>:
    /// deterministic shape mirroring the seed-lib interim row — objective in,
    /// one deliverable expected, actor evidence is the kernel mention pointer.</summary>
    public static JsonObject Assign(Guid workId, string assignmentId, string objective, ProviderOptions options, DateTime now)
    {
        var objectiveRef = objective.Length <= 512 ? objective : objective[..512];
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
                ["commandId"] = $"work-assign:{workId}",
                ["workId"] = workId.ToString(),
                ["targetId"] = options.AssignTargetId,
                ["capabilityId"] = options.AssignCapabilityId,
                ["expectedContextVersion"] = 1,
                ["inputs"] = new JsonObject
                {
                    ["objective"] = objectiveRef,
                    ["sourceRefs"] = new JsonArray(),
                },
                ["expectedDeliverables"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "analysis.report",
                    ["mediaType"] = "text/markdown",
                }),
                ["actorEvidenceRef"] = $"kernel:provider-ingest:{workId}",
                ["deadline"] = (now + AssignDeadline).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["assignmentId"] = assignmentId,
            },
        };
    }
}
