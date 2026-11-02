using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Shared;

/// <summary>
/// RFC 9457 ProblemDetails factory; every API error carries a
/// <c>https://errors.openfacilitator.org/v1/{code}</c> type URI (locked
/// error contract).
/// </summary>
public static class ProblemErrors
{
    /// <summary>Build a typed ProblemDetails.</summary>
    public static ProblemDetails Create(string code, string title, int status, string detail) => new()
    {
        Type = $"https://errors.openfacilitator.org/v1/{code}",
        Title = title,
        Status = status,
        Detail = detail,
    };

    /// <summary>422 illegal state transition (message names the edge).</summary>
    public static ProblemDetails InvalidTransition(string edge) =>
        Create("invalid-state-transition", "Invalid state transition", StatusCodes.Status422UnprocessableEntity,
            $"transition {edge} is not a legal edge of the state machine");

    /// <summary>409 optimistic version fence failure.</summary>
    public static ProblemDetails VersionConflict(long currentVersion)
    {
        var problem = Create("version-conflict", "Version conflict", StatusCodes.Status409Conflict,
            $"expectedVersion did not match the current work version ({currentVersion})");
        problem.Extensions["currentVersion"] = currentVersion;
        return problem;
    }

    /// <summary>422 command type outside the fail-closed table.</summary>
    public static ProblemDetails UnknownCommand(string type) =>
        Create("unknown-command", "Unknown command", StatusCodes.Status422UnprocessableEntity,
            $"command type '{type}' is not part of the v1 contract");

    /// <summary>400 payload schema violation.</summary>
    public static ProblemDetails SchemaViolation(string detail) =>
        Create("schema-violation", "Schema violation", StatusCodes.Status400BadRequest, detail);

    /// <summary>400 envelope structural violation.</summary>
    public static ProblemDetails EnvelopeViolation(string detail) =>
        Create("envelope-violation", "Envelope violation", StatusCodes.Status400BadRequest, detail);

    /// <summary>413 body over the 256 KiB wire limit.</summary>
    public static ProblemDetails PayloadTooLarge(int bytes) =>
        Create("payload-too-large", "Payload too large", StatusCodes.Status413PayloadTooLarge,
            $"{bytes} bytes exceeds the 262144 byte envelope limit");

    /// <summary>404 unknown work.</summary>
    public static ProblemDetails WorkNotFound(Guid workId) =>
        Create("work-not-found", "Work not found", StatusCodes.Status404NotFound, $"no work {workId}");

    /// <summary>404 unknown or foreign-team artifact (non-disclosure: identical for both).</summary>
    public static ProblemDetails ArtifactNotFound(string artifactId) =>
        Create("artifact-not-found", "Artifact not found", StatusCodes.Status404NotFound,
            $"no artifact '{artifactId}' visible to this principal");

    /// <summary>422 uploaded bytes hash elsewhere than the declared digest; row rejected.</summary>
    public static ProblemDetails ArtifactDigestMismatch(string artifactId) =>
        Create("artifact-digest-mismatch", "Artifact digest mismatch", StatusCodes.Status422UnprocessableEntity,
            $"bytes uploaded for artifact '{artifactId}' do not hash to the declared digest");

    /// <summary>422 a terminal execution event whose stamped execution generation is not
    /// the assignment's current one — a late completion from a fenced-over generation.
    /// Fencing: the stale event is refused, never finalized.</summary>
    public static ProblemDetails StaleGeneration(int eventGeneration, int currentGeneration) =>
        Create("stale-generation", "Stale execution generation", StatusCodes.Status422UnprocessableEntity,
            $"terminal event stamped generation {eventGeneration} does not match the assignment's "
            + $"current generation {currentGeneration}; the event is fenced");

    /// <summary>422 a work-request assignment dependsOn edge the kernel cannot admit
    /// (unknown sibling reference, unknown condition, or a dependency cycle).</summary>
    public static ProblemDetails DagViolation(string detail) =>
        Create("dependency-violation", "Dependency violation", StatusCodes.Status422UnprocessableEntity, detail);
    /// <summary>422 uploaded body differs from the declared size; row rejected.</summary>
    public static ProblemDetails ArtifactSizeMismatch(string artifactId) =>
        Create("artifact-size-mismatch", "Artifact size mismatch", StatusCodes.Status422UnprocessableEntity,
            $"bytes uploaded for artifact '{artifactId}' differ from the declared size");

