using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Ai;

/// <summary>Hot AiDeck policy (the <c>admin_config</c> <c>llm</c> row) joined
/// with the file-config endpoints. An absent row ⇒ AiDeck inert: decode never
/// runs, slash plane only (the NudgeSweeper precedent). A present row with
/// <c>enabled: false</c> is likewise inert. Secrets stay in file/env; this row
/// carries only policy.</summary>
public sealed record AiPolicy(
    bool Enabled,
    string VerbosityDefault,
    string ReplyMode,
    string? DecisionEndpoint,
    string DecisionMode,
    int MaxOutputTokens)
{
    /// <summary>Level 1 (decisions API) usable: decision endpoint configured
    /// in file config AND the row allows it (auto/llm semantics resolved by
    /// the decision points).</summary>
    public bool DecisionsApiUsable(AiOptions options) =>
        Enabled && DecisionMode != "off"
        && DecisionEndpoint is { Length: > 0 } name && options.Endpoint(name) is not null;
}

/// <summary>Loads the AI policy rows (llm config, actor preferences) — the
/// single read path so every decision point sees identical semantics.</summary>
public sealed class AiPolicyStore(IDbContextFactory<FacilitatorDbContext> contextFactory)
{
    /// <summary>Default policy when the row is absent: inert.</summary>
    public static readonly AiPolicy Inert = new(false, "balanced", "template", null, "auto", 600);

    /// <summary>Read + parse the <c>llm</c> row; absent/undecodable ⇒ inert.</summary>
    public async Task<AiPolicy> LoadAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == "llm")
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        return Parse(payload);
    }

    /// <summary>Read + parse the <c>llm</c> row on a fresh context (callers
    /// outside a business tx).</summary>
    public async Task<AiPolicy> LoadAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await LoadAsync(db, ct);
    }

    /// <summary>Actor verbosity preference, or null when unmapped (caller
    /// falls to <see cref="AiPolicy.VerbosityDefault"/>).</summary>
    public async Task<string?> ActorVerbosityAsync(FacilitatorDbContext db, string actor, CancellationToken ct) =>
        await db.ActorPreferences.AsNoTracking()
            .Where(p => p.Actor == actor)
            .Select(p => p.Verbosity)
            .FirstOrDefaultAsync(ct);

    internal static AiPolicy Parse(string? payload)
    {
        if (payload is not { Length: > 0 } text)
        {
            return Inert;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var verbosity = Str(root, "verbosityDefault");
            var replyMode = Str(root, "replyMode");
            var decisionMode = Str(root, "decisionMode");
            return new AiPolicy(
                root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.True,
                verbosity is "terse" or "balanced" or "proactive" ? verbosity : "balanced",
                replyMode is "model" or "template" ? replyMode : "template",
                Str(root, "decisionEndpoint"),
                decisionMode is "auto" or "llm" or "off" ? decisionMode : "auto",
                root.TryGetProperty("maxOutputTokens", out var tok) && tok.TryGetInt32(out var t) && t is >= 40 and <= 4000 ? t : 600);
        }
        catch (JsonException)
        {
            return Inert;
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
