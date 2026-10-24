using System.Text.Json;

using Facilitator.Kernel.Shared.Auth;

using Microsoft.AspNetCore.Mvc;

namespace Facilitator.Kernel.Link;

/// <summary>
/// Guards every <c>/api/link/v1/*</c> request except <c>POST /enroll</c>
/// (guarded by the enrollment secret) with <see cref="ILinkTokenValidator"/>.
/// Invalid/expired/revoked link tokens ⇒ 401 ProblemDetails
/// <c>unauthorized</c> (the principal is simply unknown — no oracle for
/// whether an enrollment exists). The resolved <see cref="LinkPrincipal"/> is
/// stored in <c>HttpContext.Items</c> for the endpoints.
/// </summary>
public sealed class LinkAuthMiddleware(RequestDelegate next, ILinkTokenValidator validator)
{
    /// <summary>HttpContext.Items key holding the resolved <see cref="LinkPrincipal"/>.</summary>
    public const string PrincipalItem = "facilitator.link.principal";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Terminal handler for the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/link/v1", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Equals("/api/link/v1/enroll", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.FirstOrDefault();
        const string prefix = "Bearer ";
        var token = header is not null && header.StartsWith(prefix, StringComparison.Ordinal)
            ? header[prefix.Length..].Trim()
            : null;

        var principal = token is null ? null : await validator.ValidateAsync(token, context.RequestAborted);
        if (principal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/problem+json";
            await JsonSerializer.SerializeAsync(context.Response.Body, new ProblemDetails
            {
                Type = "https://errors.openfacilitator.org/v1/unauthorized",
                Title = "Unauthorized",
                Status = StatusCodes.Status401Unauthorized,
                Detail = "A valid 'Authorization: Bearer <linkToken>' header is required.",
            }, JsonOptions, context.RequestAborted);
            return;
        }

        context.Items[PrincipalItem] = principal;
        await next(context);
    }

    /// <summary>Extract the principal this middleware stored (throws when the guard was skipped).</summary>
    public static LinkPrincipal RequirePrincipal(HttpContext context) =>
        context.Items[PrincipalItem] as LinkPrincipal
            ?? throw new InvalidOperationException("link principal missing — LinkAuthMiddleware did not run for this request");
}
