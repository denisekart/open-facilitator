namespace Facilitator.Kernel.Workflows;

/// <summary>
/// Deterministic identity law of the workflow plane, mirroring
/// <see cref="Facilitator.Kernel.Work.RoutingIntent"/>'s double-idempotency
/// doctrine: every append reads back its deterministic id first, the unique
/// constraint is the backstop only.
/// Namespaces are fixed and documented; names join with fixed separators
/// because idTokens contain dots (an unjoined concatenation would be
/// ambiguous, so the <c>:</c>-separated law of <c>RoutingIntent</c> applies).
/// </summary>
public static class WorkflowIdentity
{
    /// <summary>UUIDv5 namespace for trigger instances: name =
    /// <c>defId@ver&gt;triggerOutboxRowId:N</c>.</summary>
    public static readonly Guid TriggerNamespace = new("9f2b71c4-58d1-4f0a-9c63-71a0d5e2b107");

    /// <summary>UUIDv5 namespace for fork-successor works: name =
    /// <c>instance:stepId</c>.</summary>
    public static readonly Guid ForkNamespace = new("b4d0e9a2-7c33-4e58-8a1f-2d69c0b74a51");

    /// <summary>UUIDv5 namespace for invoked children: name =
    /// <c>parentInstance:stepId</c>.</summary>
    public static readonly Guid InvokeNamespace = new("6e51c8f3-14ab-4d27-b0e9-85f2a3c6d904");

    /// <summary>UUIDv5 namespace for the work-create step's subject work:
    /// name = <c>instance:stepId</c> (the fork law's shape, its own space).</summary>
    public static readonly Guid WorkCreateNamespace = new("b48f27e0-9c15-4a63-91dd-05e8c73f1a46");

    /// <summary>Trigger-row command id of an instance start: exactly
    /// <c>wf-trigger:&lt;defId&gt;@&lt;ver&gt;:&lt;triggerOutboxRowId&gt;</c>.</summary>
    public static string TriggerCommandId(string defId, int version, Guid triggerOutboxRowId) =>
        $"wf-trigger:{defId}@{version}:{triggerOutboxRowId}";

    /// <summary>Step-effect command id: exactly
    /// <c>wf:&lt;defId&gt;@&lt;ver&gt;:&lt;instance&gt;:&lt;step-id&gt;:&lt;n&gt;</c>
    /// (n = the 1-based attempt/leg counter for repeatable legs).</summary>
    public static string StepCommandId(string defId, int version, Guid instance, string stepId, int n) =>
        $"wf:{defId}@{version}:{instance:N}:{stepId}:{n}";

    /// <summary>Wake-row command id of a child completion hand-off: exactly
    /// <c>wf-wake:&lt;childInstance&gt;:&lt;status&gt;</c>.</summary>
    public static string WakeCommandId(Guid childInstance, string status) =>
        $"wf-wake:{childInstance:N}:{status}";

    /// <summary>UUIDv5 namespace for schedule ticks: name =
    /// <c>defId@ver&gt;occurrence</c> (occurrence formatted
    /// <c>yyyy-MM-ddTHH:mm:ssZ</c>). The tick envelope's identity is a pure
    /// function of (definition version, plan slot): a redelivered sweep of
    /// the same slot converges on the same envelope id, hence (by the
    /// trigger-row convergence law) the same trigger row and the same
    /// instance — exactly one start per scheduled minute.</summary>
    public static readonly Guid ScheduleNamespace = new("c7e40b58-1a96-4f2d-8e07-63b9d2af4c10");

    /// <summary>The tick envelope identity for one (definition version,
    /// occurrence) pair.</summary>
    public static Guid ScheduledEnvelopeId(string defId, int version, DateTime occurrenceUtc) =>
        Uuid5(ScheduleNamespace, $"{defId}@{version}>{occurrenceUtc:yyyy-MM-ddTHH:mm:ssZ}");

    /// <summary>UUIDv5 namespace for trigger rows: name =
    /// <c>defId@ver&gt;envelopeId</c>. The trigger row's identity is the
    /// envelope's own, per definition: an arm hook and the generic ingress
    /// hook on the SAME envelope converge on one row (PK) and therefore one
    /// instance — the plane observes each accepted event exactly once
    /// however many code paths report it.</summary>
    public static readonly Guid TriggerRowNamespace = new("9f3b71c6-2d54-4a08-b7e2-41c8d05f6b13");

    /// <summary>The trigger-row identity for one (definition, envelope)
    /// pair. Deterministic: the instance identity becomes a pure function
    /// of (defId, version, wire envelope id), derivable by receipts and
    /// tests without reading the row.</summary>
    public static Guid TriggerRowId(string defId, int version, string envelopeId) =>
        Uuid5(TriggerRowNamespace, $"{defId}@{version}>{envelopeId}");

    /// <summary>The instance identity of a trigger delivery: UUIDv5 over the
    /// trigger namespace with the fixed-separator name
    /// <c>defId@ver&gt;triggerOutboxRowId</c>.</summary>
    public static Guid InstanceIdFor(string defId, int version, Guid triggerOutboxRowId) =>
        Uuid5(TriggerNamespace, $"{defId}@{version}>{triggerOutboxRowId:N}");

    /// <summary>The deterministic successor-work identity of a fork step:
    /// UUIDv5 over the fork namespace with name <c>instance:stepId</c>. Makes
    /// the arm's <c>successor:{workId:N}</c> reply command id reproducible by
    /// the plane.</summary>
    /// <summary>The work-create step's deterministic subject work (the
    /// fork posture): a redelivered trigger cannot mint a second work.</summary>
    public static Guid WorkCreateWorkId(Guid instance, string stepId) =>
        Uuid5(WorkCreateNamespace, $"{instance:N}:{stepId}");

    public static Guid ForkWorkId(Guid instance, string stepId) =>
        Uuid5(ForkNamespace, $"{instance:N}:{stepId}");

    /// <summary>The deterministic child-instance identity of an invoke step:
    /// UUIDv5 over the invoke namespace with name <c>parentInstance:stepId</c>.
    /// A redelivered invoke re-derives the same child id and collides on the
    /// PK — exactly-once child, the outbox law verbatim.</summary>
    public static Guid ChildInstanceId(Guid parentInstance, string stepId) =>
        Uuid5(InvokeNamespace, $"{parentInstance:N}:{stepId}");

    /// <summary>The single UUIDv5 derivation (SHA-1, RFC 9562) — the BCL
    /// predates <c>Guid.CreateVersion5</c>, same construction as
    /// <see cref="Facilitator.Kernel.Work.RoutingIntent.AssignmentIdFor"/>.</summary>
    public static Guid Uuid5(Guid namespaceGuid, string name)
    {
        static byte[] Wire(Guid g)
        {
            var b = g.ToByteArray();
            return [b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]];
        }

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var hash = System.Security.Cryptography.SHA1.HashData([.. Wire(namespaceGuid), .. nameBytes]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50); // version 5
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80); // RFC 4122 variant
        var fromWire = new byte[16];
        fromWire[0] = hash[3];
        fromWire[1] = hash[2];
        fromWire[2] = hash[1];
        fromWire[3] = hash[0];
        fromWire[4] = hash[5];
        fromWire[5] = hash[4];
        fromWire[6] = hash[7];
        fromWire[7] = hash[6];
        System.Array.Copy(hash, 8, fromWire, 8, 8);
        return new Guid(fromWire);
    }
}
