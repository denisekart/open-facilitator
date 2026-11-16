// The runner's link-plane credential: an in-memory Ed25519 identity (a
// conformance run is a throwaway endpoint, so nothing touches disk) plus the
// detached EdDSA JWS proof builder (RFC 8037 / RFC 7515 subset) for both the
// HTTP proof route and the hello-carried channel proof.
//
// The crypto, the base64url codec and the `sha256:` fingerprint come from the
// kernel's own `LinkKeys`, compiled in as source exactly like the
// `ApprovalDigest` harness above: the runner must sign with the production
// primitive, and a ProjectReference to the web app would drag EF Core into
// this harness. The proof's PAYLOAD shape is the published wire contract
// (`LinkProof.HeaderType` for the protected header, the claim names the schema
// and `protocol/openapi/link-v1.yaml` lock), and every proof is pre-flight
// checked against the kernel's reference parser before it is sent.
using System.Security.Cryptography;
using System.Text.Json;

using Facilitator.Kernel.Link;

namespace Ofac.Conformance;

/// <summary>An in-memory Ed25519 keypair: the identity one conformance
/// endpoint enrolls, never written to disk.</summary>
public sealed class LinkIdentity
{
    private readonly byte[] _seed;

    private LinkIdentity(byte[] seed, string publicKeyBase64Url, string keyId)
    {
        _seed = seed;
        PublicKeyBase64Url = publicKeyBase64Url;
        KeyId = keyId;
    }

    /// <summary>Raw public key, base64url without padding — what enroll takes.</summary>
    public string PublicKeyBase64Url { get; }

    /// <summary><c>sha256:</c> + lowercase hex of the raw key — the fingerprint the kernel stores.</summary>
    public string KeyId { get; }

    /// <summary>Ed25519-sign over the raw signing-input bytes.</summary>
    public byte[] Sign(byte[] signingInput) => LinkKeys.Sign(_seed, signingInput);

    /// <summary>Mint a fresh keypair (32 random bytes of seed).</summary>
    public static LinkIdentity Create()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var raw = LinkKeys.PublicKeyFromSeed(seed);
        return new LinkIdentity(seed, LinkKeys.Base64Url(raw), LinkKeys.KeyId(raw));
    }
}

/// <summary>Builds the detached EdDSA JWS proofs of possession the link plane
/// authenticates with.</summary>
public static class LinkProofWire
{
    /// <summary>
    /// <c>b64u(header).b64u(payload).b64u(signature)</c> with
    /// <c>{"alg":"EdDSA","typ":"ofac-proof+jwt"}</c>, payload
    /// <c>{iss, sub, jti, htm, htu, iat, nbf, exp, nonce, seq, epoch, cbh}</c>,
    /// and the signature over the ASCII bytes of <c>b64u(header) + "." + b64u(payload)</c>.
    /// <paramref name="nonce"/> is the kernel's challenge, which carries the
    /// state it was minted against (<c>&lt;epoch&gt;.&lt;seq&gt;.&lt;random&gt;</c>);
    /// the caller signs exactly that epoch and fence.
    /// </summary>
    public static string Build(
        LinkIdentity identity, string endpointId, string htm, string htu, string nonce,
        long seq, long epoch, string cbh = "", int ttlSeconds = 30)
    {
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = LinkKeys.Base64Url(LinkKeys.Utf8(
            JsonSerializer.Serialize(new { alg = "EdDSA", typ = LinkProof.HeaderType })));
        var payload = LinkKeys.Base64Url(LinkKeys.Utf8(JsonSerializer.Serialize(new
        {
            iss = endpointId,
            sub = identity.KeyId,
            jti = Guid.NewGuid().ToString("N"),
            htm,
            htu,
            iat = issuedAt,
            nbf = issuedAt - 5,
            exp = issuedAt + ttlSeconds,
            nonce,
            seq,
            epoch,
            cbh,
        })));
        var signature = LinkKeys.Base64Url(identity.Sign(
            System.Text.Encoding.ASCII.GetBytes(header + "." + payload)));
        var proof = header + "." + payload + "." + signature;

        // Pre-flight: a proof the runner hands the kernel must satisfy the
        // kernel's own reference parser and verify under its own key — a
        // client-side typo fails loudly here, not as an unexplained 401.
        if (!LinkProof.TryParse(proof, out var parsed, out var detail)
            || !LinkProof.VerifySignature(proof, identity.PublicKeyBase64Url)
            || parsed is null
            || parsed.Issuer != endpointId || parsed.Sub != identity.KeyId
            || parsed.Htm != htm || parsed.Htu != htu || parsed.Nonce != nonce
            || parsed.Seq != seq || parsed.Epoch != epoch)
        {
            throw new InvalidOperationException(
                $"self-built proof is not a valid detached proof: {detail ?? "self-check failed"}");
        }

        return proof;
    }
}
