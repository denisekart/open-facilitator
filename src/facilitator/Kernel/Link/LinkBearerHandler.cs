using System.Security.Claims;
using System.Text.Encodings.Web;

using Facilitator.Kernel.Shared.Auth;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Facilitator.Kernel.Link;

/// <summary>
/// Hub authentication scheme "facilitator-link": one job now — validate the
/// short-lived (5-minute) kernel-signed session JWT on the negotiate and
/// upgrade request (Authorization header on negotiate AND on the WebSocket
/// upgrade; <c>?access_token=</c> is accepted only on a WebSocket upgrade —
/// our TS clients send headers, and long-polling is not used in-repo).
/// Resolves through the SAME <see cref="ILinkTokenValidator"/> seam as the
/// HTTP surface, and stamps <c>facilitator.epoch</c> / <c>facilitator.seq</c>
/// so <see cref="LinkHub"/> can fence revocation and replay without a second
/// query.
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
            new Claim(LinkHub.TeamSlugClaim, principal.TeamSlug),
            new Claim(LinkHub.TrustModeClaim, principal.TrustMode),
            new Claim(LinkHub.RoleClaim, principal.Role),
            new Claim(LinkHub.EpochClaim, principal.Epoch.ToString()),
            new Claim(LinkHub.SeqClaim, principal.Seq.ToString()),
            new Claim(LinkHub.AuthSourceClaim, principal.AuthSource),
            new Claim("facilitator.session_key_id", principal.SessionKeyId),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
