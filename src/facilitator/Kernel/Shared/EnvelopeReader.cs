using System.Text.Json;

namespace Facilitator.Kernel.Shared;

/// <summary>Field extraction shared by every CloudEvent admission path (HTTP ingress + link ingress).</summary>
public static class EnvelopeReader
{
    /// <summary>The envelope's source, or the unknown-source fallback.</summary>
    public static string SourceOf(JsonElement root) =>
        root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "urn:facilitator:unknown";

    /// <summary>The envelope id, or a fresh uuid when absent.</summary>
    public static string EventIdOf(JsonElement root) =>
        root.TryGetProperty("id", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : Guid.NewGuid().ToString("N");

    /// <summary><c>data.workId</c> when present and parseable.</summary>
    public static Guid? WorkIdOf(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data)
            && data.TryGetProperty("workId", out var wid)
            && wid.ValueKind == JsonValueKind.String
            && Guid.TryParse(wid.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
