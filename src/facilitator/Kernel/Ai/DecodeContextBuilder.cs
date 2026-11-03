using Facilitator.Kernel.Shared.Memory;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Ai;

/// <summary>
/// The decode prompt's facts: conversation memory (recent ledger turns),
/// top memory-recall hits, and Work state — deterministic order, size-capped.
/// Every read degrades gracefully: no team ⇒ no memory query; broker throws ⇒
/// no hits; unbound ⇒ no Work facts. Context is best-effort, never a failure
/// source for the decode.
/// </summary>
/// <param name="Turns">Recent conversation turns (chronological): speaker key + text.</param>
/// <param name="MemoryHits">Recalled memory contents (broker order = relevance).</param>
/// <param name="WorkFacts">Bound Work state + last events, null when unbound.</param>
/// <param name="RenderedBlock">The headed, capped block injected into the decode template.</param>
public sealed record DecodeContext(
    IReadOnlyList<(string Who, string What)> Turns,
    IReadOnlyList<string> MemoryHits,
    string? WorkFacts,
    string RenderedBlock);

/// <summary>
/// Builds the <see cref="DecodeContext"/> for one message: ledger memory
/// window, postgres-fts recall (purpose <c>ai-decode</c>, team-scope), and
/// Work facts, rendered under explicit headings and capped to 4 KiB,
/// dropping oldest turns first.
/// </summary>
public sealed class DecodeContextBuilder(
    MemoryBroker broker,
    AiPolicyStore policyStore,
    IDbContextFactory<FacilitatorDbContext> contextFactory)
{
    /// <summary>Rendered-block ceiling (bytes of UTF-8, oldest turns drop first).</summary>
    public const int MaxBlockBytes = 4096;

    /// <summary>Recall page size (top hits by the provider's ranking).</summary>
    public const int MemoryTopK = 3;

    /// <summary>
    /// Assemble the context for one decode. All reads ride the caller's
    /// context except the broker recall (its own transaction, broker law).
    /// </summary>
    /// <param name="db">Caller's context.</param>
    /// <param name="conversationKey">Ledger conversation key (provider|channel|root).</param>
    /// <param name="actorKey">Canonical oidc actor when mapped (memory consumer identity), else null.</param>
    /// <param name="providerTenant">Provider tenant of the conversation (audit identity only).</param>
    /// <param name="teamId">Owning workload team; null/unresolvable ⇒ no memory recall.</param>
    /// <param name="messageText">The message being decoded (recall query).</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<DecodeContext> BuildAsync(
        FacilitatorDbContext db,
        string conversationKey,
        string? actorKey,
        string? providerTenant,
        Guid? teamId,
        string messageText,
        CancellationToken ct)
    {
        _ = policyStore;
        _ = providerTenant;
        _ = contextFactory;
        var turns = (await AiLedgerStore.RecentAsync(db, conversationKey, AiLedgerStore.MemoryWindow, ct))
            .Select(t => (Who: t.Direction == "in" ? t.ProviderAccountId ?? t.Actor ?? "human" : "bot", What: t.Text))
            .ToList();

        var teamSlug = teamId is { } team
            ? await db.Teams.AsNoTracking().Where(t => t.TeamId == team).Select(t => t.Slug).FirstOrDefaultAsync(ct)
            : null;

        var hits = new List<string>();
        if (teamSlug is { Length: > 0 })
        {
            try
            {
                var response = await broker.RecallAsync(
                    new MemoryAccessContext(
                        actorKey ?? $"link:decode:{teamSlug}",
                        teamSlug,
                        new HashSet<string>(StringComparer.Ordinal),
                        IsWorkload: true),
                    new MemoryRecallRequest(messageText, "ai-decode", MaxItems: MemoryTopK, MaxTokens: 900),
                    ct);
                hits.AddRange(response.Entries.Select(e => e.Content));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Memory is an enhancement: an outage (or ACL refusal) leaves
                // the decode with just conversation + Work facts.
            }
        }

        var workFacts = await WorkFactsAsync(db, conversationKey, ct);
        var rendered = Render(turns, hits, workFacts);
        return new DecodeContext(turns, hits, workFacts, rendered);
    }

    /// <summary>Bound Work (by the conversation key's provider|channel|root):
    /// state/version + last three timeline events, deterministic order.</summary>
    private static async Task<string?> WorkFactsAsync(FacilitatorDbContext db, string conversationKey, CancellationToken ct)
    {
        var parts = conversationKey.Split('|');
        if (parts.Length != 3)
        {
            return null;
        }

        var workId = await db.CommunicationBindings.AsNoTracking()
            .Where(b => b.Provider == parts[0] && b.Channel == parts[1] && b.RootMessageId == parts[2])
            .Select(b => b.WorkId)
            .FirstOrDefaultAsync(ct);
        if (workId is not { } id)
        {
            return null;
        }

        var work = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == id)
            .Select(w => new { w.State, w.Version })
            .FirstOrDefaultAsync(ct);
        if (work is null)
        {
            return null;
        }

        var events = await db.WorkEvents.AsNoTracking()
            .Where(e => e.WorkId == id)
            .OrderByDescending(e => e.WorkVersion).ThenByDescending(e => e.CreatedAt)
            .Take(3)
            .Select(e => new { e.Kind, e.CreatedAt })
            .ToListAsync(ct);
        events.Reverse();

        var text = $"Work {id:N} is {work.State} (version {work.Version}).";
        if (events.Count > 0)
        {
            text += " Recent timeline: " + string.Join("; ",
                events.Select(e => $"{e.CreatedAt:yyyy-MM-dd HH:mm} {e.Kind}"));
        }

        return text;
    }

    // Deterministic render, 4 KiB cap: sections fixed, memory fixed,
    // turns are the elastic part — oldest drop first.
    private static string Render(List<(string Who, string What)> turns, List<string> hits, string? workFacts)
    {
        var tail = string.Empty;
        if (workFacts is not null)
        {
            tail += "WORK FACTS\n" + workFacts + "\n\n";
        }

        if (hits.Count > 0)
        {
            tail += "MEMORY\n" + string.Join("\n", hits.Select(h => "- " + OneLine(h))) + "\n\n";
        }

        var included = 0;
        string Block()
        {
            var head = included == 0 ? string.Empty : "CONVERSATION (oldest first)\n";
            for (var i = turns.Count - included; i < turns.Count; i++)
            {
                head += $"{turns[i].Who}: {OneLine(turns[i].What)}\n";
            }

            return head.TrimEnd() is { Length: > 0 } b ? b + "\n\n" + tail : tail.TrimEnd('\n');
        }

        while (included < turns.Count && System.Text.Encoding.UTF8.GetByteCount(Block()) > MaxBlockBytes)
        {
            included++;
        }

        return Block();
    }

    private static string OneLine(string text) =>
        text.ReplaceLineEndings(" ").Replace('\t', ' ').Trim();
}
