using System.Buffers;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// JwtBearer wiring for the kernel's OIDC human plane: issuer validated
/// against <c>identity:oidc:authority</c> + <c>trustedIssuers</c>, audience
/// required, https enforced per config, algorithms pinned to RS256/ES256.
/// Validation failures surface as plain unauthenticated (the bearer challenge
/// answers 401 with the standing ProblemDetails) — a JWKS outage is no oracle.
/// </summary>
public static class IdentityJwt
{
    /// <summary>Configure one JwtBearer scheme from an <see cref="IdentityOptions"/> instance.</summary>
    public static void Configure(JwtBearerOptions options, IdentityOptions identity)
    {
        var issuers = new List<string> { identity.Oidc.Authority };
        issuers.AddRange(identity.Oidc.TrustedIssuers);

        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = identity.Oidc.RequireHttps;
        options.Authority = identity.Oidc.Authority;
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = issuers,
            ValidateAudience = true,
            ValidAudience = identity.Oidc.Audience,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = IdentityAuthentication.ValidAlgorithms,
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                // The verified payload rides in HttpContext.Items — no second
                // parse of the wire token downstream.
                if (context.SecurityToken is JsonWebToken token)
                {
                    context.HttpContext.Items[IdentityAuthentication.PayloadItem] = ToPayloadElement(token.EncodedPayload);
                }

                return Task.CompletedTask;
            },
            OnChallenge = async context =>
            {
                context.HandleResponse();
                await IdentityAuthentication.WriteProblemAsync(
                    context.HttpContext, IdentityAuthentication.Unauthorized(), context.HttpContext.RequestAborted);
            },
        };
    }

    private static JsonElement ToPayloadElement(string encodedPayload)
    {
        var padded = encodedPayload.Length % 4 == 0
            ? encodedPayload
            : encodedPayload.PadRight(encodedPayload.Length + 4 - encodedPayload.Length % 4, '=');
        var json = System.Text.Encoding.UTF8.GetString(
            System.Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
