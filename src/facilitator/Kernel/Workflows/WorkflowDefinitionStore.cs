using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Workflows;

/// <summary>List view of one definition (latest version).</summary>
/// <param name="Id">Definition id.</param>
/// <param name="Version">Latest version.</param>
/// <param name="Title">Operator-facing name.</param>
/// <param name="Description">Operator-facing law.</param>
/// <param name="Source">gallery | hand | ai | derived.</param>
/// <param name="Enabled">Enablement.</param>
/// <param name="Shadow">Strangler flag.</param>
/// <param name="Team">Team scope slug or null.</param>
/// <param name="Digest">sha256 of the canonical payload.</param>
/// <param name="InstancesRunning">Instances in running|waiting on any version.</param>
public sealed record WorkflowDefinitionSummary(
    string Id, int Version, string Title, string Description, string Source,
    bool Enabled, bool Shadow, string? Team, string Digest, int InstancesRunning);

/// <summary>Full view: canonical JSON + YAML face + audit trail fields.</summary>
public sealed record WorkflowDefinitionView(
    WorkflowDefinitionSummary Summary, JsonElement Document, string Yaml, DateTime CreatedAt, string CreatedBy);

/// <summary>PUT outcome (AdminConfigStore outcome law: a conflict never writes).</summary>
public enum WorkflowDefinitionOutcome
{
    Stored,
    Invalid,
    Conflict,
    TooLarge,
    NotFound,
}

/// <summary>Mutation result: on Invalid the errors are the structured findings.</summary>
public sealed record WorkflowDefinitionMutationResult(
    WorkflowDefinitionOutcome Outcome, WorkflowDefinitionView? View, string? Detail,
    IReadOnlyList<WorkflowValidationError>? Errors = null)
{
    /// <summary>Stored = the new version landed.</summary>
    public bool Stored => Outcome == WorkflowDefinitionOutcome.Stored;
}

