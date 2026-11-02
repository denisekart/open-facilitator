using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Evidence;
using Facilitator.Kernel.Shared.Memory;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Work;

/// <summary>What a memory arm hands back to the ingress that invoked it (the ResultArm shape).</summary>
/// <param name="Problem">Typed problem to answer with; null = handled.</param>
/// <param name="Status">Outcome status wire value when handled.</param>
/// <param name="Data">In-band ack payload (retain receipt / recall response), when the arm produced one.</param>
public sealed record MemoryArm(ProblemDetails? Problem, string Status, object? Data = null)
{
    /// <summary>True when the arm refused — the caller must NOT settle the inbox as processed.</summary>
    public bool Refused => Problem is not null;
}

/// <summary>
/// The memory arms of the link + CloudEvent planes: the two
/// commands delegate to the broker with a context built ONLY from the screened
/// principal (D4: a workload retain lands <c>candidate</c>; curation never
/// rides this plane) and answer in-band on the ack <c>data</c> (the
/// request_upload ticket precedent). The two frozen events never refuse on
/// memory trouble — §14.6: basic routing is never blocked on memory — an
/// unresolvable candidate or attestation audits a deny inside the broker and
/// the arm still answers accepted.
/// </summary>
public sealed class MemoryIngress(
    MemoryBroker broker,
    TimeProvider timeProvider,
    Microsoft.EntityFrameworkCore.IDbContextFactory<Facilitator.Kernel.Shared.Persistence.FacilitatorDbContext> contextFactory)
{
    /// <summary>Command types these arms handle.</summary>
    public const string RetainType = "org.facilitator.command.memory.retain.v1";
    public const string RecallType = "org.facilitator.command.memory.recall.v1";

    /// <summary>Event types these arms handle (frozen schemas, no amendment).</summary>
    public const string CandidateType = "org.facilitator.event.memory.candidate.v1";
    public const string UsedType = "org.facilitator.event.memory.used.v1";

    /// <summary>Command arm: retain. The receipt rides the ack; a duplicate commandId re-runs the idempotent retain (content-hash dedupe returns the stored identity + <c>duplicate:true</c>).</summary>
    public async Task<MemoryArm> HandleRetainAsync(
        JsonElement root, EffectivePrincipal? principal, CancellationToken ct)
    {
        if (principal is null)
        {
            return new MemoryArm(ProblemErrors.Unsupported("memory.retain requires a link principal"), "memory-denied");
        }

        var data = root.GetProperty("data");
        var request = ParseRetain(data);
        if (request is null)
        {
            return new MemoryArm(ProblemErrors.SchemaViolation(
                "memory.retain payload failed broker validation"), "schema-violation");
        }

        var result = await broker.RetainAsync(MemoryAccessContext.From(principal), request, ct);
        return result.Outcome switch
        {
            MemoryOpOutcome.Ok => new MemoryArm(null, "accepted", Receipt(result.Receipt!)),
            MemoryOpOutcome.QuotaExceeded => new(ProblemErrors.MemoryQuotaExceeded(principal.Workload.TeamSlug), "memory-quota-exceeded"),
            _ => new(ProblemErrors.MemoryScopeInvalid(Str(data, "scopeSelector") ?? "?"), "memory-scope-invalid"),
        };
    }

    /// <summary>Command arm: recall. Read-only, so a duplicate commandId honestly re-runs the query (no stored-result caching).</summary>
    public async Task<MemoryArm> HandleRecallAsync(
        JsonElement root, EffectivePrincipal? principal, CancellationToken ct)
    {
        if (principal is null)
        {
            return new MemoryArm(ProblemErrors.Unsupported("memory.recall requires a link principal"), "memory-denied");
        }

        var data = root.GetProperty("data");
        var request = ParseRecall(data);
        if (request is null)
        {
            return new MemoryArm(ProblemErrors.SchemaViolation(
                "memory.recall payload failed broker validation"), "schema-violation");
        }

        var response = await broker.RecallAsync(MemoryAccessContext.From(principal), request, ct);
        return new MemoryArm(null, "accepted", Response(response));
    }

    /// <summary>
    /// Event arm: the frozen <c>event.memory.candidate</c> records a candidate
    /// entry. The wire <c>scope</c> enum maps per the recorded law (Known
    /// fact 2): <c>workspace</c> lands team-scoped-by-ownership; content is
    /// the summary (the event carries no full text). The event plane never
    /// 500s on memory: an unresolvable scope audits a deny inside the broker
    /// and the arm still answers accepted.
    /// </summary>
    public async Task<MemoryArm> HandleCandidateAsync(
        JsonElement root, EffectivePrincipal? principal, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        var summary = Str(data, "contentSummary");
        if (summary is null || !data.TryGetProperty("provenance", out var prov))
        {
            return new MemoryArm(null, "accepted");
        }
        var workRaw = Str(prov, "workId") ?? EnvelopeWorkId(root);

        // Link plane: the screened workload owns the candidate. CE plane (no
        // principal): ownership follows the named Work's lineage — Work →
        // Project → Team — since the ungated surface carries no workload row.
        MemoryAccessContext? caller = principal is not null
            ? MemoryAccessContext.From(principal)
            : await CePlaneCallerAsync(prov, workRaw, ct);

        if (caller?.TeamSlug is null)
        {
            return new MemoryArm(null, "accepted");
        }

        var scope = Str(data, "scope") ?? "team";
        var teamSelector = $"team:{caller.TeamSlug}";
        var selector = scope switch
        {
            "team" or "workspace" => teamSelector,
            // A project-scope candidate belongs to its work's project lineage;
            // without a resolvable work, ownership lands team-scoped.
            "project" => Guid.TryParse(workRaw, out _) ? $"work:{workRaw}" : teamSelector,
            "private" => $"private:{caller.Actor}",
            _ => null,
        };
        if (selector is null)
        {
            return new MemoryArm(null, "accepted");
        }

        // The event's own provenance timestamps the entry; a malformed or
        // missing createdAt falls back to the event envelope's time-equivalent
        // (now) rather than refusing — routing never blocks on memory.
        var createdRaw = Str(prov, "createdAt");
        var createdAt = DateTime.TryParse(createdRaw, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : default;
        // (workRaw resolved above with the selector.)
        var request = new MemoryRetainRequest(
            selector, "lesson_candidate", summary,
            new MemoryProvenanceInput(
                Str(prov, "source") ?? "workspace", Str(prov, "evidenceRef") ?? "unknown",
                Str(prov, "createdBy") ?? caller.Actor,
                createdAt == default ? timeProvider.GetUtcNow().UtcDateTime : createdAt,
                WorkId: Guid.TryParse(workRaw, out _) ? workRaw : null),
            PolicyLabel: Str(data, "policyLabel"));
        var result = await broker.RetainAsync(caller, request, ct);
        if (result.Outcome == MemoryOpOutcome.Ok && Guid.TryParse(workRaw, out var workId))
        {
            await broker.NoteWorkCandidateAsync(caller, workId, result.Receipt!.EntryId, ct);
        }

        return new MemoryArm(null, "accepted");
    }

    /// <summary>Event arm: the frozen <c>event.memory.used</c> attestation records the same usage row an in-kernel recall writes (appendix §6.2). Unknown entry/consumer: broker audits + swallows.</summary>
    public async Task<MemoryArm> HandleUsedAsync(
        JsonElement root, EffectivePrincipal? principal, CancellationToken ct)
    {
        var data = root.GetProperty("data");
        // CE plane: no workload row exists, so the consumer's team cannot be
        // verified — the broker's read law denies (audit + swallow) unless the
        // entry is facilitator-visible. §14.6: the plane still answers accepted.
        var caller = principal is not null
            ? MemoryAccessContext.From(principal)
            : new MemoryAccessContext(
                "cloud-event", null, new HashSet<string>(StringComparer.Ordinal), IsWorkload: true);
        await broker.RecordUsageAsync(
            caller,
            Str(data, "memoryId") ?? string.Empty,
            Str(data, "consumer") ?? "unknown",
            Str(data, "justification") ?? "unjustified",
            ct);
        return new MemoryArm(null, "accepted");
    }

    /// <summary>
    /// The CE-plane candidate caller: ownership follows the named Work's
    /// lineage (Work → Project → Team). No live Work ⇒ no owner ⇒ the arm
    /// swallows (an event-plane retain needs a team to land anything in).
    /// </summary>
    private async Task<MemoryAccessContext?> CePlaneCallerAsync(
        JsonElement prov, string? workRaw, CancellationToken ct)
    {
        if (workRaw is null || !Guid.TryParse(workRaw, out var workId))
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var slug = await (
            from w in db.Works.AsNoTracking()
            join p in db.Projects.AsNoTracking() on w.ProjectId equals (Guid?)p.ProjectId
            join t in db.Teams.AsNoTracking() on p.TeamId equals t.TeamId
            where w.WorkId == workId
            select t.Slug).FirstOrDefaultAsync(ct);
        if (slug is null)
        {
            return null;
        }

        var actor = Str(prov, "createdBy") ?? "cloud-event";
        return new MemoryAccessContext(
            actor, slug, new HashSet<string>(StringComparer.Ordinal), IsWorkload: true);
    }

    /// <summary>The Work GUID a <c>work/&lt;id&gt;[/...]</c> envelope subject names, when one exists.</summary>
    private static string? EnvelopeWorkId(JsonElement root)
    {
        if (!root.TryGetProperty("subject", out var s) || s.ValueKind != JsonValueKind.String
            || s.GetString() is not { } subject || !subject.StartsWith("work/", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = subject["work/".Length..];
        var slash = rest.IndexOf('/');
        var candidate = slash < 0 ? rest : rest[..slash];
        return Guid.TryParse(candidate, out _) ? candidate : null;
    }

    private static string? Str(JsonElement data, string name) =>
        data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static MemoryRetainRequest? ParseRetain(JsonElement data)
    {
        var selector = Str(data, "scopeSelector");
        var type = Str(data, "type");
        var content = Str(data, "content");
        if (selector is null || content is null
            || type is null || !MemoryBroker.WireTypes.Contains(type)
            || !data.TryGetProperty("provenance", out var prov) || prov.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var source = Str(prov, "source");
        var evidenceRef = Str(prov, "evidenceRef");
        var createdBy = Str(prov, "createdBy");
        var createdRaw = Str(prov, "createdAt");
        if (source is null || evidenceRef is null || createdBy is null
            || createdRaw is null
            || !DateTime.TryParse(createdRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var createdAt))
        {
            return null;
        }

        var label = Str(data, "policyLabel");
        if (label is not null && !MemoryBroker.WirePolicyLabels.Contains(label))
        {
            return null;
        }

        DateTime? observedAt = null;
        if (Str(data, "observedAt") is { } raw)
        {
            if (!DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                return null;
            }

            observedAt = parsed;
        }

        return new MemoryRetainRequest(
            selector, type, content,
            new MemoryProvenanceInput(
                source, evidenceRef, createdBy, createdAt.ToUniversalTime(),
                Str(prov, "workId"), Str(prov, "executionId"), Str(prov, "sourceDigest")),
            MediaType: Str(data, "mediaType"),
            ContentSummary: Str(data, "contentSummary"),
            PolicyLabel: label,
            Classification: Str(data, "classification") is "internal" or "public" ? Str(data, "classification") : null,
            ObservedAt: observedAt);
    }

    private static MemoryRecallRequest? ParseRecall(JsonElement data)
    {
        var query = Str(data, "query");
        var purpose = Str(data, "purpose");
        if (query is null || purpose is null
            || purpose is not ("assignment.context" or "requirements.clarification" or "report.synthesis"))
        {
            return null;
        }

        List<string>? selectors = null;
        if (data.TryGetProperty("scopeSelectors", out var hints) && hints.ValueKind == JsonValueKind.Array)
        {
            selectors = [];
            foreach (var hint in hints.EnumerateArray())
            {
                if (hint.ValueKind == JsonValueKind.String && hint.GetString() is { } s)
                {
                    selectors.Add(s);
                }
            }
        }

        int? maxItems = data.TryGetProperty("maxItems", out var mi) && mi.TryGetInt32(out var items) ? items : null;
        int? maxTokens = data.TryGetProperty("maxTokens", out var mt) && mt.TryGetInt32(out var tokens) ? tokens : null;
        var includeSuperseded = data.TryGetProperty("includeSuperseded", out var iss) && iss.ValueKind == JsonValueKind.True;
        return new MemoryRecallRequest(query, purpose, Str(data, "workId"), selectors, maxItems, maxTokens, includeSuperseded);
    }

    /// <summary>The wire receipt shape (the same fields the REST 201 answers).</summary>
    public static object Receipt(MemoryRetainReceipt receipt) =>
        new { entryId = receipt.EntryId, status = receipt.Status, duplicate = receipt.Duplicate };

    /// <summary>The D8 response shape shared by REST and ack data.</summary>
    public static object Response(MemoryRecallResponse response) => new
    {
        requestId = response.RequestId,
        providerProfile = response.ProviderProfile,
        entries = response.Entries.Select(e => new
        {
            entryId = e.EntryId,
            scopeId = e.ScopeId,
            type = e.Type,
            content = e.Content,
            contentHash = e.ContentHash,
            provenance = e.Provenance,
            observedAt = e.ObservedAt,
            effectiveFrom = e.EffectiveFrom,
            revision = e.Revision,
            status = e.Status,
            policyLabel = e.PolicyLabel,
            accessControl = new { visibility = e.PolicyLabel, allowedPrincipals = new[] { $"team:{e.TeamSlug}" } },
            retrievalReason = e.RetrievalReason,
        }),
        truncated = response.Truncated,
        asOf = response.AsOf,
    };
}
