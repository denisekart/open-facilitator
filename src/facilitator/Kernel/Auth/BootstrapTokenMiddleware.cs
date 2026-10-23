using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Auth;

/// <summary>Verdict of an ingress token check.</summary>
public enum TokenCheck
{
    /// <summary>Request may proceed.</summary>
    Ok,

    /// <summary>Missing/incorrect bearer token (401).</summary>
    Unauthorized,

    /// <summary>Auth not configured in a non-development environment (503, fail-closed).</summary>
    Unavailable,
}

/// <summary>
/// The single ingress-authentication seam. Plan 07 swaps the implementation
/// for OIDC token validation; no call sites change.
/// </summary>
public interface IIngressTokenValidator
{
    /// <summary>Check an <c>Authorization</c> header value.</summary>
    TokenCheck Validate(string? authorizationHeader);
}

/// <summary>
/// Bootstrap shared-token validator (interim until Keycloak lands in Plan 07).
/// Config <c>Auth:BootstrapToken</c>; unset + Development accepts the literal
/// <c>dev-token</c>; unset + non-Development fails closed (503 + startup throw).
/// </summary>
public sealed class BootstrapTokenValidator(IConfiguration configuration, IHostEnvironment environment) : IIngressTokenValidator
{
    /// <summary>Token accepted in Development when <c>Auth:BootstrapToken</c> is unset.</summary>
    public const string DevToken = "dev-token";

    private string? ConfiguredToken => configuration["Auth:BootstrapToken"];

    internal bool IsConfigured => !string.IsNullOrEmpty(ConfiguredToken) || environment.IsDevelopment();

    /// <inheritdoc />
    public TokenCheck Validate(string? authorizationHeader)
    {
        var expected = ConfiguredToken;
        if (string.IsNullOrEmpty(expected))
        {
            if (!environment.IsDevelopment())
            {
                return TokenCheck.Unavailable;
            }

            expected = DevToken;
        }

        const string prefix = "Bearer ";
        if (authorizationHeader is null || !authorizationHeader.StartsWith(prefix, StringComparison.Ordinal))
        {
            return TokenCheck.Unauthorized;
        }

        var provided = authorizationHeader[prefix.Length..].Trim();
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes)
            ? TokenCheck.Ok
            : TokenCheck.Unauthorized;
    }
}

/// <summary>
/// Guards every <c>/api/*</c> request with <see cref="IIngressTokenValidator"/>;
/// <c>/health</c> stays anonymous. 401/503 answers are RFC 9457 ProblemDetails.
/// </summary>
public sealed class BootstrapTokenMiddleware(RequestDelegate next, IIngressTokenValidator validator)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Terminal handler for the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/api/link/v1", StringComparison.OrdinalIgnoreCase))
        {
            // The link surface guards itself: LinkAuthMiddleware (bearer link
            // tokens) + the enrollment secret on POST /enroll (Plan 03).
            await next(context);
            return;
        }

        var check = validator.Validate(context.Request.Headers.Authorization.FirstOrDefault());
        switch (check)
        {
            case TokenCheck.Ok:
                await next(context);
                return;
            case TokenCheck.Unavailable:
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/problem+json";
                await JsonSerializer.SerializeAsync(context.Response.Body, new ProblemDetails
                {
                    Type = "https://errors.openfacilitator.org/v1/auth-unavailable",
                    Title = "Authentication unavailable",
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Detail = "Auth:BootstrapToken is not configured outside Development.",
                }, JsonOptions, context.RequestAborted);
                return;
            default:
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/problem+json";
                await JsonSerializer.SerializeAsync(context.Response.Body, new ProblemDetails
                {
                    Type = "https://errors.openfacilitator.org/v1/unauthorized",
                    Title = "Unauthorized",
                    Status = StatusCodes.Status401Unauthorized,
                    Detail = "A valid 'Authorization: Bearer <token>' header is required.",
                }, JsonOptions, context.RequestAborted);
                return;
        }
    }
}


/// <summary>
/// Fail-closed startup gate: a non-Development environment without
/// <c>Auth:BootstrapToken</c> must not serve traffic at all.
/// </summary>
public sealed class BootstrapAuthStartupCheck(IIngressTokenValidator validator) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (validator is BootstrapTokenValidator bootstrap && !bootstrap.IsConfigured)
        {
            throw new InvalidOperationException(
                "Auth:BootstrapToken is unset and the environment is not Development — refusing to start (fail closed).");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
