using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Admin;

/// <summary>Stored admin-config resource (the PATCH/GET body shape).</summary>
/// <param name="Resource">Resource name.</param>
/// <param name="Payload">Validated jsonb document.</param>
/// <param name="RowVersion">Optimistic version — the If-Match ETag.</param>
/// <param name="Author">Canonical actor of the last writer.</param>
/// <param name="Reason">Change reason of the last write.</param>
/// <param name="UpdatedAt">Last write (UTC).</param>
public sealed record AdminConfigView(string Resource, JsonElement Payload, long RowVersion, string Author, string Reason, DateTime UpdatedAt);

/// <summary>PATCH outcome for the config surface.</summary>
public enum AdminConfigOutcome
{
    /// <summary>The write landed.</summary>
    Stored,

    /// <summary>Resource outside the registered set — 404.</summary>
    UnknownResource,

    /// <summary>Shape violation — 400.</summary>
    Invalid,

    /// <summary>Payload over the 16 KiB cap — 400.</summary>
    TooLarge,

    /// <summary>If-Match rowVersion mismatch (or insert-vs-exist disagreement) — 412.</summary>
    Conflict,
}

/// <summary>Result of <see cref="AdminConfigStore.PatchAsync"/>.</summary>
/// <param name="Outcome">Terminal outcome.</param>
/// <param name="View">Stored view when <see cref="AdminConfigOutcome.Stored"/>; the current row otherwise (null when absent/unknown).</param>
/// <param name="Detail">Validation detail when <see cref="AdminConfigOutcome.Invalid"/> or <see cref="AdminConfigOutcome.TooLarge"/>.</param>
public sealed record AdminConfigMutationResult(AdminConfigOutcome Outcome, AdminConfigView? View, string? Detail)
{
    /// <summary>True when the caller may serve 200 + the new ETag.</summary>
    public bool Stored => Outcome == AdminConfigOutcome.Stored;
}

/// <summary>One history entry (newest-first page of the bounded trail).</summary>
public sealed record AdminConfigHistoryEntry(long Version, JsonElement Payload, string Author, string Reason, DateTime UpdatedAt);

/// <summary>
/// The <c>facilitator.admin_config</c> store: plain
/// field-shape validation against a closed registry, version-fenced PATCH
/// (the roles conditional-write template), same-tx history row + audit +
/// newest-20 retention. Stored intent only — no kernel behavior reads these
/// rows yet; the <c>Config:</c>-file value stays the runtime truth until a
/// feature reads through.
/// </summary>
public sealed class AdminConfigStore(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    TimeProvider timeProvider)
{
    /// <summary>Payload cap in bytes (jsonb, verbatim).</summary>
    public const int MaxPayloadBytes = 16 * 1024;

    /// <summary>Versions retained per resource.</summary>
    public const int HistoryRetention = 20;

    /// <summary>Registered resource names (GET /api/admin/config lists these with rowVersions).</summary>
    public static IReadOnlyList<string> Resources { get; } =
    [
        "notifications",
        "retry-policy",
        "nudge-policy",
        "workload-scale",
        "routing-defaults",
        "llm",
        "commands",
        "team-behavior",
        "consent-policy",
        "loop-budget",
        "escalation",
    ];

    /// <summary>Stored view, or null when the resource was never written.</summary>
    public async Task<AdminConfigView?> GetAsync(string resource, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.AdminConfig.AsNoTracking().FirstOrDefaultAsync(c => c.Resource == resource, ct);
        return row is null ? null : ToView(row);
    }

