using System.Text.Json;

namespace Facilitator.Kernel.Link;

/// <summary>Why a proof was refused (each maps to an audited denial).</summary>
public enum ProofFailure
{
    /// <summary>Not a parsable detached EdDSA JWS (header/payload/signature shapes).</summary>
    Malformed,

    /// <summary>Signature does not verify under the claimed key, or the key is not the enrollment's.</summary>
    Signature,

    /// <summary><c>htm</c>/<c>htu</c> do not match the request/channel the proof is offered on.</summary>
    ChannelMismatch,

    /// <summary><c>iat</c>/<c>exp</c> outside the allowed clock skew.</summary>
    Clock,

    /// <summary>Nonce absent, expired, already consumed, or issued to another endpoint.</summary>
    Nonce,

    /// <summary><c>jti</c> already seen (replay).</summary>
    Replayed,

    /// <summary>Claimed <c>seq</c> below the stored fence.</summary>
    ReplaySuspect,

    /// <summary><c>epoch</c> claim does not equal the row's revocation epoch (revoked).</summary>
    Epoch,

    /// <summary><c>cbh</c> empty while <c>Link:RequireChannelBinding</c> is on.</summary>
    ChannelBinding,

    /// <summary>Enrollment gone or revoked.</summary>
    UnknownEndpoint,
}

/// <summary>A parsed, structurally valid proof (cryptographic + state checks follow).</summary>
/// <param name="Issuer">Endpoint identity (<c>iss</c>).</param>
/// <param name="Sub">Claimed key id (<c>sub</c>), a <c>sha256:</c> fingerprint.</param>
/// <param name="Jti">Freshness id — one use, recorded in <c>link_challenges</c>.</param>
/// <param name="Htm">Method/channel verb (<c>POST</c>, <c>WS</c>).</param>
/// <param name="Htu">Request URI or channel URI as the client sees it.</param>
/// <param name="Nonce">Kernel challenge nonce.</param>
/// <param name="Seq">Claimed replay fence.</param>
/// <param name="Epoch">Claimed revocation epoch.</param>
/// <param name="Cbh">Channel-binding hash; the empty string when the transport exposes no exporter.</param>
/// <param name="Exp"><c>exp</c> (UTC).</param>
/// <param name="IssuedAt"><c>iat</c> (UTC).</param>
public sealed record LinkProofClaims(
    string Issuer,
    string Sub,
    string Jti,
    string Htm,
    string Htu,
    string Nonce,
    long Seq,
    long Epoch,
    string Cbh,
    DateTime Exp,
    DateTime IssuedAt);

/// <summary>
/// Detached EdDSA JWS proofs of possession (RFC 8037 / RFC 7515 subset):
/// <c>{"alg":"EdDSA","typ":"ofac-proof+jwt"}</c> protected header, payload
/// <c>{iss, sub, jti, htm, htu, iat, nbf, exp, nonce, seq, epoch, cbh}</c>,
/// signature = base64url 64-byte Ed25519 over <c>b64u(header) + "." + b64u(payload)</c>.
/// Parsing is structural here; every semantic check is fail-closed at the
/// endpoint, with an audited denial for each.
/// </summary>
public static class LinkProof
{
    /// <summary>The protected header every proof carries.</summary>
    public const string HeaderType = "ofac-proof+jwt";

    /// <summary>Parse a proof string structurally (no trust decisions).</summary>
    public static bool TryParse(string? proof, out LinkProofClaims? claims, out string? detail)
    {
        claims = null;
        detail = null;
        if (string.IsNullOrWhiteSpace(proof))
        {
            detail = "proof is required";
            return false;
        }

        var parts = proof.Split('.');
        if (parts.Length != 3 || parts[0].Length == 0 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            detail = "proof must be a detached JWS: header.payload.signature";
            return false;
        }

        if (!LinkKeys.TryDecodeBase64Url(parts[2], out var signature) || signature.Length != 64)
        {
            detail = "signature must be 64 bytes";
            return false;
        }

        JsonElement header;
        JsonElement payload;
        try
        {
            if (!LinkKeys.TryDecodeBase64Url(parts[0], out var headerBytes))
            {
                detail = "header is not base64url";
                return false;
            }

            header = JsonDocument.Parse(headerBytes).RootElement;
            if (!header.TryGetProperty("alg", out var alg) || alg.GetString() != "EdDSA"
                || !header.TryGetProperty("typ", out var typ) || typ.GetString() != HeaderType)
            {
                detail = $"header must be alg=EdDSA typ={HeaderType}";
                return false;
            }

            if (!LinkKeys.TryDecodeBase64Url(parts[1], out var payloadBytes))
            {
                detail = "payload is not base64url";
                return false;
            }

            payload = JsonDocument.Parse(payloadBytes).RootElement;
        }
        catch (JsonException)
        {
            detail = "header/payload must be JSON";
            return false;
        }

        var iss = Str(payload, "iss");
        var sub = Str(payload, "sub");
        var jti = Str(payload, "jti");
        var htm = Str(payload, "htm");
        var htu = Str(payload, "htu");
        var nonce = Str(payload, "nonce");
        var cbh = Str(payload, "cbh");
        if (iss is null || sub is null || jti is null || htm is null || htu is null || nonce is null || cbh is null)
        {
            detail = "iss, sub, jti, htm, htu, nonce and cbh are required";
            return false;
        }

        if (!Long(payload, "seq", out var seq) || !Long(payload, "epoch", out var epoch)
            || !EpochSeconds(payload, "iat", out var iat) || !EpochSeconds(payload, "exp", out var exp))
        {
            detail = "seq, epoch, iat and exp are required";
            return false;
        }

        // nbf is optional; when present it must already have passed (checked by the caller with the clock).
        claims = new LinkProofClaims(iss, sub, jti, htm, htu, nonce, seq, epoch, cbh, exp, iat);
        return true;
    }

