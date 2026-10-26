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
            $"transition {edge} is not a legal edge of the §4.1 state machine");

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

    /// <summary>409 HTTP-surface equivalent of the no-compatible-protocol-version goodbye.</summary>
    public static ProblemDetails NoCommonVersion() =>
        Create("no-common-version", "No common protocol version", StatusCodes.Status409Conflict,
            "no protocol version the server supports was offered");
}