    /// <summary>All stored resources (never 404s — resources without a row are simply absent).</summary>
    public async Task<List<(string Resource, long RowVersion)>> ListAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.AdminConfig.AsNoTracking()
            .OrderBy(c => c.Resource)
            .Select(c => new { c.Resource, c.RowVersion })
            .ToListAsync(ct);
        return rows.Select(r => (r.Resource, r.RowVersion)).ToList();
    }

    /// <summary>
    /// Insert (resource unseen, <paramref name="expectedRowVersion"/> null or 0)
    /// or version-fenced update; one tx writes row + history + retention trim +
    /// audit. Conflict never writes.
    /// </summary>
    /// <param name="resource">Registered resource name.</param>
    /// <param name="payload">Proposed document (validated against the registry shape).</param>
    /// <param name="expectedRowVersion">If-Match version; null = force the write.</param>
    /// <param name="author">Canonical actor for attribution.</param>
    /// <param name="reason">Change reason (validated non-empty by the caller).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<AdminConfigMutationResult> PatchAsync(string resource, JsonElement payload, long? expectedRowVersion, string author, string reason, CancellationToken ct)
    {
        if (!Validators.TryGetValue(resource, out var validator))
        {
            return new AdminConfigMutationResult(AdminConfigOutcome.UnknownResource, null, null);
        }

        var text = payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(text) > MaxPayloadBytes)
        {
            return new AdminConfigMutationResult(AdminConfigOutcome.TooLarge, null,
                $"payload exceeds the {MaxPayloadBytes} byte cap");
        }

        if (validator(payload) is { } validationError)
        {
            return new AdminConfigMutationResult(AdminConfigOutcome.Invalid, null, validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var existing = await db.AdminConfig.FirstOrDefaultAsync(c => c.Resource == resource, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        long storedVersion;
        if (existing is null)
        {
            if (expectedRowVersion > 0)
            {
                return new AdminConfigMutationResult(AdminConfigOutcome.Conflict, null, null);
            }

            storedVersion = 1;
            db.AdminConfig.Add(new AdminConfigEntity
            {
                Resource = resource,
                Payload = text,
                RowVersion = storedVersion,
                Author = author,
                Reason = reason,
                UpdatedAt = now,
            });
        }
        else if (expectedRowVersion is not null && existing.RowVersion != expectedRowVersion)
        {
            return new AdminConfigMutationResult(AdminConfigOutcome.Conflict, ToView(existing), null);
        }
        else
        {
            storedVersion = existing.RowVersion + 1;
            existing.Payload = text;
            existing.RowVersion = storedVersion;
            existing.Author = author;
            existing.Reason = reason;
            existing.UpdatedAt = now;
        }

        db.AdminConfigHistory.Add(new AdminConfigVersionEntity
        {
            Resource = resource,
            Version = storedVersion,
            Payload = text,
            Author = author,
            Reason = reason,
            UpdatedAt = now,
        });

        // Bounded trail: keep the newest 20 versions per resource.
        await db.AdminConfigHistory
            .Where(h => h.Resource == resource && h.Version <= storedVersion - HistoryRetention)
            .ExecuteDeleteAsync(ct);

        audit.Append(db, new AuditRecord(author, "admin.config.patch", $"/api/admin/config/{resource}", "allow", null, null, $"rv{storedVersion}",
            JsonSerializer.Serialize(new { resource, version = storedVersion, reason })));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new AdminConfigMutationResult(AdminConfigOutcome.Stored,
            new AdminConfigView(resource, payload.Clone(), storedVersion, author, reason, now), null);
    }

    /// <summary>History page (newest first, capped by the caller).</summary>
    public async Task<List<AdminConfigHistoryEntry>> GetHistoryAsync(string resource, int limit, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.AdminConfigHistory.AsNoTracking()
            .Where(h => h.Resource == resource)
            .OrderByDescending(h => h.Version)
            .Take(limit)
            .ToListAsync(ct);
        return rows.Select(h => new AdminConfigHistoryEntry(h.Version,
            JsonDocument.Parse(h.Payload).RootElement.Clone(), h.Author, h.Reason, h.UpdatedAt)).ToList();
    }

    private static AdminConfigView ToView(AdminConfigEntity c) =>
        new(c.Resource, JsonDocument.Parse(c.Payload).RootElement.Clone(), c.RowVersion, c.Author, c.Reason, c.UpdatedAt);

    // Closed validation registry — seven known shapes, plain field checks
    // (a schema engine would be more dependency than the problem justifies).
    // Each validator returns null when the payload is valid, else the detail.
    private static readonly Dictionary<string, Func<JsonElement, string?>> Validators = new(StringComparer.Ordinal)
    {
        ["notifications"] = payload => FirstError(
            UnknownFields(payload, "replyVsReaction", "progressVerbosity"),
            EnumField(payload, "replyVsReaction", ["reply", "reaction"]),
            EnumField(payload, "progressVerbosity", ["minimal", "normal", "verbose"])),
        ["retry-policy"] = payload => FirstError(
            UnknownFields(payload, "maxAttempts", "backoffCapSeconds"),
            IntField(payload, "maxAttempts", 1, 20),
            IntField(payload, "backoffCapSeconds", 1, 3600)),
        ["nudge-policy"] = payload => FirstError(
            UnknownFields(payload, "staleAfterMinutes"),
            IntField(payload, "staleAfterMinutes", 1, 1440)),
        ["workload-scale"] = payload => FirstError(
            UnknownFields(payload, "minWorkers", "maxWorkers", "idleShutdownMinutes"),
            IntField(payload, "minWorkers", 0, 8),
            IntField(payload, "maxWorkers", 1, 32),
            IntField(payload, "idleShutdownMinutes", 1, 1440),
            RangeOrder(payload)),
        ["routing-defaults"] = payload => FirstError(
            UnknownFields(payload, "provider", "preferTeam"),
            EnumField(payload, "provider", ["deterministic", "model", ""]),
            OptionalStringField(payload, "preferTeam", 1, 128)),
        // ^ "model" is stored-accepted as the seam for future model routing: no
        // registered implementation falls back deterministic with a model-fallback
        // audit flag (RoutingSink), so no config-schema change lands later.
        ["llm"] = payload => FirstError(
            UnknownFields(payload, "enabled", "verbosityDefault", "replyMode", "decisionEndpoint", "decisionMode", "maxOutputTokens"),
            BoolField(payload, "enabled"),
            EnumField(payload, "verbosityDefault", ["terse", "balanced", "proactive"]),
            EnumField(payload, "replyMode", ["model", "template"]),
            OptionalStringField(payload, "decisionEndpoint", 1, 128),
            EnumField(payload, "decisionMode", ["auto", "llm", "off"]),
            IntField(payload, "maxOutputTokens", 40, 4000)),
        // ^ AiDeck hot policy: absent row = AiDeck inert (NudgeSweeper precedent);
        // secrets stay in Ai: file config, never here.
        ["commands"] = ValidateCommands,
        // ^ Slash/decode command registry — CommandRegistry.Merge reads the
        // stored payload; malformed entries degrade there, this gate keeps
        // garbage off the wire (array shape + verb charset + dup verbs).
        ["team-behavior"] = payload => FirstError(
            UnknownFields(payload, "replyVsReaction", "progressVerbosity", "quiet"),
            EnumField(payload, "replyVsReaction", ["reply", "reaction"]),
            EnumField(payload, "progressVerbosity", ["minimal", "normal", "verbose"]),
            BoolField(payload, "quiet")),
        // ^ Behavior switches a team or a channel may override; every layer is
        // read through the one effective-config resolver, so a switch that is
        // only meaningful per-team still has one system-wide default here.
        ["consent-policy"] = payload => FirstError(
            UnknownFields(payload, "expiresAfterMinutes", "nudgeBeforeMinutes", "maxInFlightPerActor", "groupWindowSeconds"),
            IntField(payload, "expiresAfterMinutes", 1, 10080),
            IntField(payload, "nudgeBeforeMinutes", 1, 10080),
            IntField(payload, "maxInFlightPerActor", 1, 100),
            IntField(payload, "groupWindowSeconds", 0, 3600)),
        ["loop-budget"] = payload => FirstError(
            UnknownFields(payload, "maxExchangesPerConversation", "windowMinutes"),
            IntField(payload, "maxExchangesPerConversation", 1, 1000),
            IntField(payload, "windowMinutes", 1, 1440)),
        ["escalation"] = payload => FirstError(
            UnknownFields(payload, "notifyAfterMinutes", "audience"),
            IntField(payload, "notifyAfterMinutes", 1, 10080),
            EnumField(payload, "audience", ["owner", "team", "operators"])),
    };

    private static string? FirstError(params string?[] errors) => errors.FirstOrDefault(static e => e is not null);

    private static string? UnknownFields(JsonElement payload, params string[] allowed)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return "payload must be a JSON object";
        }

        foreach (var property in payload.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                return $"unknown field '{property.Name}' (allowed: {string.Join('|', allowed)})";
            }
        }

        return null;
    }

    private static string? EnumField(JsonElement payload, string name, string[] allowed)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text || !allowed.Contains(text, StringComparer.Ordinal))
        {
            return $"'{name}' must be one of: {string.Join('|', allowed)}";
        }

        return null;
    }

    private static string? IntField(JsonElement payload, string name, long min, long max)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var number) || number < min || number > max)
        {
            return $"'{name}' must be an integer in [{min}..{max}]";
        }

        return null;
    }

    private static string? StringField(JsonElement payload, string name, int minLen, int maxLen)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } text || text.Length < minLen || text.Length > maxLen)
        {
            return $"'{name}' must be a string of length [{minLen}..{maxLen}]";
        }

        return null;
    }

    private static string? OptionalStringField(JsonElement payload, string name, int minLen, int maxLen) =>
        payload.TryGetProperty(name, out _) ? StringField(payload, name, minLen, maxLen) : null;

    private static string? BoolField(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value)
            || (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False))
        {
            return $"'{name}' must be a boolean";
        }

        return null;
    }

    // Structured validator for `commands` (the scalar helper set can't express
    // arrays): root object, only field `commands`, array of entries with a
    // charset-clean verb + non-empty description, optional capabilityId string,
    // optional args array of {name: string, required: bool}, no dup verbs.
    private static string? ValidateCommands(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            return "payload must be a JSON object";
        }

        if (UnknownFields(payload, "commands") is { } unknown)
        {
            return unknown;
        }

        if (!payload.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array)
        {
            return "payload must carry a 'commands' array";
        }

        var verbs = new List<string>();
        foreach (var entry in commands.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return "'commands[]' entries must be JSON objects";
            }

            if (StringField(entry, "verb", 1, 128) is { } verbError)
            {
                return verbError;
            }

            var verb = entry.GetProperty("verb").GetString()!;
            if (!Facilitator.Kernel.Ai.CommandRegistry.VerbPattern.IsMatch(verb))
            {
                return "'commands[].verb' must match [a-z0-9-]+ (no leading slash)";
            }

            if (verbs.Contains(verb, StringComparer.OrdinalIgnoreCase))
            {
                return $"'commands[].verb' duplicated: '{verb}'";
            }

            verbs.Add(verb);

            if (entry.EnumerateObject().Any(p => p.Name is not ("verb" or "description" or "capabilityId" or "args")))
            {
                return $"'commands[]' entry '{verb}' has unknown fields";
            }

            if (StringField(entry, "description", 1, 512) is { } descriptionError)
            {
                return descriptionError;
            }

            if (entry.TryGetProperty("capabilityId", out var capability) && capability.ValueKind != JsonValueKind.String)
            {
                return $"'commands[].capabilityId' must be a string when present";
            }

            if (entry.TryGetProperty("args", out var args))
            {
                if (args.ValueKind != JsonValueKind.Array)
                {
                    return $"'commands[].args' must be an array";
                }

                foreach (var arg in args.EnumerateArray())
                {
                    if (arg.ValueKind != JsonValueKind.Object)
                    {
                        return "'commands[].args' entries must be JSON objects";
                    }

                    if (arg.EnumerateObject().Any(p => p.Name is not ("name" or "required")))
                    {
                        return "'commands[].args' entries carry unknown fields";
                    }

                    if (StringField(arg, "name", 1, 128) is { } nameError)
                    {
                        return nameError;
                    }

                    if (BoolField(arg, "required") is { } requiredError)
                    {
                        return requiredError;
                    }
                }
            }
        }

        return null;
    }

    private static string? RangeOrder(JsonElement payload)
    {
        if (payload.TryGetProperty("minWorkers", out var min) && min.TryGetInt64(out var lo)
            && payload.TryGetProperty("maxWorkers", out var max) && max.TryGetInt64(out var hi)
            && lo > hi)
        {
            return "'minWorkers' must not exceed 'maxWorkers'";
        }

        return null;
    }
}