    /// <summary>Verify the signature over the compact signing input by a raw public key.</summary>
    public static bool VerifySignature(string proof, string publicKeyBase64Url)
    {
        var parts = proof.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var signingInput = System.Text.Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        return LinkKeys.VerifyBase64Url(publicKeyBase64Url, signingInput, parts[2]);
    }

    /// <summary>The rotation handshake: both proofs sign the SAME payload
    /// <c>{oldKid, newKid, nonce, seq}</c> — mutual agreement, not takeover.</summary>
    public sealed record RotationProofClaims(string OldKid, string NewKid, string Nonce, long Seq);

    /// <summary>Parse one rotation proof structurally (signature checked against the named key by the caller).</summary>
    public static bool TryParseRotationProof(string? proof, out RotationProofClaims? claims, out string? detail)
    {
        claims = null;
        detail = null;
        var parts = (proof ?? string.Empty).Split('.');
        if (parts.Length != 3)
        {
            detail = "each rotation proof must be a detached JWS";
            return false;
        }

        try
        {
            if (!LinkKeys.TryDecodeBase64Url(parts[2], out var signature) || signature.Length != 64)
            {
                detail = "signature must be 64 bytes";
                return false;
            }

            var header = JsonDocument.Parse(LinkKeys.TryDecodeBase64Url(parts[0], out var hb) ? hb : []).RootElement;
            if (!header.TryGetProperty("alg", out var alg) || alg.GetString() != "EdDSA")
            {
                detail = "rotation proofs must use alg=EdDSA";
                return false;
            }

            if (!LinkKeys.TryDecodeBase64Url(parts[1], out var pb))
            {
                detail = "payload is not base64url";
                return false;
            }

            var payload = JsonDocument.Parse(pb).RootElement;
            var oldKid = Str(payload, "oldKid");
            var newKid = Str(payload, "newKid");
            var nonce = Str(payload, "nonce");
            if (oldKid is null || newKid is null || nonce is null || !Long(payload, "seq", out var seq))
            {
                detail = "oldKid, newKid, nonce and seq are required";
                return false;
            }

            claims = new RotationProofClaims(oldKid, newKid, nonce, seq);
            return true;
        }
        catch (JsonException)
        {
            detail = "rotation proof payload must be JSON";
            return false;
        }
    }

    /// <summary>Serialize + sign a proof (used by the conformance client and tests; clients own their own implementation).</summary>
    public static string Build(byte[] seed, byte[] publicKey, LinkProofClaims claims)
    {
        var header = LinkKeys.Base64Url(LinkKeys.Utf8(
            JsonSerializer.Serialize(new { alg = "EdDSA", typ = HeaderType })));
        var payload = LinkKeys.Base64Url(LinkKeys.Utf8(JsonSerializer.Serialize(new
        {
            iss = claims.Issuer,
            sub = claims.Sub,
            jti = claims.Jti,
            htm = claims.Htm,
            htu = claims.Htu,
            iat = ToEpoch(claims.IssuedAt),
            nbf = ToEpoch(claims.IssuedAt.AddSeconds(-5)),
            exp = ToEpoch(claims.Exp),
            nonce = claims.Nonce,
            seq = claims.Seq,
            epoch = claims.Epoch,
            cbh = claims.Cbh,
        })));
        var signature = LinkKeys.Sign(seed, System.Text.Encoding.ASCII.GetBytes(header + "." + payload));
        return header + "." + payload + "." + LinkKeys.Base64Url(signature);
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

    private static bool EpochSeconds(JsonElement e, string name, out DateTime utc)
    {
        utc = default;
        if (!Long(e, name, out var seconds))
        {
            return false;
        }

        utc = DateTime.UnixEpoch.AddSeconds(seconds);
        return true;
    }
}
