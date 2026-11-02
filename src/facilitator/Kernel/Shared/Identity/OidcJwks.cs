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
/// Shared workload/OIDC JWT verifier: signature via realm JWKS
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
    /// <param name="clockSkewSeconds">Lifetime-check tolerance; evidence tests pin it to 0 so a 1s-lifespan token expires deterministically.</param>
    public async Task<OidcVerifyResult> ValidateAsync(string token, string audience, CancellationToken ct, int clockSkewSeconds = 30)
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
        var result = await VerifyAsync(token, audience, keys, clockSkewSeconds);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Unfamiliar kid: the IdP may have lazily created/rotated its
            // signing key after our cached fetch (Keycloak creates the realm
            // key on FIRST token issuance, so a cold stack hits this class
            // for real). Force one refresh and retry — the same law the
            // stock JwtBearerHandler applies to unknown keys.
            RefreshIssuer(jwt.Issuer);
            keys = await KeysForIssuerAsync(jwt.Issuer, ct);
            result = await VerifyAsync(token, audience, keys, clockSkewSeconds);
        }
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

    private Task<TokenValidationResult> VerifyAsync(
        string token,
        string audience,
        IReadOnlyCollection<SecurityKey> keys,
        int clockSkewSeconds) =>
        _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = TrustedIssuers,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = IdentityAuthentication.ValidAlgorithms,
            ClockSkew = TimeSpan.FromSeconds(clockSkewSeconds),
            IssuerSigningKeys = keys,
        });

    /// <summary>Discovery-document signing keys for an issuer (cached per issuer, refreshed per manager policy).</summary>
    private async Task<IReadOnlyCollection<SecurityKey>> KeysForIssuerAsync(string issuer, CancellationToken ct)
    {
        var config = await ManagerFor(issuer).GetConfigurationAsync(ct);
        return [.. config.SigningKeys];
    }

    /// <summary>
    /// Force a re-fetch of an issuer's discovery document: replace the cached
    /// manager with a fresh one (a new manager fetches on first use, so the
    /// next keys lookup sees a rotated key without waiting the 30s refresh).
    /// </summary>
    private void RefreshIssuer(string issuer) => _managers[issuer] = NewManager(issuer, identity.Value.Oidc.RequireHttps);

    private ConfigurationManager<OpenIdConnectConfiguration> ManagerFor(string issuer) =>
        _managers.GetOrAdd(issuer, static (iss, https) => NewManager(iss, https), identity.Value.Oidc.RequireHttps);

    private static ConfigurationManager<OpenIdConnectConfiguration> NewManager(string issuer, bool requireHttps) =>
        new(
            issuer.TrimEnd('/') + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = requireHttps })
        {
            AutomaticRefreshInterval = TimeSpan.FromMinutes(5),
            RefreshInterval = TimeSpan.FromSeconds(30),
        };

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
