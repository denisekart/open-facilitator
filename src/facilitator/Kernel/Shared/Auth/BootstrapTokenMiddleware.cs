using System.Text.Json;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Shared.Auth;

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
            // tokens) + the enrollment secret on POST /enroll.
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
