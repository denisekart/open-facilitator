using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Jcs.Net;

using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Facilitator.Kernel.Workflows;

/// <summary>
/// The definition-document face law: <b>JSON is canonical</b>, YAML is a
/// serialization view only. Canonical form is RFC 8785 (the pinned
/// <see cref="Jcs.Net"/> pipeline the approval/manifest digests use — one
/// canonicalization law in the kernel, not two), the digest is
/// <c>sha256:&lt;64 hex&gt;</c> over it, and the YAML face round-trips
/// byte-equal after canonicalization.
/// </summary>
public static class WorkflowDoc
{
    /// <summary>The whole-document cap (the validator enforces it before
    /// anything else; the JSON Schema cannot express pre-parse byte size).</summary>
    public const int MaxDocumentBytes = 32 * 1024;

    private static readonly ISerializer Yaml = new SerializerBuilder().Build();

    private static readonly IDeserializer YamlReader = new DeserializerBuilder().Build();

    /// <summary>Canonical (RFC 8785, sorted keys, no insignificant whitespace)
    /// text of a definition document — the form the store keeps.</summary>
    public static string Canonical(JsonElement document) =>
        Encoding.UTF8.GetString(JsonCanonicalizer.CanonicalizeToUtf8(JsonSerializer.Serialize(Normalize(document))));

    /// <summary><c>sha256:&lt;64 hex&gt;</c> over the canonical form.</summary>
    public static string Digest(JsonElement document)
    {
        var hash = SHA256.HashData(JsonCanonicalizer.CanonicalizeToUtf8(JsonSerializer.Serialize(Normalize(document))));
        return $"sha256:{Convert.ToHexStringLower(hash)}";
    }

    /// <summary>The YAML view (block style, canonical key order, stable: two
    /// renders of one document are byte-identical).</summary>
    public static string ToYaml(JsonElement document)
    {
        var graph = Normalize(document);
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        // Plain overload: the declared-type overload object-dispatches on the
        // STATIC type, which for a graph is the empty property set of `object`.
        Yaml.Serialize(writer, graph);
        return writer.ToString();
    }

    /// <summary>Parse the YAML view into a document (canonicalizing its text
    /// equals the stored canonical form).</summary>
    public static JsonElement FromYaml(string yaml)
    {
        var graph = YamlReader.Deserialize<object?>(new StringReader(yaml));
        var json = JsonSerializer.Serialize(ToGraph(graph), new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Parse a document in either face: a leading <c>{</c> means
    /// canonical JSON, anything else is the YAML view. False = neither.</summary>
    public static bool TryParse(string text, out JsonElement document)
    {
        try
        {
            document = Parse(text);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or YamlDotNet.Core.YamlException)
        {
            document = default;
            return false;
        }
    }

    /// <summary>Parse a document in either face; throws
    /// <see cref="FormatException"/> when the text is neither.</summary>
    public static JsonElement Parse(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.Length == 0)
        {
            throw new FormatException("empty workflow definition document");
        }

        if (trimmed[0] == '{')
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        return FromYaml(text);
    }

    // RFC 8785 normalization to a YAML-friendly CLR graph: ordinal-sorted
    // object keys (the serializer walks dictionaries in order, so the YAML
    // face is canonical-ordered), integers stay integers, raw number
    // spellings never leak, strings are NFC.
    private static object? Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToDictionary(p => p.Name, p => Normalize(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(Normalize).ToList(),
        JsonValueKind.String => element.GetString()?.Normalize(NormalizationForm.FormC),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var l) && element.GetRawText() == l.ToString(CultureInfo.InvariantCulture)
            ? l
            : element.GetDouble(),
        JsonValueKind.Null => null,
        _ => throw new FormatException($"unsupported JSON value kind {element.ValueKind}"),
    };

    // The deserialized YAML graph (dictionaries / lists / scalars, YamlDotNet
    // hands plain scalars back as strings) typed the way JSON reads them.
    private static object? ToGraph(object? graph) => graph switch
    {
        null => null,
        string s => Scalar(s),
        bool b => b,
        IDictionary<object, object?> dict => dict.ToDictionary(
            kvp => Scalar(Convert.ToString(kvp.Key, CultureInfo.InvariantCulture) ?? string.Empty)!,
            kvp => ToGraph(kvp.Value)),
        IList<object?> list => list.Select(ToGraph).ToList(),
        var other => throw new FormatException($"unsupported YAML node {other.GetType().Name}"),
    };

    // Plain YAML scalars arrive as strings; retype integer/float/bool/null.
    // Quoted scalars can't be distinguished after deserialization, so the
    // YAML face quotes only when canonicalization needs it (MinimizeJsonCompatibility).
    private static object? Scalar(string v)
    {
        if (v.Length == 0 || v is "null" or "~" or "Null" or "NULL")
        {
            return null;
        }

        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }

        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !StartsWithLetterish(v))
        {
            return d;
        }

        if (v is "true" or "True" or "TRUE")
        {
            return true;
        }

        if (v is "false" or "False" or "FALSE")
        {
            return false;
        }

        return v.Normalize(NormalizationForm.FormC);
    }

    // "2026-01" parses as a double in some cultures' tolerant paths and is a
    // string in JSON; anything starting with a sign/digit-dot only becomes a
    // number when fully numeric (TryParse above already guarantees that).
    private static bool StartsWithLetterish(string v) =>
        v.Any(static c => char.IsAsciiLetter(c) || c is '-' or ':');
}
