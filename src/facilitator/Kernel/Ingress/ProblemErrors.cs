using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Ingress;

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
}