/// <summary>
/// The definition store: CRUD + version bump + digest (SHA-256 of the
/// canonical JSON), an audit row on every change (allow AND deny), modeled on
/// <see cref="Facilitator.Kernel.Admin.AdminConfigStore"/> (immutable history
/// rows, row-identity ETag). A definition version is immutable once stored —
/// a PUT lands a NEW version; toggling <c>enabled</c>/<c>shadow</c> are the
/// only in-place updates (also audited).
/// </summary>
public sealed class WorkflowDefinitionStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    WorkflowDefinitionValidator validator,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>Store the document as a new version of <paramref name="defId"/>.
    /// The document's <c>id</c> must match the route id; <c>version</c> is
    /// stamped by the store (1 for a new definition, latest+1 otherwise).
    /// <paramref name="expectedEtag"/> is the If-Match value against the
    /// current latest digest (<c>*</c> or a null expectation = force).</summary>
    public async Task<WorkflowDefinitionMutationResult> PutAsync(
        string defId, JsonElement document, string? expectedEtag, string actor, string reason, bool forceEnabled, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var raw = document.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > WorkflowDoc.MaxDocumentBytes)
        {
            await DenyAsync(db, actor, "put", defId, "definition-too-large", reason, ct);
            return new(WorkflowDefinitionOutcome.TooLarge, null,
                $"document exceeds the {WorkflowDoc.MaxDocumentBytes} byte cap");
        }

        if (document.TryGetProperty("id", out var idProp) && idProp.GetString() is { } docId && docId != defId)
        {
            await DenyAsync(db, actor, "put", defId, "id-mismatch", reason, ct);
            return new(WorkflowDefinitionOutcome.Invalid, null, $"document id '{docId}' does not match route id '{defId}'");
        }

        var latest = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.DefId == defId)
            .OrderByDescending(d => d.Version)
            .FirstOrDefaultAsync(ct);
        if (expectedEtag is not null && expectedEtag != "*"
            && (latest is null || expectedEtag.Trim('"') != latest.Digest))
        {
            await DenyAsync(db, actor, "put", defId, "etag-mismatch", reason, ct);
            return new(WorkflowDefinitionOutcome.Conflict, null,
                latest is null ? "no definition version to match the If-Match etag" : "If-Match etag does not match the latest digest");
        }

        // Stamp the identity fields the store owns, then validate + canonicalize.
        var version = (latest?.Version ?? 0) + 1;
        var stamped = Stamp(document, defId, version);
        var validation = validator.Validate(stamped);
        if (!validation.Ok)
        {
            await DenyAsync(db, actor, "put", defId, validation.Errors[0].Code, reason, ct);
            return new(WorkflowDefinitionOutcome.Invalid, null, validation.Errors[0].Detail, validation.Errors);
        }

        var canonical = WorkflowDoc.Canonical(stamped);
        var enabled = forceEnabled || (stamped.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.True);
        if (enabled)
        {
            var enableErrors = EnableChecks(db, stamped, latest);
            if (enableErrors.Count > 0)
            {
                await DenyAsync(db, actor, "put", defId, enableErrors[0].Code, reason, ct);
                return new(WorkflowDefinitionOutcome.Invalid, null, enableErrors[0].Detail, enableErrors);
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var entity = new WorkflowDefinitionEntity
        {
            DefId = defId,
            Version = version,
            Title = stamped.GetProperty("title").GetString()!,
            Description = stamped.GetProperty("description").GetString()!,
            Payload = canonical,
            Digest = WorkflowDoc.Digest(stamped),
            Enabled = enabled,
            Shadow = stamped.TryGetProperty("shadow", out var s) && s.ValueKind == JsonValueKind.True,
            Source = stamped.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String
                ? src.GetString()!
                : "hand",
            Team = stamped.TryGetProperty("team", out var team) && team.ValueKind == JsonValueKind.String
                ? team.GetString()
                : null,
            CreatedAt = now,
            CreatedBy = actor,
            // Schedule-trigger plan (ScheduleEntity law): an enabled
            // scheduled definition stamps NOW — enabling fires the first
            // tick on the next pass; a disabled one keeps a null plan and
            // gets it on the toggle that enables it.
            NextTickAt = enabled && stamped.GetProperty("on").TryGetProperty("schedule", out _)
                ? now
                : null,
        };
        db.WorkflowDefinitions.Add(entity);
        audit.Append(db, new AuditRecord(actor, "admin.workflows.put", $"/api/admin/workflows/definitions/{defId}",
            "allow", reason, null, $"{defId}@{version}",
            JsonSerializer.Serialize(new { defId, version, digest = entity.Digest, reason })));
        await db.SaveChangesAsync(ct);
        return new(WorkflowDefinitionOutcome.Stored, ToView(entity, 0), null);
    }

    /// <summary>Toggle enabled/shadow of one stored version (in-place, audited).</summary>
    public async Task<WorkflowDefinitionMutationResult> ToggleAsync(
        string defId, int version, bool? enabled, bool? shadow, string actor, string reason, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkflowDefinitions
            .FirstOrDefaultAsync(d => d.DefId == defId && d.Version == version, ct);
        if (entity is null)
        {
            return new(WorkflowDefinitionOutcome.NotFound, null, $"no definition version {defId}@{version}");
        }

        var nowEnabled = enabled ?? entity.Enabled;
        if (nowEnabled && !entity.Enabled)
        {
            using var doc = JsonDocument.Parse(entity.Payload);
            var enableErrors = EnableChecks(db, doc.RootElement, entity);
            if (enableErrors.Count > 0)
            {
                await DenyAsync(db, actor, "toggle", $"{defId}@{version}", enableErrors[0].Code, reason, ct);
                await db.SaveChangesAsync(ct);
                return new(WorkflowDefinitionOutcome.Invalid, null, enableErrors[0].Detail, enableErrors);
            }
        }

        var wasEnabled = entity.Enabled;
        entity.Enabled = nowEnabled;
        if (nowEnabled && !wasEnabled)
        {
            using var doc = JsonDocument.Parse(entity.Payload);
            entity.NextTickAt = doc.RootElement.GetProperty("on").TryGetProperty("schedule", out _)
                ? timeProvider.GetUtcNow().UtcDateTime
                : null;
        }
        entity.Shadow = shadow ?? entity.Shadow;
        audit.Append(db, new AuditRecord(actor, "admin.workflows.toggle", $"/api/admin/workflows/definitions/{defId}/versions/{version}/toggle",
            "allow", reason, null, $"{defId}@{version}",
            JsonSerializer.Serialize(new { defId, version, enabled = entity.Enabled, shadow = entity.Shadow, wasEnabled, reason })));
        await db.SaveChangesAsync(ct);
        return new(WorkflowDefinitionOutcome.Stored, await SummaryAsync(db, entity, ct), null);
    }

    /// <summary>All definitions, latest version each, with running-instance counts.</summary>
    public async Task<List<WorkflowDefinitionSummary>> ListAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkflowDefinitions.AsNoTracking().ToListAsync(ct);
        var running = await db.WorkflowInstances.AsNoTracking()
            .Where(i => i.Status == "running" || i.Status == "waiting")
            .GroupBy(i => new { i.DefId, i.DefVersion })
            .Select(g => new { g.Key.DefId, Count = g.Count() })
            .ToListAsync(ct);
        var runningByDef = running.GroupBy(r => r.DefId).ToDictionary(g => g.Key, g => g.Sum(x => x.Count), StringComparer.Ordinal);

        return rows
            .GroupBy(d => d.DefId, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(d => d.Version).First())
            .Select(d => ToSummary(d, runningByDef.GetValueOrDefault(d.DefId)))
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One version (latest when <paramref name="version"/> is null), full view.</summary>
    public async Task<WorkflowDefinitionView?> GetAsync(string defId, int? version, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var query = db.WorkflowDefinitions.AsNoTracking().Where(d => d.DefId == defId);
        query = version is null
            ? query.Where(d => d.Version == db.WorkflowDefinitions.Where(o => o.DefId == defId).Max(o => o.Version))
            : query.Where(d => d.Version == version);
        var entity = await query.FirstOrDefaultAsync(ct);
        return entity is null ? null : await SummaryAsync(db, entity, ct);
    }

    /// <summary>The pinned document of one exact version (the runner's read).</summary>
    public async Task<JsonElement?> GetPinnedAsync(string defId, int version, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var payload = await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.DefId == defId && d.Version == version)
            .Select(d => d.Payload)
            .FirstOrDefaultAsync(ct);
        return payload is null ? null : JsonDocument.Parse(payload).RootElement.Clone();
    }

    /// <summary>Latest enabled version of a definition, or null.</summary>
    public async Task<WorkflowDefinitionEntity?> LatestEnabledAsync(string defId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.WorkflowDefinitions.AsNoTracking()
            .Where(d => d.DefId == defId && d.Enabled)
            .OrderByDescending(d => d.Version)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Every enabled definition version (subscription set + trigger matching).</summary>
    public async Task<List<WorkflowDefinitionEntity>> EnabledAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.WorkflowDefinitions.AsNoTracking().Where(d => d.Enabled).ToListAsync(ct);
    }

    /// <summary>Every stored version (export).</summary>
    public async Task<List<WorkflowDefinitionEntity>> AllVersionsAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.WorkflowDefinitions.AsNoTracking()
            .OrderBy(d => d.DefId).ThenBy(d => d.Version)
            .ToListAsync(ct);
    }

    /// <summary>Invoke-call edges over enabled versions: source
    /// <c>defId@ver</c> -> target <c>workflowId</c> (pinned <c>workflowId@version</c>
    /// when the step pins one). The static-cycle guard consumes this.</summary>
    public async Task<List<(string From, string ToWorkflow, int? PinnedVersion)>> InvokeEdgesAsync(CancellationToken ct)
    {
        var edges = new List<(string, string, int?)>();
        foreach (var def in await EnabledAsync(ct))
        {
            using var doc = JsonDocument.Parse(def.Payload);
            foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray())
            {
                foreach (var (kind, payload) in WorkflowDefinitionValidator.Payload(step))
                {
                    if (kind == "invoke")
                    {
                        int? pinned = payload.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : null;
                        edges.Add(($"{def.DefId}@{def.Version}", payload.GetProperty("workflow").GetString()!, pinned));
                    }
                }
            }
        }

        return edges;
    }

    private List<WorkflowValidationError> EnableChecks(FacilitatorDbContext db, JsonElement document, WorkflowDefinitionEntity? latest)
    {
        var errors = new List<WorkflowValidationError>(validator.EnableTimeErrors(document).Errors);
        var defId = document.GetProperty("id").GetString()!;
        var candidateVersion = document.GetProperty("version").GetInt32();

        // Live-version invoke targets resolve to the target's latest enabled
        // version (re-checked at every enable — guard 1's runtime face).
        var liveVersion = db.WorkflowDefinitions.AsNoTracking().Where(d => d.Enabled)
            .GroupBy(d => d.DefId)
            .Select(g => new { DefId = g.Key, Version = g.Max(d => d.Version) })
            .ToDictionary(g => g.DefId, g => g.Version, StringComparer.Ordinal);
        // The candidate itself participates: a self-invoke resolves to the
        // version being enabled, not to an older enabled row.
        liveVersion[defId] = Math.Max(candidateVersion, liveVersion.GetValueOrDefault(defId));
        string Resolve(string target, int? pinned) =>
            (pinned ?? liveVersion.GetValueOrDefault(target, -1)) is var v && v > 0 ? $"{target}@{v}" : target;

        // Referential enable law: an invoke target must exist, be enabled, and
        // the invoke-call graph over enabled versions must stay acyclic.
        var targets = new List<(string Target, int? Pinned)>();
        foreach (var step in document.GetProperty("steps").EnumerateArray())
        {
            foreach (var (kind, payload) in WorkflowDefinitionValidator.Payload(step))
            {
                if (kind == "invoke")
                {
                    targets.Add((payload.GetProperty("workflow").GetString()!,
                        payload.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null));
                }
            }
        }

        foreach (var (target, pinned) in targets)
        {
            if (!liveVersion.ContainsKey(target) || (pinned is not null && pinned != liveVersion.GetValueOrDefault(target) &&
                !db.WorkflowDefinitions.AsNoTracking().Any(d => d.DefId == target && d.Version == pinned && d.Enabled)))
            {
                errors.Add(new("invoke-target-unavailable",
                    $"invoke target '{target}'{(pinned is null ? "" : $"@{pinned}")}' is not an enabled definition"));
            }
        }

        // Cycle check over the enabled graph including this candidate.
        var edges = new List<(string From, string To)>();
        foreach (var other in db.WorkflowDefinitions.AsNoTracking().Where(d => d.Enabled))
        {
            if (other.DefId == defId && other.Version == candidateVersion)
            {
                continue; // the candidate replaces its own row's edges
            }

            using var otherDoc = JsonDocument.Parse(other.Payload);
            var otherReentrant = Reentrant(otherDoc.RootElement);
            foreach (var step in otherDoc.RootElement.GetProperty("steps").EnumerateArray())
            {
                foreach (var (kind, payload) in WorkflowDefinitionValidator.Payload(step))
                {
                    if (kind == "invoke")
                    {
                        var pinned = payload.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number
                            ? v.GetInt32()
                            : (int?)null;
                        var to = Resolve(payload.GetProperty("workflow").GetString()!, pinned);
                        if (to != $"{other.DefId}@{other.Version}" || !otherReentrant)
                        {
                            edges.Add(($"{other.DefId}@{other.Version}", to));
                        }
                    }
                }
            }
        }

        var candidateReentrant = Reentrant(document);
        foreach (var (target, pinned) in targets)
        {
            var to = Resolve(target, pinned);
            if (to != $"{defId}@{candidateVersion}" || !candidateReentrant)
            {
                edges.Add(($"{defId}@{candidateVersion}", to));
            }
        }

        var cycle = WorkflowPreemption.FindStaticCycle(edges);
        if (cycle is not null)
        {
            errors.Add(new("composition-cycle", $"invoke-call graph cycle: {string.Join(" -> ", cycle)}"));
        }

        _ = latest;
        return errors;
    }

    private static bool Reentrant(JsonElement document) =>
        document.TryGetProperty("composition", out var c) && c.ValueKind == JsonValueKind.Object
        && c.TryGetProperty("reentrant", out var r) && r.ValueKind == JsonValueKind.True;

    private async Task DenyAsync(FacilitatorDbContext db, string actor, string verb, string id, string code, string reason, CancellationToken ct)
    {
        audit.Append(db, new AuditRecord(actor, $"admin.workflows.{verb}", $"/api/admin/workflows/definitions/{id}",
            "deny", reason, null, null, JsonSerializer.Serialize(new { id, code, reason })));
        await db.SaveChangesAsync(ct);
    }

    private async Task<WorkflowDefinitionView> SummaryAsync(FacilitatorDbContext db, WorkflowDefinitionEntity entity, CancellationToken ct)
    {
        var running = await db.WorkflowInstances.AsNoTracking()
            .CountAsync(i => i.DefId == entity.DefId && (i.Status == "running" || i.Status == "waiting"), ct);
        using var doc = JsonDocument.Parse(entity.Payload);
        return new(ToSummary(entity, running), doc.RootElement.Clone(), WorkflowDoc.ToYaml(doc.RootElement),
            entity.CreatedAt, entity.CreatedBy);
    }

    private static WorkflowDefinitionView ToView(WorkflowDefinitionEntity entity, int running)
    {
        using var doc = JsonDocument.Parse(entity.Payload);
        return new(ToSummary(entity, running), doc.RootElement.Clone(), WorkflowDoc.ToYaml(doc.RootElement),
            entity.CreatedAt, entity.CreatedBy);
    }

    private static WorkflowDefinitionSummary ToSummary(WorkflowDefinitionEntity d, int running) =>
        new(d.DefId, d.Version, d.Title, d.Description, d.Source, d.Enabled, d.Shadow, d.Team, d.Digest, running);

    // Store-owned fields: id + version are set verbatim, enabled defaults to
    // false when the document omits it.
    private static JsonElement Stamp(JsonElement document, string defId, int version)
    {
        var text = JsonSerializer.Deserialize<Dictionary<string, object?>>(document.GetRawText())!;
        text["id"] = defId;
        text["version"] = version;
        text.TryAdd("enabled", false);
        return JsonDocument.Parse(JsonSerializer.Serialize(text)).RootElement.Clone();
    }
}
