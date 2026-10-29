using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Integrations;
using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Link;

/// <summary>What the admission gate hands to the dispatch half of <see cref="LinkIngress"/>.</summary>
/// <param name="Problem">Refusal to answer (audit row already persisted when one was needed); null = admitted.</param>
/// <param name="Root">Parsed envelope, when it parsed.</param>
/// <param name="Type">Envelope <c>type</c> (empty when absent/non-string).</param>
/// <param name="IsCommand">Type is a command-class CloudEvent.</param>
/// <param name="CommandId"><c>data.commandId</c> for commands.</param>
/// <param name="Principal">The authority admitted commands act under (plan 10 audit actor).</param>
public sealed record AdmissionVerdict(ProblemDetails? Problem, JsonElement Root, string Type, bool IsCommand, string? CommandId, EffectivePrincipal? Principal = null);

/// <summary>
/// The validation prefix of link admission, shared by the hub <c>Envelope</c>
/// method and <c>POST /api/link/v1/events</c> via <see cref="LinkIngress"/>:
/// size gate → parse → receipt-class refusal → envelope validation →
/// known-command gate → assign-forbidden → evidence evaluation +
/// <c>authorization.evaluated:link</c> audit (commands only, plan 10).
/// Fail-closed refusals persist their failed audit inbox row (S:305) here;
/// an <c>oidc:</c>/<c>kernel:</c> ref that fails validation denies admission
/// (422 <c>evidence-invalid</c>), any other scheme records a non-authoritative
/// workload-only outcome, and a failed audit write on an allow path is 503
/// <c>auth-unavailable</c> — the human-plane law. Dedupe + dispatch stay in
/// <see cref="LinkIngress"/>.
/// </summary>
public sealed class AdmissionGate(
    IngressStore inbox,
    EnvelopeValidator validator,
    KernelMetrics metrics,
    EvidenceValidator evidence,
    LinkEnrollmentStore enrollments,
    AuditWriter audit,
    Facilitator.Kernel.Integrations.ProviderOptions providers)
{
    /// <summary>Screen one raw envelope under an authenticated workload principal.</summary>
    public async Task<AdmissionVerdict> ScreenAsync(string rawBody, LinkPrincipal workload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetByteCount(rawBody);
        if (bytes > RequestBodies.MaxBodyBytes)
        {
            return Refused(ProblemErrors.PayloadTooLarge(bytes));
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Refused(ProblemErrors.SchemaViolation("body is not valid JSON"));
        }

        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : string.Empty;
        if (type.StartsWith("org.facilitator.receipt.", StringComparison.Ordinal))
        {
            return Refused(ProblemErrors.EnvelopeViolation(
                "receipt-class frames travel via the Receipt method / receipt endpoint, not envelopes"));
        }

        var verdict = validator.Validate(root, bytes);
        if (!verdict.Ok)
        {
            if (verdict.ErrorClass == "unknown-command")
            {
                // §5.5: fail closed, but persist the audit row.
                await AuditAsync(root, rawBody, ct);
                return Refused(ProblemErrors.Unsupported($"command type '{type}' is outside the v1 contract"));
            }

            return Refused(verdict.ErrorClass switch
            {
                "payload-too-large" => ProblemErrors.PayloadTooLarge(bytes),
                "schema-violation" => ProblemErrors.SchemaViolation(verdict.Message ?? "schema violation"),
                _ => ProblemErrors.EnvelopeViolation(verdict.Message ?? "envelope violation"),
            });
        }

        var isCommand = type.StartsWith("org.facilitator.command.", StringComparison.Ordinal);
        if (isCommand && !KnownCommands.All.Contains(type))
        {
            await AuditAsync(root, rawBody, ct);
            return Refused(ProblemErrors.Unsupported($"command type '{type}' is outside the v1 contract"));
        }

        // assign is kernel→link only: a link posting one is a protocol error.
        if (type == "org.facilitator.command.work.assign.v1")
        {
            metrics.LinkReceived();
            return Refused(ProblemErrors.Unsupported("work.assign is kernel→link only; a link endpoint may not post it"));
        }

        var commandId = isCommand
            && root.GetProperty("data").TryGetProperty("commandId", out var cid)
            && cid.ValueKind == JsonValueKind.String
            ? cid.GetString()
            : null;
        var isProviderEvent = ProviderEventClass.IsProviderClass(type);
        if (isProviderEvent && !ProviderEventClass.SupportedBy(providers, type))
        {
            // Fail-closed (§7.1 provider path): the class is on the wire, but a
            // kernel without that provider refuses it (failed audit inbox row,
            // the S:305 precedent) instead of settling it no-effect-accepted.
            await AuditAsync(root, rawBody, ct);
            return Refused(ProblemErrors.Unsupported($"provider event type '{type}' has no registered provider"));
        }

        if (!isCommand && !isProviderEvent)
        {
            // Events/receipts carry no delegation claim: workload-only, no audit row.
            return new AdmissionVerdict(null, root, type, isCommand, commandId);
        }

        if (isProviderEvent)
        {
            return new AdmissionVerdict(null, root, type, isCommand, commandId);
        }

        // appendix §3.2: evidence evaluation + authorization.evaluated:link, commands only.
        var evidenceRef = root.GetProperty("data").TryGetProperty("actorEvidenceRef", out var er)
            && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
        var outcome = await evidence.EvaluateAsync(evidenceRef, ct);
        var accepts = outcome.State == EvidenceState.Valid
            && await enrollments.AcceptsDelegatedClaimsAsync(workload.EndpointId, ct);
        var effective = EffectivePrincipal.Derive(workload, outcome, accepts);
        var decision = outcome.State == EvidenceState.Invalid ? "deny" : "allow";
        var reason = outcome.State == EvidenceState.Invalid
            ? $"evidence-invalid:{outcome.InvalidReason}"
            : effective.Outcome;
        metrics.LinkEvidence(outcome.Scheme, reason);
        try
        {
            await audit.WriteAsync(new AuditRecord(
                Actor: $"link:{workload.EndpointId}",
                Action: "authorization.evaluated:link",
                Resource: type,
                Decision: decision,
                Reason: reason,
                EvidenceRef: evidenceRef,
                PolicyVersion: "identity-v1",
                Detail: AuditDetail(workload, outcome, effective)), ct);
        }
        catch
        {
            // Audit-fail law (human plane): an allow path that cannot record is a 503.
            return new AdmissionVerdict(IdentityAuthentication.AuthUnavailable(), root, type, isCommand, commandId);
        }

        if (outcome.State == EvidenceState.Invalid)
        {
            return new AdmissionVerdict(ProblemErrors.EvidenceInvalid(outcome.InvalidReason!), root, type, isCommand, commandId);
        }

        return new AdmissionVerdict(null, root, type, isCommand, commandId, effective);
    }

    private static string AuditDetail(LinkPrincipal workload, EvidenceOutcome outcome, EffectivePrincipal effective) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["team"] = workload.TeamSlug,
            ["role"] = workload.Role,
            ["scheme"] = outcome.Scheme,
            ["outcome"] = effective.Outcome,
            ["delegatedSubject"] = effective.Delegated?.Subject,
            ["groups"] = effective.Delegated?.Groups,
            ["endpointId"] = workload.EndpointId,
        });

    private async Task AuditAsync(JsonElement root, string rawBody, CancellationToken ct)
    {
        await inbox.InsertInboxAsync(
            Guid.NewGuid(), EnvelopeReader.SourceOf(root), EnvelopeReader.EventIdOf(root), null, rawBody, null, "failed", null, ct);
        metrics.LinkReceived();
    }

    private static AdmissionVerdict Refused(ProblemDetails problem)
    {
        var _ = problem;
        return new AdmissionVerdict(problem, default, string.Empty, false, null);
    }
}