    /// <summary>422 an artifact.available declaration contradicts the kernel's recorded row; the row stays as recorded.</summary>
    public static ProblemDetails ArtifactDeclarationMismatch(string artifactId) =>
        Create("artifact-declaration-mismatch", "Artifact declaration mismatch", StatusCodes.Status422UnprocessableEntity,
            $"artifact '{artifactId}' re-declared with fields the kernel did not record");

    /// <summary>409 the row exists but no adapter configured here can confirm it (external or foreign-scheme row).</summary>
    public static ProblemDetails ArtifactNotConfirmable(string artifactId) =>
        Create("artifact-not-confirmable", "Artifact not confirmable", StatusCodes.Status409Conflict,
            $"artifact '{artifactId}' is not a kernel-hosted pending upload");

    /// <summary>409 the row exists and is visible but is not in pending state (spent slot).</summary>
    public static ProblemDetails ArtifactNotPending(string artifactId) =>
        Create("artifact-not-pending", "Artifact not pending", StatusCodes.Status409Conflict,
            $"artifact '{artifactId}' is not awaiting upload confirmation");

    /// <summary>422 message class the link path cannot act on (e.g. work.assign FROM a link).</summary>
    public static ProblemDetails Unsupported(string detail) =>
        Create("unsupported", "Unsupported", StatusCodes.Status422UnprocessableEntity, detail);

    /// <summary>409 another session superseded this endpoint's live session.</summary>
    public static ProblemDetails SessionSuperseded(string endpointId) =>
        Create("session-superseded", "Session superseded", StatusCodes.Status409Conflict,
            $"link session for '{endpointId}' was superseded by a newer session epoch");

    /// <summary>404 no live link_sessions row (or the lease expired).</summary>
    public static ProblemDetails SessionNotFound(string endpointId) =>
        Create("session-not-found", "Session not found", StatusCodes.Status404NotFound,
            $"no live link session for '{endpointId}' (enroll + hello/handshake first)");

    /// <summary>404 unknown deliveryId or a delivery of another endpoint.</summary>
    public static ProblemDetails DeliveryNotFound(Guid deliveryId) =>
        Create("delivery-not-found", "Delivery not found", StatusCodes.Status404NotFound,
            $"no link delivery {deliveryId} for this endpoint");

    /// <summary>404 enrollment never supplied a capability manifest.</summary>
    public static ProblemDetails CapabilitiesNotFound(string endpointId) =>
        Create("capabilities-not-found", "Capabilities not found", StatusCodes.Status404NotFound,
            $"enrollment for '{endpointId}' has not advertised a capability manifest");

    /// <summary>403 manifest ownerTeam does not equal the principal's team.</summary>
    public static ProblemDetails TeamMismatch(string team) =>
        Create("team-mismatch", "Team mismatch", StatusCodes.Status403Forbidden,
            $"ownerTeam '{team}' is not the authenticated principal's team");

    /// <summary>401 enrollment secret (Link:EnrollmentToken) missing or wrong.</summary>
    public static ProblemDetails EnrollmentSecretInvalid() =>
        Create("enrollment-secret-invalid", "Enrollment secret invalid", StatusCodes.Status401Unauthorized,
            "the enrollment token did not match Link:EnrollmentToken");

    /// <summary>403 enrollment named a team the roster (Link:Teams) lacks.</summary>
    public static ProblemDetails TeamUnknown(string team) =>
        Create("team-unknown", "Team unknown", StatusCodes.Status403Forbidden,
            $"team '{team}' is not in the configured roster (Link:Teams)");

    /// <summary>403 claimed role contradicts the binding or the gw. prefix law.</summary>
    public static ProblemDetails RoleMismatch(string detail) =>
        Create("role-mismatch", "Role mismatch", StatusCodes.Status403Forbidden, detail);

    /// <summary>401 mode-(b) OIDC enrollment token failed verification.</summary>
    public static ProblemDetails WorkloadTokenInvalid(string reason) =>
        Create("workload-token-invalid", "Workload token invalid", StatusCodes.Status401Unauthorized,
            $"the OIDC enrollment token failed verification ({reason})");

    /// <summary>403 the workload identity (azp + endpoint) has no pre-registration (Link:WorkloadIdentities).</summary>
    public static ProblemDetails WorkloadNotRegistered(string clientId) =>
        Create("workload-not-registered", "Workload not registered", StatusCodes.Status403Forbidden,
            $"client '{clientId}' has no Link:WorkloadIdentities binding for this endpoint");

