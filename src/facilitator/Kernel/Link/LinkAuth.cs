using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Facilitator.Kernel.Link;

/// <summary>Authenticated Workspace Link principal (interim; Plan 07 swaps in OIDC + principal_mappings).</summary>
/// <param name="EndpointId">Logical endpoint identity (stable across reconnects).</param>
/// <param name="TeamId">Owning team.</param>
/// <param name="TrustMode">Declared trust mode (immutable for the session, appendix §4).</param>
public sealed record LinkPrincipal(string EndpointId, Guid TeamId, string TrustMode);

/// <summary>
/// The link-authentication seam (clean-cutover point for Plan 07): Plan 03
/// validates opaque per-endpoint link tokens from <c>link_enrollments</c>;
/// Plan 07 replaces the implementation with OIDC validation — call sites
/// (middleware, hub policy) never change.
/// </summary>
public interface ILinkTokenValidator
{
    /// <summary>Resolve a bearer token to its active principal; null when unknown/expired/revoked.</summary>
    Task<LinkPrincipal?> ValidateAsync(string token, CancellationToken ct);
}

/// <summary>
/// Resolves the bootstrap enrollment secret (config <c>Link:EnrollmentToken</c>,
/// env <c>Link__EnrollmentToken</c>). Unset + Development ⇒ the literal
/// <c>enroll-dev</c>; unset + non-Development ⇒ fail closed (startup check
/// below, mirroring <c>Auth:BootstrapToken</c>).
/// </summary>
public sealed class LinkEnrollmentSecret(IConfiguration configuration, IHostEnvironment environment)
{
    /// <summary>Secret accepted in Development when <c>Link:EnrollmentToken</c> is unset.</summary>
    public const string DevSecret = "enroll-dev";

    private string? ConfiguredSecret => configuration["Link:EnrollmentToken"];

    internal bool IsConfigured => !string.IsNullOrEmpty(ConfiguredSecret) || environment.IsDevelopment();

    /// <summary>Constant-time check of a candidate enrollment secret.</summary>
    public bool Matches(string? candidate)
    {
        var expected = string.IsNullOrEmpty(ConfiguredSecret) ? DevSecret : ConfiguredSecret;
        if (candidate is null)
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(candidate);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

/// <summary>
/// Fail-closed startup gate: a non-Development environment without
/// <c>Link:EnrollmentToken</c> must not serve enrollments at all.
/// </summary>
public sealed class LinkEnrollmentStartupCheck(LinkEnrollmentSecret secret) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!secret.IsConfigured)
        {
            throw new InvalidOperationException(
                "Link:EnrollmentToken is unset and the environment is not Development — refusing to start (fail closed).");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

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

/// <summary>
/// Hub authentication scheme "facilitator-link": bearer tokens (Authorization
/// header on negotiate AND on the WebSocket upgrade; <c>?access_token=</c> is
/// accepted only as the documented WS-only fallback — our TS clients send
/// headers, spec S:624 forbids secrets in query strings for browser-side
/// code). Resolves through the SAME <see cref="ILinkTokenValidator"/> seam as
/// the HTTP surface.
/// </summary>
public sealed class LinkBearerHandler(
    ILinkTokenValidator validator,
    ILoggerFactory logger,
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>Authentication scheme name referenced by the hub attribute.</summary>
    public const string SchemeName = "facilitator-link";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        const string prefix = "Bearer ";
        var token = header is not null && header.StartsWith(prefix, StringComparison.Ordinal)
            ? header[prefix.Length..].Trim()
            : Context.WebSockets.IsWebSocketRequest
                ? Request.Query["access_token"].FirstOrDefault()
                : null;
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var principal = await validator.ValidateAsync(token, Context.RequestAborted);
        if (principal is null)
        {
            return AuthenticateResult.Fail("unknown, expired, or revoked link token");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, principal.EndpointId),
            new Claim(LinkHub.TeamClaim, principal.TeamId.ToString()),
            new Claim(LinkHub.TrustModeClaim, principal.TrustMode),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
