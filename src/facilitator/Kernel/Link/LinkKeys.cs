using System.Security.Cryptography;
using System.Text;

using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Facilitator.Kernel.Link;

/// <summary>
/// The kernel's only Ed25519 surface: parse, fingerprint, verify, and the
/// kernel's own session-JWT signing. BouncyCastle provides the primitive (the
/// net10.0 ref pack ships no Ed25519 — only ML-DSA hybrids); the wire never
/// exposes that: raw 32-byte keys, <c>sha256:</c> fingerprints, <c>EdDSA</c>.
/// </summary>
public static class LinkKeys
{
    /// <summary>Decode a base64url raw Ed25519 public key (43/44 chars, exactly 32 bytes).</summary>
    public static bool TryParsePublicKey(string? base64Url, out byte[] raw)
    {
        raw = [];
        if (base64Url is null || base64Url.Length is < 43 or > 44)
        {
            return false;
        }

        try
        {
            var padded = base64Url.Replace('-', '+').Replace('_', '/').TrimEnd('=').PadRight(((base64Url.Length + 3) / 4) * 4, '=');
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length != 32)
            {
                return false;
            }

            raw = bytes;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The key identity: <c>sha256:</c> + lowercase hex of the raw key —
    /// the hash-only rule enrollment codes already follow, extended to keys.</summary>
    public static string KeyId(byte[] rawPublicKey) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(rawPublicKey)).ToLowerInvariant();

    /// <summary>Verify a 64-byte Ed25519 signature over <paramref name="signingInput"/> by a raw public key.</summary>
    public static bool Verify(byte[] rawPublicKey, byte[] signingInput, byte[] signature)
    {
        if (rawPublicKey.Length != 32 || signature.Length != 64)
        {
            return false;
        }

        try
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(rawPublicKey, 0));
            signer.BlockUpdate(signingInput, 0, signingInput.Length);
            return signer.VerifySignature(signature);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Verify a detached-JWS style signature given base64url components.</summary>
    public static bool VerifyBase64Url(string publicKeyBase64Url, byte[] signingInput, string signatureBase64Url)
    {
        if (!TryParsePublicKey(publicKeyBase64Url, out var raw) || !TryDecodeBase64Url(signatureBase64Url, out var signature))
        {
            return false;
        }

        return Verify(raw, signingInput, signature);
    }

    /// <summary>base64url (no padding) decode.</summary>
    public static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length % 4 == 1)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight(((value.Length + 3) / 4) * 4, '='));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>base64url (no padding) encode.</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Sign with a raw Ed25519 seed (kernel session key; 32 bytes).</summary>
    public static byte[] Sign(byte[] seed, byte[] signingInput)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        signer.BlockUpdate(signingInput, 0, signingInput.Length);
        return signer.GenerateSignature();
    }

    /// <summary>The raw public key belonging to a 32-byte seed.</summary>
    public static byte[] PublicKeyFromSeed(byte[] seed) =>
        new Ed25519PrivateKeyParameters(seed, 0).GeneratePublicKey().GetEncoded();

    /// <summary>utf8 bytes of a string (signing inputs are ASCII by construction).</summary>
    public static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
}
