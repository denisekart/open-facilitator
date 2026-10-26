using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>Outcome of verifying an untrusted JWT against the realm JWKS.</summary>
/// <param name="Payload">Verified token payload (null when invalid).</param>
/// <param name="Token">The verified token (null when invalid).</param>
/// <param name="Reason">Machine reason when invalid: malformed, issuer, wrong-audience, expired, not-yet-valid, unknown-key, signature.</param>
public readonly record struct OidcVerifyResult(JsonElement? Payload, JsonWebToken? Token, string? Reason)
{
    /// <summary>True when the token passed every check.</summary>
    public bool Valid => Payload is not null;
}

/// <summary>
/// Shared workload/OIDC JWT verifier (Plan 10): signature via realm JWKS
/// (one cached <see cref="ConfigurationManager{OpenIdConnectConfiguration}"/>
/// per issuer, refreshed per its own bounded lifetime policy), algorithms
/// pinned to <see cref="IdentityAuthentication.ValidAlgorithms"/>, issuer ∈
/// authority + trustedIssuers, audience required. Used by mode-(b) enrollment
/// and the <c>oidc:</c> evidence validator — the vendor law is OIDC-only, so
/// discovery + JWKS over plain HTTP is allowed when
/// <c>identity:oidc:requireHttps</c> is false.
/// </summary>
public sealed class OidcJwks(IOptions<IdentityOptions> identity)
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.Ordinal);
    private readonly JsonWebTokenHandler _handler = new();
    private IReadOnlyCollection<string>? _trusted;

    private IReadOnlyCollection<string> TrustedIssuers =>
        _trusted ??= [identity.Value.Oidc.Authority, .. identity.Value.Oidc.TrustedIssuers];

    /// <summary>True when the string parses as a JWT whose iss is trusted and aud contains <paramref name="audience"/>.</summary>
    /// <remarks>Claim pre-check only — no trust is derived from an unverified token; this only routes enrollment mode.</remarks>
    public bool IsOidcCandidate(string token, string audience)
    {
        if (!TryRead(token, out var jwt))
        {
            return false;
        }

        return TrustedIssuers.Contains(jwt.Issuer) && jwt.Audiences.Contains(audience, StringComparer.Ordinal);
    }

    /// <summary>Verify signature + issuer + audience + lifetime; the reason names the failed check.</summary>
    public async Task<OidcVerifyResult> ValidateAsync(string token, string audience, CancellationToken ct)
    {
        if (!TryRead(token, out var jwt))
        {
            return new OidcVerifyResult(null, null, "malformed");
        }

        if (!TrustedIssuers.Contains(jwt.Issuer))
        {
            return new OidcVerifyResult(null, null, "issuer");
        }

        if (!jwt.Audiences.Contains(audience, StringComparer.Ordinal))
        {
            return new OidcVerifyResult(null, null, "wrong-audience");
        }

        var keys = await KeysForIssuerAsync(jwt.Issuer, ct);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = TrustedIssuers,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = IdentityAuthentication.ValidAlgorithms,
            ClockSkew = TimeSpan.FromSeconds(30),
            IssuerSigningKeys = keys,
        };

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
        {
            var reason = result.Exception switch
            {
                SecurityTokenExpiredException => "expired",
                SecurityTokenNotYetValidException => "not-yet-valid",
                SecurityTokenSignatureKeyNotFoundException => "unknown-key",
                _ => "signature",
            };
            return new OidcVerifyResult(null, null, reason);
        }

        return new OidcVerifyResult(ParsePayload(jwt.EncodedPayload), jwt, null);
    }

    /// <summary>Discovery-document signing keys for an issuer (cached per issuer, refreshed per manager policy).</summary>
    private async Task<IReadOnlyCollection<SecurityKey>> KeysForIssuerAsync(string issuer, CancellationToken ct)
    {
        var manager = _managers.GetOrAdd(issuer, static (iss, ident) =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                iss.TrimEnd('/') + "/.well-known/openid-configuration",
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = ident.Oidc.RequireHttps })
            {
                AutomaticRefreshInterval = TimeSpan.FromMinutes(5),
                RefreshInterval = TimeSpan.FromSeconds(30),
            }, identity.Value);
        var config = await manager.GetConfigurationAsync(ct);
        return [.. config.SigningKeys];
    }

    private static bool TryRead(string token, out JsonWebToken jwt)
    {
        try
        {
            jwt = new JsonWebToken(token);
            return true;
        }
        catch (ArgumentException)
        {
            jwt = null!;
            return false;
        }
    }

    private static JsonElement ParsePayload(string encodedPayload)
    {
        var padded = encodedPayload.Length % 4 == 0
            ? encodedPayload
            : encodedPayload.PadRight(encodedPayload.Length + 4 - encodedPayload.Length % 4, '=');
        var json = System.Text.Encoding.UTF8.GetString(
            System.Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')));
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}

/// <summary>Payload-JSON claim readers shared by mode-(b) enrollment and evidence validation.</summary>
public static class OidcClaims
{
    /// <summary>String claim value (null when absent/non-string).</summary>
    public static string? String(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>String-array claim, tolerating the space-delimited single-string form (Keycloak <c>scope</c>).</summary>
    public static IReadOnlyList<string> ArrayOrSpaced(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var v))
        {
            return [];
        }

        if (v.ValueKind == JsonValueKind.Array)
        {
            return v.EnumerateArray().Select(static e => e.ToString()).ToArray();
        }

        return v.ValueKind == JsonValueKind.String
            ? v.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [v.ToString()];
    }

    /// <summary>True when the claim is boolean true (delegation law).</summary>
    public static bool IsTrue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
