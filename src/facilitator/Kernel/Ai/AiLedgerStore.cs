using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Ai;

/// <summary>One ledger append (direction 'in' = a decoded message,
/// 'out' = a composed reply). Every AI touch writes one of these in the same
/// tx as its effect — the ledger is both conversation memory and the audit
/// of what the model saw and said.</summary>
public sealed record AiTurnAppend(
    string ConversationKey,
    string? Actor,
    string? ProviderAccountId,
    string Direction,
    string Text,
    string Basis,
    string? TemplateId = null,
    JsonElement? Decoded = null,
    string? EndpointName = null,
    int? PromptTokens = null,
    int? CompletionTokens = null,
    string? TraceId = null);

/// <summary>facilitator.ai_turns writes/reads: the ledger append helper and
/// the conversation-memory read (chronological, recency-capped).</summary>
public sealed class AiLedgerStore
{
    /// <summary>Conversation memory window (context builder's recency list).</summary>
    public const int MemoryWindow = 8;

    /// <summary>Append inside the caller's tx/context (same-tx law with the effect).</summary>
    public static void Append(FacilitatorDbContext db, AiTurnAppend turn, DateTime now) =>
        db.AiTurns.Add(new AiTurnEntity
        {
            TurnId = Guid.NewGuid(),
            OccurredAt = now,
            ConversationKey = turn.ConversationKey,
            Actor = turn.Actor,
            ProviderAccountId = turn.ProviderAccountId,
            Direction = turn.Direction,
            Text = turn.Text,
            Basis = turn.Basis,
            TemplateId = turn.TemplateId,
            Decoded = turn.Decoded is { } d ? d.GetRawText() : null,
            EndpointName = turn.EndpointName,
            PromptTokens = turn.PromptTokens,
            CompletionTokens = turn.CompletionTokens,
            TraceId = turn.TraceId ?? System.Diagnostics.Activity.Current?.TraceId.ToString(),
        });

    /// <summary>Last <paramref name="limit"/> turns for a conversation key,
    /// chronological (deterministic: occurred_at, then turn_id).</summary>
    public static async Task<List<AiTurnEntity>> RecentAsync(
        FacilitatorDbContext db, string conversationKey, int limit, CancellationToken ct)
    {
        var rows = await db.AiTurns.AsNoTracking()
            .Where(t => t.ConversationKey == conversationKey)
            .OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.TurnId)
            .Take(limit)
            .ToListAsync(ct);
        rows.Reverse();
        return rows;
    }

    /// <summary>Provider-plane conversation key: provider|channel|root.</summary>
    public static string ProviderKey(string provider, string channel, string root) =>
        $"{provider}|{channel}|{root}";

    /// <summary>Chat-plane conversation key.</summary>
    public static string ChatKey(Guid sessionId) => $"chat:{sessionId:N}";
}
