using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Auth;
using Facilitator.Kernel.Shared.Memory;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Work;

/// <summary>One resolved context item: the URI, the content object (never
/// raw bytes — refs, digests and brokered URLs only), and an honest
/// <c>truncated</c> flag whenever the budget trimmed what the source
/// offered.</summary>
public sealed record ContextItem(string Uri, JsonObject Content, bool Truncated);

/// <summary>A context URI the kernel cannot answer locally (today:
/// <c>thread://</c>, whose bodies live on the source platform): the caller
/// routes <c>command.context.fetch.v1> to the owning adapter, which answers
/// asynchronously with <c>event.context.fetched.v1</c> and resolves this
/// fetch id.</summary>
public sealed record ContextPending(string Uri, string FetchId, string Provider);

/// <summary>What one expansion answered: the items resolved kernel-locally
/// plus the URIs parked as pending for an adapter to answer.</summary>
public sealed record ContextFetchResult(
    string FetchId,
    IReadOnlyList<ContextItem> Items,
    IReadOnlyList<ContextPending> Pending);

/// <summary>
/// The <c>context.fetch</c> expansion core, kernel-local for every URI class
/// the kernel owns the bytes for:
/// <list type="bullet">
///   <item><c>memory://&lt;selector&gt;?q=…</c> — the recall path the SDK's
///   memory client rides (same broker, same team-scope law: a selector whose
///   <c>team:</c> segment is not the caller's team yields an error item, not
///   another team's knowledge).</item>
///   <item><c>artifact://…/&lt;guid&gt;</c> — the logical-URI law: the GUID is
///   the last path segment; the answer carries metadata + a brokered content
///   URL, never the bytes (envelope-bounded law).</item>
///   <item><c>thread://&lt;provider&gt;/…</c> — NOT kernel-local: pending with
///   a fetchId (3.2 routes it to the source adapter).</item>
/// </list>
/// Budget law lives here centrally: ≤ 32 refs per expansion, ≤ 32 KB of
/// serialized content per item — recorded as <c>truncated: true</c>, never a
/// silent cut.
/// </summary>
public sealed class ContextFetchService(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    MemoryBroker memory,
    InteractionOptions interaction)
{
    public const string CommandType = "org.facilitator.command.context.fetch.v1";

    /// <summary>Contract ceiling: <c>contextRefs</c> holds at most 32 URIs.</summary>
    public const int MaxRefs = 32;

    /// <summary>Per-item serialized-content budget (32 KB).</summary>
    public const int MaxItemBytes = 32 * 1024;

    /// <summary>Expand one set of context refs for a link principal. Unknown
    /// or malformed URIs answer an ERROR ITEM (honest content, never a failed
    /// command): a fetch is a read, and a read that could not answer says
    /// what it could not answer.</summary>
    public async Task<ContextFetchResult> ExpandAsync(
        IReadOnlyList<string> contextRefs,
        LinkPrincipal principal,
        Guid? workId,
        CancellationToken ct)
    {
        var fetchId = Guid.NewGuid().ToString("N");
        var items = new List<ContextItem>();
        var pending = new List<ContextPending>();
        foreach (var uri in contextRefs.Take(MaxRefs))
        {
            var (scheme, authority, query) = Split(uri);
            switch (scheme)
            {
                case "memory":
                    items.Add(await ExpandMemoryAsync(uri, authority, query, principal, workId, ct));
                    break;
                case "artifact":
                    items.Add(await ExpandArtifactAsync(uri, authority, principal, ct));
                    break;
                case "thread":
                    // The provider token is the authority segment
                    // (thread://telegram/123/456?limit=20).
                    pending.Add(new ContextPending(uri, fetchId, authority.Split('/')[0]));
                    break;
                default:
                    items.Add(ErrorItem(uri, "scheme-unsupported"));
                    break;
            }
        }

        return new ContextFetchResult(fetchId, items, pending);
    }

    private async Task<ContextItem> ExpandMemoryAsync(
        string uri, string selector, string query, LinkPrincipal principal, Guid? workId, CancellationToken ct)
    {
        // Scope law (MemoryEndpoints law repeated at this door): a team:
        // selector outside the caller's team answers a deny item.
        if (selector.StartsWith("team:", StringComparison.Ordinal)
            && !string.Equals(selector["team:".Length..], principal.TeamSlug, StringComparison.Ordinal))
        {
            return ErrorItem(uri, "memory-scope-outside-team");
        }

        var q = QueryParam(query, "q");
        if (q is null || selector.Length == 0)
        {
            return ErrorItem(uri, "memory-needs-selector-and-q");
        }

        var response = await memory.RecallAsync(
            new MemoryAccessContext(
                $"link:{principal.EndpointId}", principal.TeamSlug,
                new HashSet<string>(StringComparer.Ordinal), IsWorkload: true),
            new MemoryRecallRequest(q, "assignment.context", workId?.ToString(), [selector], 8),
            ct);

        var entries = new JsonArray();
        var bytes = 0;
        var truncated = response.Truncated;
        foreach (var entry in response.Entries)
        {
            var row = (JsonNode)new JsonObject
            {
                ["entryId"] = entry.EntryId.ToString("N"),
                ["type"] = entry.Type,
                ["content"] = entry.Content,
                ["observedAt"] = entry.ObservedAt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture),
                ["retrievalReason"] = entry.RetrievalReason,
            };
            var size = row.ToJsonString().Length;
            if (bytes + size > MaxItemBytes)
            {
                truncated = true;
                break;
            }

            bytes += size;
            entries.Add(row);
        }

        return new ContextItem(
            uri,
            new JsonObject { ["entries"] = entries, ["truncatedBySource"] = response.Truncated },
            truncated);
    }

    private async Task<ContextItem> ExpandArtifactAsync(
        string uri, string authority, LinkPrincipal principal, CancellationToken ct)
    {
        // The logical-URI law: the kernel GUID is the LAST path segment
        // (artifact://work/assignment/<guid>); a bare artifact://<guid> is
        // the same law with one segment.
        var last = authority.Contains('/') ? authority[(authority.LastIndexOf('/') + 1)..] : authority;
        if (!Guid.TryParse(last, out var artifactId))
        {
            return ErrorItem(uri, "artifact-guid-unparseable");
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.Artifacts.AsNoTracking().FirstOrDefaultAsync(a => a.ArtifactId == artifactId, ct);
        if (row is null || !string.Equals(row.TeamSlug, principal.TeamSlug, StringComparison.Ordinal))
        {
            return ErrorItem(uri, "artifact-not-readable");
        }

        var publicBase = interaction.PublicBaseUrl?.TrimEnd('/');
        var contentUrl = string.IsNullOrWhiteSpace(publicBase)
            ? $"/api/artifacts/{row.ArtifactId:N}/content"
            : $"{publicBase}/api/artifacts/{row.ArtifactId:N}/content";
        var content = new JsonObject
        {
            ["artifactId"] = row.ArtifactId.ToString("N"),
            ["mediaType"] = row.MediaType,
            ["size"] = row.Size,
            ["digest"] = row.Digest,
            ["state"] = row.State,
            ["kind"] = row.Kind,
            ["contentUrl"] = contentUrl,
        };
        // Envelope-bounded law: refs + digests only; a row over the per-item
        // budget is already only metadata (bytes never enter the envelope).
        return new ContextItem(uri, content, content.ToJsonString().Length > MaxItemBytes);
    }

    private static ContextItem ErrorItem(string uri, string error) =>
        new(uri, new JsonObject { ["error"] = error }, false);

    /// <summary>Manual URI split (memory://team:alpha?q=… has an authority
    /// the <see cref="Uri"/> class would not parse as a host): returns
    /// scheme, authority (without the query/fragment), and the raw query.</summary>
    public static (string Scheme, string Authority, string Query) Split(string uri)
    {
        var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return (string.Empty, uri, string.Empty);
        }

        var rest = uri[(schemeEnd + 3)..];
        var queryStart = rest.IndexOf('?');
        var fragmentStart = rest.IndexOf('#');
        var cut = (queryStart, fragmentStart) switch
        {
            (-1, -1) => -1,
            (-1, var f) => f,
            (var q, -1) => q,
            (var q, var f) => Math.Min(q, f),
        };
        var authority = cut == -1 ? rest : rest[..cut];
        var query = queryStart == -1 || (fragmentStart != -1 && fragmentStart < queryStart)
            ? string.Empty
            : rest[(queryStart + 1)..(fragmentStart != -1 && fragmentStart > queryStart ? fragmentStart : rest.Length)];
        return (uri[..schemeEnd].ToLowerInvariant(), authority, query);
    }

    private static string? QueryParam(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && string.Equals(pair[..eq], key, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }
}
