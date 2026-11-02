using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Jcs.Net;

namespace Facilitator.Kernel.Shared;

/// <summary>
/// The kernel's approval action digest (appendix §7.1–7.2): SHA-256 over the
/// RFC 8785 canonical form of the approval record
/// <c>{"approvalId","action","eligibleActors","expiresAt","createdAt"}</c>,
/// rendered <c>sha256:&lt;64 hex&gt;</c>. <c>nonce</c> is deliberately excluded
/// (§7.3: the anti-replay nonce is ledger-only). Canonicalization is the same
/// pinned <see cref="Jcs.Net"/> pipeline the conformance harness uses, and the
/// <c>@ofac/protocol</c> <c>computeActionDigest</c> export is the byte-for-byte
/// TS mirror checked against <c>canonical/approval-record-1.sha256</c>.
/// </summary>
public static class ApprovalDigest
{
    /// <summary>UTC second-precision RFC 3339 — both languages must render timestamps identically.</summary>
    internal const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Compute the digest of the §7.2 record wrapping <paramref name="action"/> (verbatim payload).</summary>
    public static string Compute(
        string approvalId,
        JsonElement action,
        IReadOnlyList<string> eligibleActors,
        DateTime expiresAt,
        DateTime createdAt)
    {
        var record = new Dictionary<string, object?>
        {
            ["approvalId"] = approvalId,
            ["action"] = Normalize(action),
            ["eligibleActors"] = eligibleActors.ToArray(),
            ["expiresAt"] = Format(expiresAt),
            ["createdAt"] = Format(createdAt),
        };
        var text = JsonSerializer.Serialize(record);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(JsonCanonicalizer.CanonicalizeToUtf8(text))).ToLowerInvariant();
    }

    /// <summary>
    /// The Result Manifest digest law (spec §15.3): SHA-256 over the
    /// RFC 8785 canonical form of an arbitrary document, NFC-normalized with
    /// the same normalization as <see cref="Compute"/> — one law, two
    /// documents. The <c>@ofac/protocol</c> <c>computeManifestDigest</c>
    /// export is the TS mirror checked against
    /// <c>canonical/result-manifest-1.sha256</c>.
    /// </summary>
    public static string CanonicalDocument(JsonElement document)
    {
        var text = JsonSerializer.Serialize(Normalize(document));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(JsonCanonicalizer.CanonicalizeToUtf8(text))).ToLowerInvariant();
    }

    /// <summary>The canonical form (sorted keys, no insignificant whitespace,
    /// NFC) of <paramref name="document"/> as UTF-8 bytes — what a manifest
    /// upload must be byte-for-byte for <see cref="CanonicalDocument"/> to
    /// match the stored bytes.</summary>
    public static byte[] CanonicalBytes(JsonElement document) =>
        JsonCanonicalizer.CanonicalizeToUtf8(JsonSerializer.Serialize(Normalize(document)));

    /// <summary>RFC 3339 UTC second-precision rendering shared by every digest caller.</summary>
    public static string Format(DateTime value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    // Deep-copy the payload to normalized CLR scalars: source key order and
    // raw number spellings never leak (numbers become doubles — shortest
    // round-trip on both stacks; strings are NFC-normalized per §7.1, a
    // no-op for input that is already NFC).
    private static object? Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => NormObject(element),
        JsonValueKind.Array => NormArray(element),
        JsonValueKind.String => element.GetString()!.Normalize(NormalizationForm.FormC),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.TryGetInt64(out var l) ? (double)l : element.GetDouble(),
    };

    private static Dictionary<string, object?> NormObject(JsonElement element)
    {
        var map = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            map[property.Name.Normalize(NormalizationForm.FormC)] = Normalize(property.Value);
        }

        return map;
    }

    private static List<object?> NormArray(JsonElement element)
    {
        var items = new List<object?>();
        foreach (var item in element.EnumerateArray())
        {
            items.Add(Normalize(item));
        }

        return items;
    }
}
