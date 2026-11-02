using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Facilitator.Kernel.Shared.Memory;

/// <summary>Provider-private write: the broker resolved the scope, the ACL, and the row.</summary>
/// <param name="Row">Fully-populated entry row (the broker owns identity + metadata).</param>
public sealed record AuthorizedMemoryWrite(MemoryEntryEntity Row);

/// <summary>Provider-private search: the broker resolved the caller's team once; the provider embeds it in the SQL predicate (D2 — ACL before search, never a post-filter).</summary>
/// <param name="Query">Raw user query text (data, never syntax — <c>websearch_to_tsquery</c> is injection-safe by construction).</param>
/// <param name="CallerTeam">The caller's team slug; <c>""</c> for a team-less caller (sees only facilitator-visible rows).</param>
/// <param name="IncludeSuperseded">Widen the status filter to superseded revisions.</param>
/// <param name="ScopeIds">When non-empty, restrict to these resolved scope ids (selector-hint intersection).</param>
/// <param name="Limit">Rows to fetch (the +1 truncated-detection row is the caller's budget).</param>
public sealed record AuthorizedMemorySearch(
    string Query,
    string CallerTeam,
    bool IncludeSuperseded,
    IReadOnlyList<Guid> ScopeIds,
    int Limit);

/// <summary>One FTS hit: identity + the backend's relevance score (surfaced as <c>retrievalReason</c>).</summary>
public sealed record MemorySearchHit(Guid EntryId, double Rank);

/// <summary>Provider-private deletion (D5): tombstone + content drop + share revocation, guarded on live status.</summary>
/// <param name="EntryId">The entry to tombstone.</param>
/// <param name="Now">Pass timestamp (shared by the row update and the share stamps).</param>
public sealed record AuthorizedMemoryDeletion(Guid EntryId, DateTime Now);

/// <summary>
/// The provider seam: the broker authorizes, the provider stores /
/// searches / deletes inside the broker's transaction. <c>postgres-fts</c> is
/// the only implementation (D10); a Hindsight adapter lands behind
/// this same port — never as an empty class.
/// </summary>
public interface IMemoryProvider
{
    /// <summary>Persist the row into the broker's open context (the broker saves).</summary>
    Task StoreAsync(FacilitatorDbContext db, AuthorizedMemoryWrite write, CancellationToken ct);

    /// <summary>Run the ranked, ACL-filtered search; the predicate lives here, backend-flavored.</summary>
    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(FacilitatorDbContext db, AuthorizedMemorySearch search, CancellationToken ct);

    /// <summary>Apply the D5 deletion law; returns false when the row is not live (guarded).</summary>
    Task<bool> DeleteAsync(FacilitatorDbContext db, AuthorizedMemoryDeletion deletion, CancellationToken ct);
}

/// <summary>
/// The reference profile: the kernel's own Postgres is the store
/// and the index. Lexical <c>websearch_to_tsquery</c> over the stored tsvector
/// generated column, ranked by <c>ts_rank</c>; the D2 predicate — owner team,
/// facilitator-visible, live share, with the private cross-team block — is
/// plain SQL, so no row a caller may not read ever leaves the database.
/// </summary>
public sealed class PostgresFtsMemoryProvider : IMemoryProvider
{
    /// <inheritdoc />
    public Task StoreAsync(FacilitatorDbContext db, AuthorizedMemoryWrite write, CancellationToken ct)
    {
        db.MemoryEntries.Add(write.Row);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemorySearchHit>> SearchAsync(FacilitatorDbContext db, AuthorizedMemorySearch search, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = BuildSearchSql(search.ScopeIds.Count > 0);
        AddText(command, "q", search.Query);
        AddText(command, "team", search.CallerTeam);
        AddBool(command, "superseded", search.IncludeSuperseded);
        AddInt(command, "limit", search.Limit);
        if (search.ScopeIds.Count > 0)
        {
            var p = command.CreateParameter();
            p.ParameterName = "scopes";
            p.Value = search.ScopeIds.ToArray();
            command.Parameters.Add(p);
        }

        var hits = new List<MemorySearchHit>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            hits.Add(new MemorySearchHit(reader.GetGuid(0), reader.GetDouble(1)));
        }

        return hits;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(FacilitatorDbContext db, AuthorizedMemoryDeletion deletion, CancellationToken ct)
    {
        // D5 in one guarded update: content drop (which recomputes the tsvector
        // — the row can never match a search again), tombstone, and the share
        // revocation rides the same tx through MemoryBroker.
        var dropped = await db.MemoryEntries
            .Where(e => e.EntryId == deletion.EntryId && e.Status != "tombstoned")
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, "tombstoned")
                .SetProperty(e => e.Content, string.Empty), ct);
        if (dropped == 0)
        {
            return false;
        }

        await db.MemoryShares
            .Where(s => s.EntryId == deletion.EntryId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, deletion.Now), ct);
        return true;
    }

    private static string BuildSearchSql(bool scopeFiltered) =>
        $$"""
        SELECT e.entry_id, ts_rank(e.search, q)
        FROM facilitator.memory_entries e, websearch_to_tsquery('english', @q) q
        WHERE e.search @@ q
          AND e.status <> 'tombstoned'
          AND (e.status IN ('candidate', 'active') OR (@superseded AND e.status = 'superseded'))
          AND (e.team_slug = @team
               OR e.policy_label = 'facilitator-visible'
               OR EXISTS (SELECT 1 FROM facilitator.memory_shares s
                          WHERE s.entry_id = e.entry_id AND s.grantee_team_slug = @team AND s.revoked_at IS NULL))
          AND (e.policy_label <> 'private'
               OR e.team_slug = @team
               OR EXISTS (SELECT 1 FROM facilitator.memory_shares s
                          WHERE s.entry_id = e.entry_id AND s.grantee_team_slug = @team AND s.revoked_at IS NULL))
        {{(scopeFiltered ? "  AND e.scope_id = ANY(@scopes)\n" : "")}}        ORDER BY ts_rank(e.search, q) DESC, e.observed_at DESC
        LIMIT @limit
        """;

    private static void AddText(System.Data.Common.DbCommand command, string name, string value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private static void AddBool(System.Data.Common.DbCommand command, string name, bool value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }

    private static void AddInt(System.Data.Common.DbCommand command, string name, int value)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        command.Parameters.Add(p);
    }
}
