using System.Text.Json;

namespace Facilitator.Kernel.Link;

/// <summary>What a verified session JWT carries.</summary>
public sealed record SessionClaims(
    string Subject,
    string Team,
    string Role,
    string TrustMode,
    string AuthSource,
    long Epoch,
    long Seq,
    DateTime ExpiresAt);

/// <summary>
/// The link plane's only bearer: a 5-minute EdDSA JWT signed by the kernel
/// session key (hand-built compact JWS — the claim set and the
/// <c>EdDSA</c> algorithm are the contract, the serializer is not). Any
/// replica verifies it from the shared <c>admin_config</c> key row with no DB
/// read for the signature itself; the <c>epoch</c> claim is what makes
/// revocation effective inside the token's own lifetime.
/// </summary>
public static class LinkSessionJwt
{
    /// <summary>Mint a session JWT signed by the kernel's session key.</summary>
    public static string Mint(LinkSessionKey key, SessionClaims claims)
    {
        var header = LinkKeys.Base64Url(LinkKeys.Utf8(
            JsonSerializer.Serialize(new { alg = "EdDSA", typ = "at+jwt", kid = key.Kid })));
        var payload = LinkKeys.Base64Url(LinkKeys.Utf8(JsonSerializer.Serialize(new
        {
            sub = claims.Subject,
            team = claims.Team,
            role = claims.Role,
            trustMode = claims.TrustMode,
            auth_source = claims.AuthSource,
            epoch = claims.Epoch,
            seq = claims.Seq,
            iat = ToEpoch(DateTime.UtcNow),
            exp = ToEpoch(claims.ExpiresAt),
        })));
        if (!LinkKeys.TryDecodeBase64Url(key.PrivateKeySeed, out var seed) || seed.Length != 32)
        {
            throw new InvalidOperationException("link-session-key private key material is malformed");
        }

        var signature = LinkKeys.Sign(seed, System.Text.Encoding.ASCII.GetBytes(header + "." + payload));
        return header + "." + payload + "." + LinkKeys.Base64Url(signature);
    }

    /// <summary>Structural parse + signature verification against the kernel's
    /// current session public key + expiry. Epoch/fence comparison is the
    /// caller's job (it needs the enrollment row).</summary>
    public static bool TryVerify(string? token, string publicKeyBase64Url, DateTime nowUtc, out SessionClaims? claims)
    {
        claims = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            if (!LinkKeys.TryDecodeBase64Url(parts[0], out var headerBytes))
            {
                return false;
            }

            var header = JsonDocument.Parse(headerBytes).RootElement;
            if (!header.TryGetProperty("alg", out var alg) || alg.GetString() != "EdDSA")
            {
                return false;
            }

            if (!LinkKeys.TryDecodeBase64Url(parts[1], out var payloadBytes))
            {
                return false;
            }

            var payload = JsonDocument.Parse(payloadBytes).RootElement;
            var sub = Str(payload, "sub");
            var team = Str(payload, "team");
            var role = Str(payload, "role");
            var trustMode = Str(payload, "trustMode");
            var authSource = Str(payload, "auth_source");
            if (sub is null || team is null || role is null || trustMode is null || authSource is null
                || !Long(payload, "epoch", out var epoch) || !Long(payload, "seq", out var seq)
                || !Long(payload, "exp", out var exp))
            {
                return false;
            }

            var expiresAt = DateTime.UnixEpoch.AddSeconds(exp);
            if (expiresAt <= nowUtc)
            {
                return false;
            }

            var signingInput = System.Text.Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (!LinkKeys.VerifyBase64Url(publicKeyBase64Url, signingInput, parts[2]))
            {
                return false;
            }

            claims = new SessionClaims(sub, team, role, trustMode, authSource, epoch, seq, expiresAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long ToEpoch(DateTime utc) => (long)(utc - DateTime.UnixEpoch).TotalSeconds;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Long(JsonElement e, string name, out long value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var v))
        {
            return false;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out value))
        {
            return true;
        }

        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out value);
    }
}