    /// <summary>422 kernel-checkable evidence failed validation.</summary>
    public static ProblemDetails EvidenceInvalid(string reason) =>
        Create("evidence-invalid", "Evidence invalid", StatusCodes.Status422UnprocessableEntity,
            $"actorEvidenceRef failed validation ({reason})");

    /// <summary>422 kernel-recomputed action digest != the declared one (no ledger row).</summary>
    public static ProblemDetails ApprovalDigestMismatch(string approvalId) =>
        Create("approval-digest-mismatch", "Approval digest mismatch", StatusCodes.Status422UnprocessableEntity,
            $"approval '{approvalId}': the canonicalized action does not hash to the declared actionDigest");

    /// <summary>422 approvalId outside the ledger's Guid identity space.</summary>
    public static ProblemDetails ApprovalIdInvalid(string approvalId) =>
        Create("approval-id-invalid", "Approval id invalid", StatusCodes.Status422UnprocessableEntity,
            $"'{approvalId}' is not a GUID — the approvals ledger cannot key it");

    /// <summary>404 unknown approval (or another work's approval, on the work-scoped surface).</summary>
    public static ProblemDetails ApprovalNotFound(string approvalId) =>
        Create("approval-not-found", "Approval not found", StatusCodes.Status404NotFound,
            $"no approval '{approvalId}' in the ledger");

    /// <summary>409 approval already decided/consumed — atomic single use.</summary>
    public static ProblemDetails ApprovalAlreadyConsumed(string approvalId) =>
        Create("approval-already-consumed", "Approval already consumed", StatusCodes.Status409Conflict,
            $"approval '{approvalId}' was already decided — a replay denies even with identical command JSON");

    /// <summary>409 approval past its expiry (lazy flip applied).</summary>
    public static ProblemDetails ApprovalExpired(string approvalId) =>
        Create("approval-expired", "Approval expired", StatusCodes.Status409Conflict,
            $"approval '{approvalId}' is past its expiresAt");

    /// <summary>409 approval revoked before use.</summary>
    public static ProblemDetails ApprovalRevoked(string approvalId) =>
        Create("approval-revoked", "Approval revoked", StatusCodes.Status409Conflict,
            $"approval '{approvalId}' was revoked before use");

    /// <summary>409 respond against a still-pending approval (consume/revoke-before-decide).</summary>
    public static ProblemDetails ApprovalNotPending(string approvalId) =>
        Create("approval-not-pending", "Approval not pending", StatusCodes.Status409Conflict,
            $"approval '{approvalId}' is pending — only an approved row may be consumed");

    /// <summary>403 the responding principal does not intersect eligibleActors.</summary>
    public static ProblemDetails ApprovalActorIneligible(string approvalId) =>
        Create("approval-actor-ineligible", "Approval actor ineligible", StatusCodes.Status403Forbidden,
            $"the responding principal is not in approval '{approvalId}'s eligibleActors");

    /// <summary>403 respond arrived without a verified human identity behind it (law D4).</summary>
    public static ProblemDetails ApprovalActorUnknown() =>
        Create("approval-actor-unknown", "Approval actor unknown", StatusCodes.Status403Forbidden,
            "approval.respond requires a verified human principal (oidc:/kernel: evidence or the OIDC-authenticated REST surface)");

    /// <summary>409 HTTP-surface equivalent of the no-compatible-protocol-version goodbye.</summary>
    public static ProblemDetails NoCommonVersion() =>
        Create("no-common-version", "No common protocol version", StatusCodes.Status409Conflict,
            "no protocol version the server supports was offered");

    /// <summary>404 manifest ref unknown, unresolvable, or off-team (non-disclosure: identical for every cause).</summary>
    public static ProblemDetails ManifestNotFound(string manifestRef) =>
        Create("manifest-not-found", "Manifest not found", StatusCodes.Status404NotFound,
            $"no manifest for '{manifestRef}' visible to this principal");

    /// <summary>422 stored manifest bytes do not hash to the declared <c>resultDigest</c>.</summary>
    public static ProblemDetails ManifestDigestMismatch(string manifestId) =>
        Create("manifest-digest-mismatch", "Manifest digest mismatch", StatusCodes.Status422UnprocessableEntity,
            $"manifest '{manifestId}' bytes do not match the declared resultDigest");

