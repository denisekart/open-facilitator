using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// The OIDC human-plane authentication seam (scheme <c>"oidc"</c>): JwtBearer
/// options factory + the <c>HttpContext.Items</c> keys where the verified
/// token payload and the resolved <see cref="FacilitatorPrincipal"/> ride.
/// The link plane (<c>LinkBearerHandler</c>) is a fully separate seam.
/// </summary>
public static class IdentityAuthentication
{
    /// <summary>Authentication scheme name.</summary>
    public const string Scheme = "oidc";

    /// <summary>HttpContext.Items key holding the verified token payload (boxed <see cref="JsonElement"/>).</summary>
    public const string PayloadItem = "facilitator.identity.payload";

    /// <summary>HttpContext.Items key holding the resolved <see cref="FacilitatorPrincipal"/>.</summary>
    public const string PrincipalItem = "facilitator.identity.principal";

    /// <summary>Pinned signing algorithms (RSA/ECDSA only — no MAC surprise from misconfigured IdPs).</summary>
    public static readonly IReadOnlyCollection<string> ValidAlgorithms = ["RS256", "ES256"];

    /// <summary>The 401 wire contract inherited from the retired bootstrap middleware (RFC 9457, camelCase).</summary>
    public static ProblemDetails Unauthorized() => new()
    {
        Type = "https://errors.openfacilitator.org/v1/unauthorized",
        Title = "Unauthorized",
        Status = StatusCodes.Status401Unauthorized,
        Detail = "A valid 'Authorization: Bearer <token>' header is required.",
    };

    /// <summary>The 503 auth-unavailable contract (audit write failure on an allow path, fail closed).</summary>
    public static ProblemDetails AuthUnavailable() => new()
    {
        Type = "https://errors.openfacilitator.org/v1/auth-unavailable",
        Title = "Authentication unavailable",
        Status = StatusCodes.Status503ServiceUnavailable,
        Detail = "The identity plane could not record the authorization decision.",
    };

    /// <summary>The 403 forbidden contract (gate binding matched, no role held).</summary>
    public static ProblemDetails Forbidden(string operationId, IReadOnlyCollection<string> requiredRoles) => new()
    {
        Type = "https://errors.openfacilitator.org/v1/forbidden",
        Title = "Forbidden",
        Status = StatusCodes.Status403Forbidden,
        Detail = $"operation '{operationId}' requires one of the roles: {string.Join(", ", requiredRoles)}",
    };

    /// <summary>Serialize a ProblemDetails answer with the platform's web (camelCase) options.</summary>
    public static Task WriteProblemAsync(HttpContext context, ProblemDetails problem, CancellationToken ct)
    {
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";
        return JsonSerializer.SerializeAsync(context.Response.Body, problem, WebJson.Options, ct);
    }

    private static class WebJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}

/// <summary>Request-scoped access to the verified principal (set by the role-gate middleware).</summary>
public static class FacilitatorPrincipalExtensions
{
    /// <summary>The gate-resolved principal, or null on unauthenticated surfaces.</summary>
    public static FacilitatorPrincipal? GetFacilitatorPrincipal(this HttpContext context) =>
        context.Items[IdentityAuthentication.PrincipalItem] as FacilitatorPrincipal;
}
