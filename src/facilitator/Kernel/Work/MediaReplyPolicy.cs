using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Work;

/// <summary>The declared <c>media</c> facet of one capability descriptor, as
/// read from an endpoint's stored enrollment manifest. An absent facet means
/// text-only — exactly what a provider without a media leg means — so the
/// default carries <c>out</c> with an empty kinds allow-list and every drop
/// degrades honestly (link line + audit) instead of vanishing.</summary>
public sealed record MediaFacet(
    IReadOnlyList<string> Directions,
    IReadOnlyList<string> Kinds,
    int? MaxCount,
    long? MaxBytesPerItem)
{
    public static readonly MediaFacet TextOnly = new(["out"], [], null, null);

    public bool AcceptsOut => Directions.Contains("out", StringComparer.Ordinal);

    /// <summary>Parse the facet of the descriptor for <paramref name="capabilityId"/>
    /// out of a stored CapabilityManifestV1 jsonb document. Any shape problem —
    /// no manifest, no capabilities array, no descriptor, no facet — answers
    /// <see cref="TextOnly"/>: manifests may predate the facet, and a malformed
    /// one must never fail a completion reply.</summary>
    public static MediaFacet FromManifest(string? capabilityManifestJson, string capabilityId)
    {
        if (string.IsNullOrWhiteSpace(capabilityManifestJson))
        {
            return TextOnly;
        }

        try
        {
            using var doc = JsonDocument.Parse(capabilityManifestJson);
            if (!doc.RootElement.TryGetProperty("capabilities", out var capabilities)
                || capabilities.ValueKind != JsonValueKind.Array)
            {
                return TextOnly;
            }

            foreach (var descriptor in capabilities.EnumerateArray())
            {
                if (!descriptor.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !string.Equals(id.GetString(), capabilityId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!descriptor.TryGetProperty("media", out var media)
                    || media.ValueKind != JsonValueKind.Object)
                {
                    return TextOnly;
                }

                var directions = ReadStrings(media, "directions");
                if (directions.Count == 0)
                {
                    return TextOnly;
                }

                var kinds = ReadStrings(media, "kinds");
                int? maxCount = ReadInt(media, "maxCount");
                long? maxBytes = ReadLong(media, "maxBytesPerItem");
                return new MediaFacet(directions, kinds, maxCount, maxBytes);
            }
        }
        catch (JsonException)
        {
            // fall through to the honest text-only default
        }

        return TextOnly;
    }

    private static List<string> ReadStrings(JsonElement media, string property)
    {
        var values = new List<string>();
        if (media.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static int? ReadInt(JsonElement media, string property) =>
        media.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static long? ReadLong(JsonElement media, string property) =>
        media.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
            ? parsed
            : null;
}

/// <summary>The outbound media selection law: from the manifest's stored,
/// verified outputs, decide what a completion reply may attach and what must
/// degrade — never a silent drop. Every rejected candidate names a machine
/// reason (the audit rows and the reply's link line carry it):
/// <c>kind-not-allowed</c> (facet MIME glob missed), <c>count-cap</c> (the
/// wire ceiling / narrower facet cap ran out), <c>size-cap</c> (over the
/// facet's or the default per-item budget).</summary>
public static class MediaReplyPolicy
{
    /// <summary>The wire ceiling on <c>communication.*.artifacts</c> — fixed
    /// at 8 by the schema; a capability facet may only narrow it.</summary>
    public const int WireMaxCount = 8;

    public static (List<(ArtifactEntity Row, string Kind)> Attach, List<(ArtifactEntity Row, string Reason)> Degraded) Select(
        IReadOnlyList<ArtifactEntity> candidates,
        MediaFacet facet,
        long maxBytesPerItemDefault,
        int maxCountDefault = WireMaxCount,
        MediaPolicy? policy = null)
    {
        var attach = new List<(ArtifactEntity, string)>();
        var degraded = new List<(ArtifactEntity, string)>();
        var countCap = Math.Min(
            Math.Min(facet.MaxCount ?? maxCountDefault, maxCountDefault),
            policy is null ? WireMaxCount : Math.Min(policy.MaxCount, WireMaxCount));
        var maxBytes = Math.Min(
            facet.MaxBytesPerItem ?? maxBytesPerItemDefault,
            policy?.MaxBytesPerItem ?? long.MaxValue);
        var facetAcceptsOut = facet.AcceptsOut;

        foreach (var row in candidates)
        {
            if (!facetAcceptsOut || !KindAllowed(row.MediaType, facet.Kinds))
            {
                degraded.Add((row, "kind-not-allowed"));
                continue;
            }

            // The team's federation law (5.1) narrows the facet further:
            // an allow-list must match, a forbid-list vetoes.
            if (policy?.AllowKinds is { Count: > 0 } allow && !KindAllowed(row.MediaType, allow))
            {
                degraded.Add((row, "kind-not-allowed"));
                continue;
            }

            if (policy?.ForbidKinds is { Count: > 0 } forbid && KindAllowed(row.MediaType, forbid))
            {
                degraded.Add((row, "kind-forbidden"));
                continue;
            }

            if (row.Size > maxBytes)
            {
                degraded.Add((row, "size-cap"));
                continue;
            }

            if (attach.Count >= countCap)
            {
                degraded.Add((row, "count-cap"));
                continue;
            }

            attach.Add((row, KindOf(row.MediaType)));
        }

        return (attach, degraded);
    }

    /// <summary>The taxonomy label the wire <c>kind</c> carries (open
    /// extensibility on <c>artifactRef.kind</c>): images are results with a
    /// picture, everything else a report.</summary>
    public static string KindOf(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "result.image" : "result.report";

    /// <summary>MIME glob match against the facet's allow-list: <c>image/*</c>
    /// matches <c>image/png</c>; a bare exact type matches itself; an empty
    /// list allows nothing (text-only).</summary>
    public static bool KindAllowed(string mediaType, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var slash = pattern.IndexOf('/');
            if (slash <= 0 || !pattern.EndsWith("/*", StringComparison.Ordinal))
            {
                if (string.Equals(pattern, mediaType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            var prefix = pattern[..(slash + 1)];
            if (mediaType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && mediaType.Length > prefix.Length)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The resolved <c>media-policy</c> row for one team (plan 5.1):
/// the effective per-item budget and count cap (defaults, narrowed per-team),
/// the team's kind allow/forbid lists, and the history-forwarding switches.
/// An absent resource or an absent/malformed team entry answers
/// <see cref="System"/> — today's wave-1 behavior (facet-only, 8 MB, 8
/// items, no forwarding beyond the facet): the policy layer only ever
/// narrows, never invents permission.</summary>
public sealed record MediaPolicy(
    long MaxBytesPerItem,
    int MaxCount,
    IReadOnlyList<string>? AllowKinds,
    IReadOnlyList<string>? ForbidKinds,
    int HistoryMaxMessages,
    bool ForwardToForeignTeam)
{
    public static readonly MediaPolicy System = new(8 * 1024 * 1024, 8, null, null, 20, false);

    /// <summary>The allocation-time retention stamp (plan 5.2): the
    /// <c>media-policy</c> row's <c>defaults.retentionDays.&lt;kind&gt;</c>
    /// rendered as the wire policy (<c>"{n}d"</c>), else the caller's
    /// default. GC honesty is inherited: the existing Nd sweep does the
    /// collection, this only says how long each kind lives.</summary>
    public static async Task<string> RetentionForKindAsync(
        FacilitatorDbContext db, string kind, int defaultDays, CancellationToken ct)
    {
        var row = await db.AdminConfig.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Resource == "media-policy", ct);
        if (row is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(row.Payload);
                if (doc.RootElement.TryGetProperty("defaults", out var defaults)
                    && defaults.TryGetProperty("retentionDays", out var days)
                    && days.TryGetProperty(kind, out var value)
                    && value.TryGetInt32(out var parsed) && parsed is > 0 and <= 9999)
                {
                    return $"{parsed}d";
                }
            }
            catch (JsonException)
            {
                // a malformed policy row is no policy
            }
        }

        return $"{defaultDays}d";
    }

    /// <summary>Resolve the policy for a team slug from the stored
    /// <c>media-policy</c> admin row (read on the caller's ambient db; every
    /// shape problem degrades to a narrower default, never an exception —
    /// the reply path must not fail on a policy the operator mistyped).
    /// <paramref name="foreignDestination"/> names the destination team when
    /// the reply leaves the owning team's plane (null = same plane).</summary>
    public static async Task<MediaPolicy> ForTeamAsync(
        FacilitatorDbContext db, string? teamSlug, string? foreignDestination, CancellationToken ct)
    {
        var row = await db.AdminConfig.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Resource == "media-policy", ct);
        if (row is null)
        {
            return System;
        }

        JsonElement payload;
        try
        {
            payload = JsonDocument.Parse(row.Payload).RootElement;
        }
        catch (JsonException)
        {
            return System;
        }

        var maxBytes = System.MaxBytesPerItem;
        var maxCount = System.MaxCount;
        if (payload.TryGetProperty("defaults", out var defaults) && defaults.ValueKind == JsonValueKind.Object)
        {
            if (defaults.TryGetProperty("maxBytesPerItem", out var mb) && mb.TryGetInt64(out var mbs) && mbs > 0)
            {
                maxBytes = Math.Min(mbs, maxBytes);
            }

            if (defaults.TryGetProperty("maxCount", out var mc) && mc.TryGetInt32(out var mcs) && mcs > 0)
            {
                maxCount = Math.Min(mcs, maxCount);
            }
        }

        var entry = TeamEntry(payload, teamSlug);
        if (entry is not null)
        {
            maxBytes = Math.Min(ReadLong(entry.Value, "maxBytesPerItem") ?? maxBytes, maxBytes);
            maxCount = Math.Min(ReadInt(entry.Value, "maxCount") ?? maxCount, maxCount);
        }

        var historyMessages = entry is not null &&
            entry.Value.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Object
            && h.TryGetProperty("toHarnessMaxMessages", out var hm) && hm.TryGetInt32(out var hms) && hms is > 0 and <= 100
                ? hms
                : System.HistoryMaxMessages;
        var forward = entry is not null &&
            entry.Value.TryGetProperty("history", out var h2) && h2.ValueKind == JsonValueKind.Object
            && h2.TryGetProperty("toForeignTeam", out var tf) && tf.ValueKind == JsonValueKind.True;

        return new MediaPolicy(
            maxBytes,
            maxCount,
            ReadArray(entry, "allowKinds"),
            ReadArray(entry, "forbidKinds"),
            historyMessages,
            foreignDestination is null || forward);
    }

    private static JsonElement? TeamEntry(JsonElement payload, string? teamSlug)
    {
        if (teamSlug is null || !payload.TryGetProperty("teams", out var teams)
            || teams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in teams.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("team", out var team)
                && team.ValueKind == JsonValueKind.String
                && string.Equals(team.GetString(), teamSlug, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? ReadArray(JsonElement? entry, string property)
    {
        if (entry is not { } e || !e.TryGetProperty(property, out var arr)
            || arr.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } value)
            {
                list.Add(value);
            }
        }

        return list;
    }

    private static long? ReadLong(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed) && parsed > 0
            ? parsed
            : null;

    private static int? ReadInt(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed) && parsed > 0
            ? parsed
            : null;
}