    /// <summary>422 an <c>outputs[].artifactId</c> does not resolve to a verified, same-team artifact with the declared digest.</summary>
    public static ProblemDetails ManifestOutputUnresolvable(string manifestId) =>
        Create("manifest-output-unresolvable", "Manifest output unresolvable", StatusCodes.Status422UnprocessableEntity,
            $"manifest '{manifestId}' declares an output artifact the kernel cannot resolve");

    /// <summary>404 unknown or foreign-team memory entry (non-disclosure: identical for every cause, the artifact/manifest precedent).</summary>
    public static ProblemDetails MemoryNotFound(string entryId) =>
        Create("memory-not-found", "Memory entry not found", StatusCodes.Status404NotFound,
            $"no memory entry '{entryId}' visible to this principal");

    /// <summary>422 the team's daily retain budget is spent (write-amplification law).</summary>
    public static ProblemDetails MemoryQuotaExceeded(string team) =>
        Create("memory-quota-exceeded", "Memory quota exceeded", StatusCodes.Status422UnprocessableEntity,
            $"team '{team}' has spent its daily retain budget");

    /// <summary>422 a share contradicts the law (private entry) or names a team the roster lacks.</summary>
    public static ProblemDetails MemoryShareDenied(string detail) =>
        Create("memory-share-denied", "Memory share denied", StatusCodes.Status422UnprocessableEntity, detail);

    /// <summary>422 the scope selector is malformed, unresolvable, or names a foreign team's scope for a write.</summary>
    public static ProblemDetails MemoryScopeInvalid(string selector) =>
        Create("memory-scope-invalid", "Memory scope invalid", StatusCodes.Status422UnprocessableEntity,
            $"selector '{selector}' does not resolve to a scope this caller may write");

    /// <summary>400 search arrived with an empty q.</summary>
    public static ProblemDetails SearchQueryRequired() =>
        Create("search-query-required", "Search query required", StatusCodes.Status400BadRequest,
            "the q parameter must be non-empty");

    /// <summary>404 config resource outside the registered set.</summary>
    public static ProblemDetails ConfigResourceUnknown(string resource) =>
        Create("config-resource-unknown", "Config resource unknown", StatusCodes.Status404NotFound,
            $"'{resource}' is not a registered admin config resource");

    /// <summary>400 config payload violates the resource's registered shape (or a required change reason is missing).</summary>
    public static ProblemDetails ConfigInvalid(string detail) =>
        Create("config-invalid", "Config invalid", StatusCodes.Status400BadRequest, detail);

    /// <summary>400 config payload over the 16 KiB cap.</summary>
    public static ProblemDetails ConfigPayloadTooLarge(string detail) =>
        Create("config-payload-too-large", "Config payload too large", StatusCodes.Status400BadRequest, detail);

    /// <summary>428 config PATCH arrived without an If-Match header.</summary>
    public static ProblemDetails ConfigPreconditionMissing(string resource) =>
        Create("config-precondition-missing", "Config precondition missing", StatusCodes.Status428PreconditionRequired,
            $"PATCH /api/admin/config/{resource} requires an If-Match header (an ETag from GET, or '*' to force)");

    /// <summary>412 config PATCH If-Match does not match the stored rowVersion.</summary>
    public static ProblemDetails ConfigVersionConflict(string resource, long currentVersion)
    {
        var problem = Create("config-version-conflict", "Config version conflict", StatusCodes.Status412PreconditionFailed,
            $"config resource '{resource}' changed concurrently (stored rowVersion {currentVersion}) — re-read and retry");
        problem.Extensions["currentVersion"] = currentVersion;
        return problem;
    }

    /// <summary>404 unknown dead-letter row.</summary>
    public static ProblemDetails DeadLetterNotFound(Guid deadLetterId) =>
        Create("dead-letter-not-found", "Dead letter not found", StatusCodes.Status404NotFound,
            $"no dead letter {deadLetterId}");

    /// <summary>422 a dead-letter replay envelope fails the contract validator (fail-closed, no row inserted).</summary>
    public static ProblemDetails ReplayEnvelopeInvalid(string detail) =>
        Create("replay-envelope-invalid", "Replay envelope invalid", StatusCodes.Status422UnprocessableEntity, detail);

    /// <summary>400 manifest re-mark target outside the human-writable states.</summary>
    public static ProblemDetails ManifestReMarkInvalid(string detail) =>
        Create("manifest-re-mark-invalid", "Manifest re-mark invalid", StatusCodes.Status400BadRequest, detail);
}
