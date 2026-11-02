using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Facilitator.Kernel.Work;

/// <summary>
/// Builds CloudEvents 1.0 envelopes for kernel-emitted events. Every envelope
/// inserted into the outbox MUST pass <c>EnvelopeValidator</c> first (kernel
/// emits only conformant envelopes; invalid emissions fail closed at insert).
/// </summary>
public static class EnvelopeFactory
{
    /// <summary>CloudEvents source the kernel identifies itself with.</summary>
    public const string KernelSource = "urn:facilitator:kernel";

    /// <summary>Envelope type of the kernel's own progress event.</summary>
    public const string WorkProgressType = "org.facilitator.event.work.progress.v1";

    /// <summary>Dataschema $id of the work.progress payload schema.</summary>
    public const string WorkProgressSchemaId = "https://schemas.openfacilitator.org/v1/event.work.progress.v1.json";

    /// <summary>
    /// A <c>work.progress</c> CloudEvent attributed to the kernel. Includes
    /// <c>traceparent</c> only when an ambient activity exists (W3C format,
    /// which the envelope schema's pattern accepts verbatim), plus optional
    /// causation/correlation ids.
    /// </summary>
    public static JsonObject WorkProgress(Guid workId, string note, string? causationId)
    {
        var envelope = new JsonObject
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = KernelSource,
            ["type"] = WorkProgressType,
            ["subject"] = $"work/{workId}",
            ["time"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = WorkProgressSchemaId,
            ["data"] = new JsonObject
            {
                ["workId"] = workId.ToString(),
                ["assignmentId"] = "kernel",
                ["executionGeneration"] = 1,
                ["progressNote"] = note,
            },
        };

        var traceparent = Activity.Current?.Id;
        if (traceparent is not null)
        {
            envelope["traceparent"] = traceparent;
        }

        if (causationId is not null)
        {
            envelope["causationid"] = causationId;
            envelope["correlationid"] = causationId;
        }

        return envelope;
    }
}
