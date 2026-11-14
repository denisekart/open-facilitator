using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The plane's channel message: a byte-copy of a wire event, addressed to
/// <c>workflow.kernel</c>, still envelope-valid (the copy changes no byte —
/// unlike the <c>decode.kernel</c> internal row, a trigger row passes
/// <c>EnvelopeValidator</c> exactly as its source did). Everything
/// plane-specific — which definition matched, its version, the triggering
/// row — lives in the command id, exactly
/// <c>wf-trigger:&lt;defId&gt;@&lt;ver&gt;:&lt;triggerRowId&gt;</c>, so the
/// copied <c>data</c> stays schema-clean.
/// </summary>
/// <param name="DefinitionId">Pinned definition.</param>
/// <param name="DefinitionVersion">Pinned definition version.</param>
/// <param name="TriggerRowId">The triggering outbox row (identity seed).</param>
/// <param name="Envelope">The copied envelope.</param>
/// <param name="OutboxId">This trigger row's id.</param>
public sealed record WorkflowTrigger(
    string DefinitionId,
    int DefinitionVersion,
    Guid TriggerRowId,
    JsonElement Envelope,
    Guid OutboxId,
    Guid? SubjectWork = null)
{
    /// <summary>Recipient constant (channel <c>workflow</c>).</summary>
    public const string Recipient = "workflow.kernel";

    /// <summary>Decode a delivered row; null when it is not a
    /// <c>wf-trigger:</c> row (wake rows decode through <see cref="WorkflowWake"/>).</summary>
    public static WorkflowTrigger? Decode(OutboxRow row)
    {
        if (!row.CommandId.StartsWith("wf-trigger:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = row.CommandId["wf-trigger:".Length..];
        var at = parts.IndexOf('@');
        var colon = parts.IndexOf(':');
        if (at <= 0 || colon <= at || !Guid.TryParse(parts[(colon + 1)..], out var triggerRowId))
        {
            return null;
        }

        var version = int.TryParse(parts[(at + 1)..colon], out var v) ? v : -1;
        if (version < 1)
        {
            return null;
        }

        return new WorkflowTrigger(parts[..at], version, triggerRowId, row.Envelope, row.OutboxId, row.WorkId);
    }
}

/// <summary>
/// A wake: the copy of an event that resumes a PARKED instance (a
/// <c>wait</c> or <c>approval</c> step whose correlation key the envelope
/// carries). Command id exactly <c>wf-wake:&lt;instance&gt;:&lt;sourceRowId&gt;</c>
/// — per (instance, delivery), so two instances parked on the same event
/// each get their own row, and a redelivery read-backs to the same wake.
/// Phase 6 adds the child-completion hand-off (<c>wf-wake:&lt;child&gt;:&lt;status&gt;</c>)
/// on the same prefix.
/// </summary>
public sealed record WorkflowWake(
    Guid InstanceId,
    Guid? SourceRowId,
    JsonElement Envelope,
    Guid OutboxId,
    string? CompletionStatus = null)
{
    /// <summary>Phase 6 hand-off status tokens (the terminal states a
    /// published child can carry).</summary>
    public static readonly string[] TerminalStatuses = ["completed", "failed", "cancelled"];

    /// <summary>Decode a wake row; null for anything else. The tail is
    /// either the source row id (Phase 1: an event matched a park) or a
    /// terminal status token (Phase 6: a child's completion hand-off).</summary>
    public static WorkflowWake? Decode(OutboxRow row)
    {
        if (!row.CommandId.StartsWith("wf-wake:", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = row.CommandId["wf-wake:".Length..];
        var colon = parts.IndexOf(':');
        if (colon <= 0 || !Guid.TryParse(parts[..colon], out var instanceId))
        {
            return null;
        }

        var tail = parts[(colon + 1)..];
        if (TerminalStatuses.Contains(tail, StringComparer.Ordinal))
        {
            return new WorkflowWake(instanceId, null, row.Envelope, row.OutboxId, tail);
        }

        return Guid.TryParse(tail, out var sourceRowId)
            ? new WorkflowWake(instanceId, sourceRowId, row.Envelope, row.OutboxId)
            : null;
    }
}

/// <summary>
/// A child start (Phase 6): the deterministic row an invoke step appends
/// in the parent's transaction. Command id exactly
/// <c>wf-start:&lt;childInstance&gt;</c> — the child row itself is the
/// commit proof (same transaction), so the sink only ever advances an
/// already-committed instance, and a redelivery is a read-back advance.
/// </summary>
public sealed record WorkflowStart(Guid InstanceId, JsonElement Envelope, Guid OutboxId)
{
    /// <summary>Decode a start row; null for anything else.</summary>
    public static WorkflowStart? Decode(OutboxRow row) =>
        row.CommandId.StartsWith("wf-start:", StringComparison.Ordinal)
        && Guid.TryParse(row.CommandId["wf-start:".Length..], out var instanceId)
            ? new WorkflowStart(instanceId, row.Envelope, row.OutboxId)
            : null;
}
