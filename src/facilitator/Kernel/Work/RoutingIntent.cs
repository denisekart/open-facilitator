using System.Text.Json.Nodes;

namespace Facilitator.Kernel.Work;

/// <summary>
/// A routing intent: "this Work needs an execution for this capability".
/// Every Work-creating arm (REST ingress, provider ingest, steer re-dispatch)
/// appends one as an <c>org.facilitator.event.routing.requested.v1</c> outbox
/// effect addressed to the kernel's own <c>routing</c> channel — the routing
/// funnel is the only producer of work.assign rows, so no arm addresses an
/// endpoint directly. The intent command id is deterministic per effect
/// (<c>routing:&lt;base&gt;:&lt;effectKey&gt;</c>), making a redelivered intent
/// collide on the outbox command-id ledger instead of double-assigning.
/// </summary>
/// <param name="WorkId">The Work needing an execution.</param>
/// <param name="CapabilityId">Required capability id.</param>
/// <param name="TeamSlug">Soft team context (slug) as resolved by the caller;
/// the funnel enforces cross-team exclusion against the work's owning team.</param>
/// <param name="PreferenceEndpointId">Optional preferred endpoint (honored only when allowed).</param>
/// <param name="TargetId">Envelope targetId, resolved by the caller (rule target / provider default).</param>
/// <param name="CommandIdBase">Assign command-id base (<c>work-assign</c> / <c>work-assign-coding</c> / <c>work-redispatch</c>).</param>
/// <param name="EffectKey">Effect-identity token: the assign effect row is
/// <c>&lt;base&gt;:&lt;effectKey&gt;</c> (the steer command id for a
/// re-dispatch, preserving the envelope read-back ledger).</param>
/// <param name="Objective">Assign objective text.</param>
/// <param name="SourceRefsJson">Optional JSON array → inputs.sourceRefs (provider re-hydration).</param>
/// <param name="ExtraInputsJson">Optional JSON object merged into inputs (rule inputs / accumulated steers).</param>
/// <param name="ActorEvidenceRef">Envelope actor evidence (attribution law).</param>
public sealed record RoutingIntent(
    Guid WorkId,
    string CapabilityId,
    string? TeamSlug,
    string? PreferenceEndpointId,
    string TargetId,
    string CommandIdBase,
    string EffectKey,
    string Objective,
    string? SourceRefsJson,
    string? ExtraInputsJson,
    string ActorEvidenceRef)
{
    /// <summary>Envelope type of the intent event.</summary>
    public const string Type = "org.facilitator.event.routing.requested.v1";

    /// <summary>Dataschema $id of the intent payload.</summary>
    public const string SchemaId = "https://schemas.openfacilitator.org/v1/event.routing.requested.v1.json";

    /// <summary>Recipient channel: the kernel's routing funnel.</summary>
    public const string Recipient = "routing.kernel";

    /// <summary>Command id of the work.assign effect row — the envelope
    /// read-back ledger key (<c>work-assign:*</c> / <c>work-redispatch:*</c>).</summary>
    public string EffectCommandId => $"{CommandIdBase}:{EffectKey}";

    /// <summary>Command id of the intent row itself (double idempotency with
    /// the effect row's unique command id).</summary>
    public string IntentCommandId => $"routing:{EffectCommandId}";
}

/// <summary>A caller-supplied assignment intent on <c>POST /api/works</c>:
/// the REST twin of a provider arm's contribution. The store stamps the
/// identity (deterministic per work index), team (the project's team), and
/// effect ledger; the caller carries only the semantic fields.</summary>
/// <param name="CapabilityId">Required capability id.</param>
/// <param name="Objective">Assign objective; null ⇒ the Work's objective.</param>
/// <param name="TargetId">Envelope targetId; null ⇒ <c>kernel</c>.</param>
/// <param name="PreferenceEndpointId">Optional preferred endpoint.</param>
/// <param name="ActorEvidenceRef">Envelope actor evidence; null ⇒ a
/// <c>kernel:rest-ingress:&lt;workId&gt;</c> attribution.</param>
/// <param name="Inputs">Extra inputs merged into the assign's inputs.</param>
public sealed record RoutingIntentSeed(
    string CapabilityId,
    string? Objective = null,
    string? TargetId = null,
    string? PreferenceEndpointId = null,
    string? ActorEvidenceRef = null,
    System.Text.Json.JsonElement? Inputs = null);

/// <summary>CloudEvent envelope for a <see cref="RoutingIntent"/> (kernel source;
/// validated like every kernel emission before insert).</summary>
public static class RoutingIntentEnvelope
{
    /// <summary>The intent as a conformant envelope.</summary>
    public static JsonObject Build(RoutingIntent intent, DateTime now) =>
        new()
        {
            ["specversion"] = "1.0",
            ["id"] = Guid.NewGuid().ToString("N"),
            ["source"] = EnvelopeFactory.KernelSource,
            ["type"] = RoutingIntent.Type,
            ["subject"] = $"work/{intent.WorkId}/routing/{intent.EffectCommandId}",
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["datacontenttype"] = "application/json",
            ["dataschema"] = RoutingIntent.SchemaId,
            ["data"] = Data(intent),
        };

    internal static JsonObject Data(RoutingIntent intent)
    {
        var data = new JsonObject
        {
            ["intentId"] = intent.IntentCommandId,
            ["workId"] = intent.WorkId.ToString(),
            ["capabilityId"] = intent.CapabilityId,
            ["targetId"] = intent.TargetId,
            ["commandIdBase"] = intent.CommandIdBase,
            ["effectKey"] = intent.EffectKey,
            ["objective"] = intent.Objective.Length <= 8192 ? intent.Objective : intent.Objective[..8192],
            ["actorEvidenceRef"] = intent.ActorEvidenceRef,
        };
        if (intent.TeamSlug is { Length: > 0 } team)
        {
            data["team"] = team;
        }

        if (intent.PreferenceEndpointId is { Length: > 0 } preference)
        {
            data["preferenceEndpointId"] = preference;
        }

        if (intent.SourceRefsJson is { Length: > 0 } sourceRefs)
        {
            data["referenceJson"] = sourceRefs;
        }

        if (intent.ExtraInputsJson is { Length: > 0 } extras)
        {
            data["extraInputsJson"] = extras;
        }

        return data;
    }

    /// <summary>The intent as its own pending outbox row on the
    /// <c>routing</c> channel (validated like every kernel emission). The
    /// submitting arm appends it inside its transaction; the dispatcher's
    /// routing sink owns everything after.</summary>
    public static Facilitator.Kernel.Shared.Persistence.OutboxEntity OutboxRow(
        RoutingIntent intent, DateTime now, Ofac.Protocol.Validation.EnvelopeValidator validator)
    {
        var json = Build(intent, now).ToJsonString();
        var verdict = validator.Validate(json);
        if (!verdict.Ok)
        {
            throw new InvalidOperationException($"kernel envelope rejected by the validator: {verdict.ErrorClass}: {verdict.Message}");
        }

        return new Facilitator.Kernel.Shared.Persistence.OutboxEntity
        {
            OutboxId = Guid.NewGuid(),
            CommandId = intent.IntentCommandId,
            RecipientEndpointId = RoutingIntent.Recipient,
            AggregateType = "work",
            AggregateId = intent.WorkId,
            EventType = RoutingIntent.Type,
            Envelope = json,
            WorkId = intent.WorkId,
            Status = "pending",
            AvailableAt = now,
            Attempts = 0,
            CreatedAt = now,
        };
    }
}
