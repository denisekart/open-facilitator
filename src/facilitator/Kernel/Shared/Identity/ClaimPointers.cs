using System.Text.Json;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>
/// RFC 6901 claim-pointer resolution against a verified token payload
/// (<see cref="JsonElement"/>). Missing or malformed resolution yields the
/// empty sequence — never a wildcard (spec §11.2 deny-on-missing).
/// </summary>
public static class ClaimPointers
{
    /// <summary>Resolve a pointer; false when the pointer does not resolve.</summary>
    public static bool TryResolve(JsonElement payload, string pointer, out JsonElement value)
    {
        value = payload;
        if (string.IsNullOrEmpty(pointer))
        {
            return false;
        }

        if (pointer == "/")
        {
            pointer = "/~0";
        }

        if (pointer[0] != '/')
        {
            value = default;
            return false;
        }

        foreach (var rawToken in pointer[1..].Split('/', StringSplitOptions.None))
        {
            var token = Unescape(rawToken);
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    if (!value.TryGetProperty(token, out value))
                    {
                        return false;
                    }

                    break;
                case JsonValueKind.Array:
                    // RFC 6901 array index (no leading zeros, digits only).
                    if (!IsArrayIndex(token) || !int.TryParse(token, out var index) || index >= value.GetArrayLength())
                    {
                        return false;
                    }

                    value = value[index];
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolve a pointer as a sequence of strings per the declared shape.
    /// Missing pointer, shape mismatch, or malformed content ⇒ empty (never wildcard).
    /// </summary>
    public static IReadOnlyList<string> ResolveStrings(JsonElement payload, string pointer, string shape)
    {
        if (!TryResolve(payload, pointer, out var value))
        {
            return [];
        }

        return ExtractStrings(value, shape);
    }

    /// <summary>String leaf at the pointer, or null when absent/non-string.</summary>
    public static string? ResolveString(JsonElement payload, string pointer) =>
        TryResolve(payload, pointer, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static IReadOnlyList<string> ExtractStrings(JsonElement value, string shape) => shape switch
    {
        ClaimShape.StringArray => value.ValueKind == JsonValueKind.Array ? StringArray(value) : [],
        ClaimShape.SpaceDelimited => value.ValueKind == JsonValueKind.String ? SpaceDelimited(value.GetString()!) : [],
        ClaimShape.StringArrayOrSpaceDelimited => value.ValueKind switch
        {
            JsonValueKind.Array => StringArray(value),
            JsonValueKind.String => SpaceDelimited(value.GetString()!),
            _ => [],
        },
        _ => [],
    };

    private static IReadOnlyList<string> StringArray(JsonElement array)
    {
        var results = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                results.Add(item.GetString()!);
            }
        }

        return results;
    }

    private static IReadOnlyList<string> SpaceDelimited(string raw)
    {
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length == 0 ? [] : tokens;
    }

    /// <summary>
    /// Boot-validation verdict for a pointer expression (RFC 6901): non-empty,
    /// slash-rooted; segments are opaque tokens. Empty string and a pointer
    /// without the leading <c>/</c> are invalid; <c>"/"</c> (the empty-name
    /// member, i.e. <c>/~0</c>) is valid.
    /// </summary>
    public static bool IsValidPointer(string pointer) =>
        !string.IsNullOrEmpty(pointer) && pointer[0] == '/';

    private static string Unescape(string token) =>
        token.Contains('~', StringComparison.Ordinal)
            ? token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
            : token;
    private static bool IsArrayIndex(string token) =>
        token.Length > 0
        && token.All(static c => c is >= '0' and <= '9')
        && (token[0] != '0' || token.Length == 1);

}
