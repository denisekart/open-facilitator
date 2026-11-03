namespace Facilitator.Kernel.Ai;

/// <summary>One allow-listed kernel API endpoint an AI chat action may
/// propose.</summary>
/// <param name="Method">HTTP method (uppercase).</param>
/// <param name="PathTemplate">Route template; <c>{param}</c> segments match one path segment.</param>
/// <param name="Summary">One-line human description shown in the prompt.</param>
/// <param name="BodyHint">Request-body shape ("-" = no body), rendered into the prompt.</param>
/// <param name="Notes">Extra requirement the caller must honor (headers, preconditions); null when none.</param>
public sealed record CatalogEntry(string Method, string PathTemplate, string Summary, string BodyHint, string? Notes)
{
    /// <summary>True when this entry allows <paramref name="method"/> against
    /// <paramref name="path"/>: method matches case-insensitively, the path has
    /// one segment per template segment, literals match case-insensitively, and
    /// a <c>{param}</c> template segment matches any single segment (the same
    /// comparison law the role gate applies to binding paths).</summary>
    public bool Matches(string method, string path)
    {
        if (!string.Equals(Method, method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var left = PathTemplate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var right = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (IsParameter(left[i]) || IsParameter(right[i]))
            {
                continue;
            }

            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsParameter(string segment) =>
        segment.Length > 1 && segment[0] == '{' && segment[^1] == '}';
}

/// <summary>
/// The closed allow-list of kernel endpoints the admin chat may propose an
/// action against — rendered into the chat prompt AND used to validate every
/// proposed path. An action outside the catalog degrades the whole turn: the
/// model may only SELECT from this list, never invent endpoints. Entries
/// mirror the ACTUAL mapped routes of the works/approvals/admin planes.
/// </summary>
public static class ActionCatalog
{
    /// <summary>Every allow-listed entry (stable order = prompt render order).</summary>
    public static IReadOnlyList<CatalogEntry> Entries { get; } =
    [
        // Works + approvals plane.
        new("GET", "/api/works", "List recent Works.", "-", null),
        new("GET", "/api/works/{id}", "Work detail (state, version, timestamps).", "-", null),
        new("GET", "/api/works/{id}/timeline", "Work event timeline.", "-", null),
        new("GET", "/api/works/{id}/approvals", "Approvals pending/decided for a Work.", "-", null),
        new("POST", "/api/works", "Create a Work.",
            "{\"objective\": string (required), \"projectId\": guid-string?, \"commandId\": string?, \"capabilityId\": string?, \"capabilityName\": string?}", null),
        new("POST", "/api/works/{id}/steer", "Add a steering directive to a Work.",
            "{\"directive\": string, \"commandId\": string, \"expectedVersion\": number}", null),
        new("POST", "/api/works/{id}/transitions", "Transition a Work to a new state.",
            "{\"to\": string (state), \"expectedVersion\": number, \"reason\": string?}", null),
        new("POST", "/api/works/{id}/approvals/{approvalId}/respond", "Accept or deny an approval.",
            "{\"decision\": \"approved\"|\"denied\", \"comment\": string?}", null),

        // Admin read surface.
        new("GET", "/api/admin/summary", "Kernel roll-up dashboard.", "-", null),
        new("GET", "/api/admin/search", "Universal search; pass ?q=&lt;query&gt;.", "-", null),
        new("GET", "/api/admin/links", "Gateway links + affected assignments.", "-", null),
        new("GET", "/api/admin/deliveries", "Outbox/inbox/dead-letter delivery rows; ?state=&amp;limit=.", "-", null),
        new("GET", "/api/admin/provisioning", "Provisioning operations ledger.", "-", null),
        new("GET", "/api/admin/metrics", "Queue gauges + windowed counters; ?window=.", "-", null),
        new("GET", "/api/admin/replicas", "Live replicas + queue depth.", "-", null),
        new("GET", "/api/admin/projects/{id}", "Project detail with its Works.", "-", null),
        new("GET", "/api/admin/work/{id}", "Work detail with assignments, approvals, manifests.", "-", null),
        new("GET", "/api/admin/work/{id}/timeline", "Work timeline (admin surface).", "-", null),
        new("GET", "/api/admin/config", "Config resources with existence + row versions.", "-", null),
        new("GET", "/api/admin/config/{resource}", "One config resource payload + ETag.", "-", null),
        new("GET", "/api/admin/config/{resource}/history", "Config change history (newest first).", "-", null),
        new("GET", "/api/admin/audit", "Audit trail; ?action=&actor=&since=&limit= (cap 200).", "-", null),

        // Admin mutation surface.
        new("PATCH", "/api/admin/config/{resource}", "Replace one config resource payload.",
            "the new JSON payload document",
            "Requires an X-Change-Reason header (non-empty) and an If-Match header carrying the revision ETag (e.g. If-Match: \"3\")."),
        new("POST", "/api/admin/dead-letters/{deadLetterId}/replay", "Replay one dead-lettered dispatch.", "-", null),
        new("POST", "/api/admin/manifests/{manifestId}/re-mark", "Re-mark a result manifest's verdict.",
            "{\"resultState\": string, \"reason\": string?}", null),
    ];

    /// <summary>True when at least one entry allows the method+path pair.</summary>
    public static bool Matches(string method, string path)
    {
        foreach (var entry in Entries)
        {
            if (entry.Matches(method, path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The catalog as prompt text: one line per endpoint, notes
    /// appended where a caller-side requirement exists.</summary>
    public static string RenderForPrompt()
    {
        var lines = new List<string>(Entries.Count);
        foreach (var entry in Entries)
        {
            var line = $"- {entry.Method} {entry.PathTemplate} — {entry.Summary} — body: {entry.BodyHint}";
            lines.Add(entry.Notes is { Length: > 0 } notes ? $"{line} (NOTE: {notes})" : line);
        }

        return string.Join('\n', lines);
    }
}
