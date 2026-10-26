using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Work;

/// <summary>What an approval arm hands back to the ingress that invoked it.</summary>
/// <param name="Problem">Typed problem to answer with (the arms decide the code); null = handled.</param>
/// <param name="Status">Outcome status wire value when handled (recorded/decided/accepted/no-op).</param>
/// <param name="ApprovalId">The approval the arm acted on (echoed in bodies).</param>
/// <param name="EvidenceRef">Minted <c>kernel:</c> receipt for a consumed approve respond (law D5).</param>
public sealed record ApprovalArm(ProblemDetails? Problem, string Status, string? ApprovalId = null, string? EvidenceRef = null)
{
    /// <summary>True when the arm refused — the caller must NOT settle the inbox as processed.</summary>
    public bool Refused => Problem is not null;
}

/// <summary>
/// The approval-family arms shared by both ingress surfaces (Plan 11):
/// <c>approval.required</c> → record (recompute + compare the digest, law D2),
/// <c>approval.revoked</c> → mark revoked (terminal rows are a no-op),
/// <c>approval.respond</c> → decide through the ledger's conditional update
/// (atomic single use). Actor authority differs per surface and is passed in:
/// the wire-plane <see cref="CloudEventIngress"/> resolves the human identity
/// from the command's <c>actorEvidenceRef</c> via <see cref="ResolveWireActorAsync"/>
/// (no AdmissionGate on that surface); the Link plane hands over the gate's
/// already-screened delegated identity. A workload-only response (no verified
/// human) denies 403 <c>approval-actor-unknown</c> — non-repudiation (§12.1
/// step 4) needs a verified subject.
/// </summary>
public sealed class ApprovalIngress(
    IApprovalLedger ledger,
    EvidenceValidator evidence,
    AuditWriter audit)
{
    /// <summary>Event-class arm: record a required / mark a revoked approval.</summary>
    public async Task<ApprovalArm> HandleEventAsync(JsonElement root, string type, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var approvalId = data.TryGetProperty("approvalId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty
            : string.Empty;
        if (type == "org.facilitator.event.approval.required.v1")
        {
            var result = await ledger.RecordFromEventAsync(root, EnvelopeReader.SourceOf(root), ct);
            return result.Outcome switch
            {
                ApprovalRecordOutcome.DigestMismatch => new ApprovalArm(ProblemErrors.ApprovalDigestMismatch(approvalId), "failed"),
                ApprovalRecordOutcome.BadId => new ApprovalArm(ProblemErrors.ApprovalIdInvalid(approvalId), "failed"),
                ApprovalRecordOutcome.Duplicate => new ApprovalArm(null, "duplicate", approvalId),
                _ => new ApprovalArm(null, "recorded", approvalId),
            };
        }

        var revokedBy = data.TryGetProperty("revokedBy", out var rb) ? rb.GetString() ?? "unknown" : "unknown";
        var reason = data.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
        var revoked = await ledger.RevokeAsync(approvalId, revokedBy, reason, ct);
        return revoked.Outcome switch
        {
            ApprovalDecisionOutcome.NotFound => new ApprovalArm(ProblemErrors.ApprovalNotFound(approvalId), "failed"),
            _ => new ApprovalArm(null, revoked.Outcome == ApprovalDecisionOutcome.NoOp ? "no-op" : "revoked", approvalId),
        };
    }

    /// <summary>
    /// Command arm: decide a respond command under the passed human identity
    /// (null = workload-only ⇒ 403 <c>approval-actor-unknown</c>).
    /// <paramref name="mintEvidence"/> is set by the human REST plane (law D5).
    /// </summary>
    public async Task<ApprovalArm> HandleRespondAsync(
        JsonElement root,
        string? delegatedActor,
        IReadOnlyList<string>? delegatedGroups,
        string? delegatedDisplayName,
        bool mintEvidence,
        CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var approvalId = data.TryGetProperty("approvalId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty
            : string.Empty;
        var evidenceRef = data.TryGetProperty("actorEvidenceRef", out var er) && er.ValueKind == JsonValueKind.String
            ? er.GetString()
            : null;
        var decision = data.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String
            ? d.GetString() ?? "denied"
            : "denied";
        var comment = data.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        if (delegatedActor is null)
        {
            await AuditAsync(approvalId, "deny", "actor-unknown", evidenceRef, ct);
            return new ApprovalArm(ProblemErrors.ApprovalActorUnknown(), "denied", approvalId);
        }

        var result = await ledger.RespondAsync(
            approvalId, decision, comment,
            delegatedActor, delegatedGroups ?? [], delegatedDisplayName, evidenceRef,
            mintEvidence, ct);
        return result.Outcome switch
        {
            ApprovalDecisionOutcome.Decided => new ApprovalArm(null, "decided", approvalId, result.EvidenceRef),
            ApprovalDecisionOutcome.NotFound => new ApprovalArm(ProblemErrors.ApprovalNotFound(approvalId), "failed", approvalId),
            ApprovalDecisionOutcome.Expired => new ApprovalArm(ProblemErrors.ApprovalExpired(approvalId), "denied", approvalId),
            ApprovalDecisionOutcome.Revoked => new ApprovalArm(ProblemErrors.ApprovalRevoked(approvalId), "denied", approvalId),
            ApprovalDecisionOutcome.Ineligible => new ApprovalArm(ProblemErrors.ApprovalActorIneligible(approvalId), "denied", approvalId),
            _ => new ApprovalArm(ProblemErrors.ApprovalAlreadyConsumed(approvalId), "denied", approvalId),
        };
    }

    /// <summary>
    /// Resolve the human identity behind a wire respond on a surface WITHOUT
    /// the AdmissionGate (the <c>/api/ingress/events</c> plane): a Valid
    /// <c>oidc:</c>/<c>kernel:</c> evidence carries a verified subject;
    /// anything else is workload-only (null). Audits the evaluation in the
    /// <c>authorization.evaluated:approval</c> shape.
    /// </summary>
    public async Task<(string? Actor, IReadOnlyList<string> Groups)> ResolveWireActorAsync(JsonElement root, CancellationToken ct)
    {
        var evidenceRef = root.GetProperty("data").TryGetProperty("actorEvidenceRef", out var er)
            && er.ValueKind == JsonValueKind.String ? er.GetString() : null;
        var outcome = await evidence.EvaluateAsync(evidenceRef, ct);
        var approvalId = root.GetProperty("data").TryGetProperty("approvalId", out var id)
            && id.ValueKind == JsonValueKind.String ? id.GetString() ?? string.Empty : string.Empty;
        if (outcome.State != EvidenceState.Valid || outcome.DelegatedSubject is null)
        {
            return (null, []);
        }

        await audit.WriteAsync(new AuditRecord(
            Actor: outcome.DelegatedSubject,
            Action: "authorization.evaluated:approval",
            Resource: $"approval/{approvalId}",
            Decision: "allow",
            Reason: $"evidence-{outcome.Scheme}-validated",
            EvidenceRef: evidenceRef,
            PolicyVersion: "approval-v1",
            Detail: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["approvalId"] = approvalId,
                ["surface"] = "wire",
                ["scheme"] = outcome.Scheme,
            })), ct);
        return (outcome.DelegatedSubject, outcome.Groups);
    }

    private Task AuditAsync(string approvalId, string decision, string reason, string? evidenceRef, CancellationToken ct) =>
        audit.WriteAsync(new AuditRecord(
            Actor: null,
            Action: "authorization.evaluated:approval",
            Resource: $"approval/{approvalId}",
            Decision: decision,
            Reason: reason,
            EvidenceRef: evidenceRef,
            PolicyVersion: "approval-v1",
            Detail: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["approvalId"] = approvalId,
                ["surface"] = "ingress",
            })), ct);
}
