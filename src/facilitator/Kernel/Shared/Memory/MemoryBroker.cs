using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Facilitator.Kernel.Shared.Dispatch;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Memory;

/// <summary>Machine outcome of a broker operation; every non-Ok maps to a typed problem (or a swallowed audit-deny on the event plane).</summary>
public enum MemoryOpOutcome
{
    /// <summary>Done.</summary>
    Ok,

    /// <summary>Unknown, tombstoned, or foreign-team entry — the one 404 answer (non-disclosure).</summary>
    NotFound,

    /// <summary>The selector resolves to a scope the caller's team does not own (or is unresolvable on a write).</summary>
    ScopeInvalid,

    /// <summary>The team's UTC-day retain budget is spent (D9).</summary>
    QuotaExceeded,

    /// <summary>A share contradicts the law (private entries are unsharable) or names a team the roster lacks.</summary>
    ShareDenied,

    /// <summary>The share row is unknown, already revoked, or off-entry.</summary>
    ShareNotFound,

}

/// <summary>Normalized provenance for a retain (mirrors the frozen candidate sub-schema plus work/execution linkage).</summary>
public sealed record MemoryProvenanceInput(
    string Source,
    string EvidenceRef,
    string CreatedBy,
    DateTime CreatedAt,
    string? WorkId = null,
    string? ExecutionId = null,
    string? SourceDigest = null);

/// <summary>One retain: the broker resolves the selector hint, enforces quota + dedupe, and commits in-tx (D1 — synchronous).</summary>
public sealed record MemoryRetainRequest(
    string ScopeSelector,
    string Type,
    string Content,
    MemoryProvenanceInput Provenance,
    string? MediaType = null,
    string? ContentSummary = null,
    string? PolicyLabel = null,
    string? Classification = null,
    DateTime? ObservedAt = null);

/// <summary>The retain receipt: entry identity, landed status, and whether dedupe returned an existing row.</summary>
public sealed record MemoryRetainReceipt(Guid EntryId, string Status, bool Duplicate);

/// <summary>Result of a retain.</summary>
public sealed record MemoryRetainResult(MemoryOpOutcome Outcome, MemoryRetainReceipt? Receipt = null);

/// <summary>One recall request; <c>scopeSelectors</c> are hints the broker intersects with verified grants (never the reverse).</summary>
public sealed record MemoryRecallRequest(
    string Query,
    string Purpose,
    string? WorkId = null,
    IReadOnlyList<string>? ScopeSelectors = null,
    int? MaxItems = null,
    int? MaxTokens = null,
    bool IncludeSuperseded = false);

/// <summary>One recalled entry with the §6.3 metadata law: provenance + policy label ride every hit; <c>evidence-only</c> rows surface the summary where the caller may see, full text only via evidence (D2).</summary>
public sealed record MemoryRecallEntry(
    Guid EntryId,
    string ScopeId,
    string Type,
    string Content,
    string ContentHash,
    JsonElement Provenance,
    DateTime ObservedAt,
    DateTime? EffectiveFrom,
    int Revision,
    string Status,
    string PolicyLabel,
    string TeamSlug,
    string RetrievalReason);

/// <summary>The D8 response: §14.11 verbatim field names.</summary>
public sealed record MemoryRecallResponse(
    string RequestId,
    string ProviderProfile,
    IReadOnlyList<MemoryRecallEntry> Entries,
    bool Truncated,
    DateTime AsOf);

/// <summary>Read projection of one entry (GET surface): entry + live shares + usage count.</summary>
public sealed record MemoryEntryView(
    Guid EntryId,
    string ScopeId,
    string Type,
    string Content,
    string MediaType,
    string ContentSummary,
    string ContentHash,
    JsonElement Provenance,
    DateTime ObservedAt,
    DateTime? EffectiveFrom,
    string Classification,
    string PolicyLabel,
    string Status,
    int Revision,
    Guid? SupersedesEntryId,
    DateTime? ExpiresAt,
    DateTime CreatedAt,
    string CreatedBy,
    IReadOnlyList<(Guid ShareId, string Grantee, DateTime CreatedAt)> Shares,
    long UsageCount);

/// <summary>
/// The memory broker (Plan 16, spec §14.3): the only writer of the memory
/// tables and the only authorizer. Access context is built server-side from a
/// verified principal — a selector in a payload is a hint, a
/// <c>callerPrincipal</c> in a payload cannot exist (schema-rejected). Every
/// operation is one short transaction with same-tx audit; the ACL predicate
/// runs inside the provider's SQL (D2), so a denial discloses nothing — the
/// one 404 answer, no counts of hidden rows.
/// </summary>
public sealed class MemoryBroker(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    AuditWriter audit,
    IMemoryProvider provider,
    KernelMetrics metrics,
    TimeProvider timeProvider,
    MemoryOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Policy = "memory-v1";
    private static readonly string[] Types =
        ["observed_fact", "experience", "decision", "lesson_candidate", "lesson_verified", "hypothesis", "preference"];
    private static readonly string[] PolicyLabels =
        ["private", "team-only", "facilitator-visible", "evidence-only"];

    /// <summary>Retain one entry (D1 synchronous): scope resolution → quota → dedupe → provider store → audit, one tx.</summary>
    public async Task<MemoryRetainResult> RetainAsync(
        MemoryAccessContext caller, MemoryRetainRequest request, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var contentTooLong = request.Content.Length > options.Quota.MaxContentChars;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var (scope, denial) = await ResolveWriteScopeAsync(db, caller, request.ScopeSelector, now, ct);
        if (scope is null)
        {
            audit.Append(db, Deny(caller, "memory.denied", $"memory/{request.ScopeSelector}", denial));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.MemoryDenied(denial);
            return new MemoryRetainResult(MemoryOpOutcome.ScopeInvalid);
        }

        if (contentTooLong)
        {
            // Defense in depth behind the schema maxLength: the typed 422.
            audit.Append(db, Deny(caller, "memory.denied", $"memory/{scope.CanonicalSelector}", "content-too-long"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.MemoryDenied("content-too-long");
            return new MemoryRetainResult(MemoryOpOutcome.ScopeInvalid);
        }

        // D9 quota: rows this team created since the UTC-day boundary.
        var dayStart = now.Date;
        var today = await db.MemoryEntries.CountAsync(
            e => e.TeamSlug == caller.TeamSlug && e.CreatedAt >= dayStart, ct);
        if (today >= options.Quota.DailyRetainPerTeam)
        {
            audit.Append(db, Deny(caller, "memory.quota-exceeded", $"memory/{scope.CanonicalSelector}", "daily-retain-budget"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.MemoryDenied("quota");
            return new MemoryRetainResult(MemoryOpOutcome.QuotaExceeded);
        }

        // D9 dedupe: same scope, same content hash, still alive — return the
        // existing identity, no new row (a retry-safe no-op, not a rejection).
        var hash = Sha256Of(request.Content);
        var existing = await db.MemoryEntries.AsNoTracking().FirstOrDefaultAsync(
            e => e.ScopeId == scope.ScopeId && e.ContentHash == hash && e.Status != "tombstoned", ct);
        if (existing is not null)
        {
            audit.Append(db, Allow(caller, "memory.retained", $"memory/{existing.EntryId}", "duplicate",
                new Dictionary<string, string?> { ["scope"] = scope.CanonicalSelector, ["dedupe"] = "content-hash" }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.MemoryRetained(existing.Status, Origin(caller));
            return new MemoryRetainResult(MemoryOpOutcome.Ok, new MemoryRetainReceipt(existing.EntryId, existing.Status, true));
        }

        var status = caller.IsWorkload ? "candidate" : "active";
        var expiresAt = await CandidateExpiryAsync(db, scope, status, now, ct);
        var row = new MemoryEntryEntity
        {
            EntryId = Guid.NewGuid(),
            ScopeId = scope.ScopeId,
            TeamSlug = scope.TeamSlug,
            Type = request.Type,
            Content = request.Content,
            MediaType = request.MediaType ?? "text/plain",
            ContentSummary = request.ContentSummary ?? Summarize(request.Content),
            ContentHash = hash,
            ProvenanceJson = ProvenanceOf(request.Provenance),
            ObservedAt = request.ObservedAt?.ToUniversalTime() ?? now,
            Classification = request.Classification ?? "internal",
            PolicyLabel = request.PolicyLabel ?? "team-only",
            Status = status,
            Revision = 1,
            ExpiresAt = expiresAt,
            CreatedAt = now,
            CreatedBy = caller.Actor,
        };
        await provider.StoreAsync(db, new AuthorizedMemoryWrite(row), ct);
        audit.Append(db, Allow(caller, "memory.retained", $"memory/{row.EntryId}", "retained",
            new Dictionary<string, string?>
            {
                ["scope"] = scope.CanonicalSelector,
                ["type"] = row.Type,
                ["policyLabel"] = row.PolicyLabel,
                ["status"] = status,
                ["contentHash"] = hash,
            }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.MemoryRetained(status, Origin(caller));
        return new MemoryRetainResult(MemoryOpOutcome.Ok, new MemoryRetainReceipt(row.EntryId, status, false));
    }

    /// <summary>Recall (D2 + D3): the ACL predicate runs inside the provider SQL; every returned entry gets a <c>memory_usage</c> row in the recall tx.</summary>
    public async Task<MemoryRecallResponse> RecallAsync(
        MemoryAccessContext caller, MemoryRecallRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var maxItems = Math.Clamp(request.MaxItems ?? 8, 1, 20);
        var ignored = 0;
        List<Guid> scopeIds = [];
        if (request.ScopeSelectors is { Count: > 0 } selectors)
        {
            foreach (var hint in selectors.Take(8))
            {
                var selector = await ResolveReadableScopeAsync(db, caller, hint, ct);
                if (selector is null)
                {
                    ignored++;
                }
                else if (!scopeIds.Contains(selector.ScopeId))
                {
                    scopeIds.Add(selector.ScopeId);
                }
            }
        }

        var hits = await provider.SearchAsync(db, new AuthorizedMemorySearch(
            request.Query, caller.TeamSlug ?? string.Empty, request.IncludeSuperseded,
            scopeIds, maxItems + 1), ct);
        var truncated = hits.Count > maxItems;
        var page = hits.Take(maxItems).ToList();
        var entries = await LoadRecallEntriesAsync(db, page, now, ct);

        // D8 maxTokens: a chars/4 budget heuristic, dropped whole tail-first;
        // truncation reports honestly.
        if (request.MaxTokens is { } budget)
        {
            var tokens = entries.Sum(e => e.Content.Length / 4 + 1);
            while (entries.Count > 0 && tokens > budget)
            {
                tokens -= entries[^1].Content.Length / 4 + 1;
                entries.RemoveAt(entries.Count - 1);
                truncated = true;
            }
        }

        foreach (var entry in entries)
        {
            db.MemoryUsage.Add(new MemoryUsageEntity
            {
                UsageId = Guid.NewGuid(),
                EntryId = entry.EntryId,
                Consumer = caller.Actor,
                Purpose = request.Purpose,
                Justification = $"{request.Purpose}: {Truncate(request.Query, 240)}",
                WorkId = Guid.TryParse(request.WorkId, out var w) ? w : null,
                CreatedAt = now,
            });
            metrics.MemoryUsed();
        }

        audit.Append(db, Allow(caller, "memory.recall", $"memory/recall?{request.Purpose}", "recalled",
            new Dictionary<string, string?>
            {
                ["purpose"] = request.Purpose,
                ["results"] = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["truncated"] = truncated ? "true" : "false",
                ["selectors-ignored"] = ignored > 0 ? ignored.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            }));
        audit.Append(db, Allow(caller, "memory.used", $"memory/recall?{request.Purpose}", "auto-usage",
            new Dictionary<string, string?> { ["entries"] = string.Join(',', entries.Select(e => e.EntryId)) }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.MemoryRecalled(truncated ? "true" : "false");
        metrics.MemoryRecallLatency(ElapsedMs(started));
        return new MemoryRecallResponse(
            Guid.NewGuid().ToString("N"), options.Provider, entries, truncated, now);
    }

    /// <summary>One entry, ACL-filtered: null (⇒ 404 <c>memory-not-found</c>) for unknown, tombstoned, or unreadable — one answer, no disclosure.</summary>
    public async Task<MemoryEntryView?> GetAsync(MemoryAccessContext caller, Guid entryId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var row = await db.MemoryEntries.AsNoTracking().FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
        if (row is null || !await MayReadAsync(db, row, caller, ct))
        {
            return null;
        }

        var shares = await db.MemoryShares.AsNoTracking()
            .Where(s => s.EntryId == entryId && s.RevokedAt == null)
            .OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var usage = await db.MemoryUsage.AsNoTracking().CountAsync(u => u.EntryId == entryId, ct);
        var selector = await db.MemoryScopes.AsNoTracking()
            .Where(s => s.ScopeId == row.ScopeId).Select(s => s.CanonicalSelector).FirstOrDefaultAsync(ct)
            ?? string.Empty;
        return View(row, selector, shares, usage);
    }

    /// <summary>Promote (owner-team review, §14.6): candidate→active in place. <paramref name="targetSelector"/> is accepted only as the owner team's <c>team:</c> selector (v1 law); timeline note when the entry is work-linked.</summary>
    public async Task<MemoryOpOutcome> PromoteAsync(
        MemoryAccessContext caller, Guid entryId, string? targetSelector, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.MemoryEntries.FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
        if (row is null || row.Status == "tombstoned" || !OwnerTeam(row, caller))
        {
            return await RefuseAsync(db, tx, caller, entryId, "memory.denied", "promote-unreadable", ct);
        }

        if (targetSelector is not null && !IsOwnTeamSelector(targetSelector, row.TeamSlug))
        {
            // The entry is real and owned; the TARGET contradicts the v1 law —
            // a typed 422, not the disclosure-free 404.
            audit.Append(db, Deny(caller, "memory.denied", $"memory/{entryId}", "promote-target-invalid"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.MemoryDenied("promote-target-invalid");
            return MemoryOpOutcome.ScopeInvalid;
        }

        var flipped = await db.MemoryEntries
            .Where(e => e.EntryId == entryId && e.Status == "candidate")
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, "active")
                .SetProperty(e => e.ExpiresAt, (DateTime?)null), ct);
        if (flipped == 0)
        {
            return await RefuseAsync(db, tx, caller, entryId, "memory.denied", "promote-not-candidate", ct);
        }

        audit.Append(db, Allow(caller, "memory.promoted", $"memory/{entryId}", "promoted",
            new Dictionary<string, string?> { ["target"] = targetSelector, ["scope"] = await SelectorOfAsync(db, row.ScopeId, ct) }));
        var workId = WorkIdOf(row);
        if (workId is not null)
        {
            await AppendTimelineAsync(db, workId.Value, "memory.promoted", caller.Actor, entryId, now, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.MemoryPromoted();
        return MemoryOpOutcome.Ok;
    }

    /// <summary>Correct (§14.6): a revision+1 row supersedes the old one in the same tx; the chain links via <c>supersedes_entry_id</c>. Curation stays REST (D4).</summary>
    public async Task<MemoryOpOutcome> CorrectAsync(
        MemoryAccessContext caller, Guid entryId, string content, string? contentSummary, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var old = await db.MemoryEntries.AsNoTracking().FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
        if (old is null || old.Status == "tombstoned" || !OwnerTeam(old, caller))
        {
            return await RefuseAsync(db, tx, caller, entryId, "memory.denied", "correct-unreadable", ct);
        }

        if (content.Length > options.Quota.MaxContentChars)
        {
            return await RefuseAsync(db, tx, caller, entryId, "memory.denied", "content-too-long", ct);
        }

        var superseded = await db.MemoryEntries
            .Where(e => e.EntryId == entryId && e.Status != "tombstoned")
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "superseded"), ct);
        if (superseded == 0)
        {
            return await RefuseAsync(db, tx, caller, entryId, "memory.denied", "correct-tombstoned", ct);
        }

        var revision = await db.MemoryEntries
            .Where(e => e.ScopeId == old.ScopeId && e.ContentHash == old.ContentHash)
            .Select(e => (int?)e.Revision).MaxAsync(ct) ?? 1;
        var row = new MemoryEntryEntity
        {
            EntryId = Guid.NewGuid(),
            ScopeId = old.ScopeId,
            TeamSlug = old.TeamSlug,
            Type = old.Type,
            Content = content,
            MediaType = old.MediaType,
            ContentSummary = contentSummary ?? Summarize(content),
            ContentHash = Sha256Of(content),
            ProvenanceJson = old.ProvenanceJson,
            ObservedAt = old.ObservedAt,
            EffectiveFrom = now,
            Classification = old.Classification,
            PolicyLabel = old.PolicyLabel,
            // A correction by an owner is a review: the revision lands active.
            Status = "active",
            Revision = revision + 1,
            SupersedesEntryId = old.EntryId,
            ExpiresAt = null,
            CreatedAt = now,
            CreatedBy = caller.Actor,
        };
        await provider.StoreAsync(db, new AuthorizedMemoryWrite(row), ct);
        audit.Append(db, Allow(caller, "memory.corrected", $"memory/{row.EntryId}", "corrected",
            new Dictionary<string, string?> { ["supersedes"] = entryId.ToString(), ["revision"] = row.Revision.ToString() }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return MemoryOpOutcome.Ok;
    }

    /// <summary>Share (explicit cross-team grant, §14.2): private entries are unsharable by construction; the grantee must exist in the roster. Re-grant revives a revoked row (no duplicates).</summary>
    public async Task<MemoryOpOutcome> ShareAsync(MemoryAccessContext caller, Guid entryId, string granteeTeam, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.MemoryEntries.AsNoTracking().FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
        if (row is null || row.Status == "tombstoned" || !OwnerTeam(row, caller))
        {
            return await RefuseAsync(db, tx, caller, entryId, "memory.denied", "share-unreadable", ct);
        }

        if (row.PolicyLabel == "private")
        {
            audit.Append(db, Deny(caller, "memory.denied", $"memory/{entryId}", "share-of-private"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.MemoryDenied("share-of-private");
            return MemoryOpOutcome.ShareDenied;
        }

        if (!await db.Teams.AnyAsync(t => t.Slug == granteeTeam, ct))
        {
            audit.Append(db, Deny(caller, "memory.denied", $"memory/{entryId}", $"grantee-unknown:{granteeTeam}"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.MemoryDenied("grantee-unknown");
            return MemoryOpOutcome.ShareDenied;
        }

        var share = await db.MemoryShares.FirstOrDefaultAsync(
            s => s.EntryId == entryId && s.GranteeTeamSlug == granteeTeam, ct);
        if (share is null)
        {
            db.MemoryShares.Add(new MemoryShareEntity
            {
                ShareId = Guid.NewGuid(),
                EntryId = entryId,
                GranteeTeamSlug = granteeTeam,
                GrantedBy = caller.Actor,
                CreatedAt = now,
            });
        }
        else
        {
            share.RevokedAt = null;
            share.GrantedBy = caller.Actor;
        }

        audit.Append(db, Allow(caller, "memory.shared", $"memory/{entryId}", "shared",
            new Dictionary<string, string?> { ["grantee"] = granteeTeam }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return MemoryOpOutcome.Ok;
    }

    /// <summary>Revoke one share (owner-team curation).</summary>
    public async Task<MemoryOpOutcome> RevokeShareAsync(MemoryAccessContext caller, Guid entryId, Guid shareId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.MemoryEntries.AsNoTracking().FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
        if (row is null || !OwnerTeam(row, caller))
        {
            return await RefuseAsync(db, tx, caller, entryId, "memory.denied", "share-revoke-unreadable", ct);
        }

        var stamped = await db.MemoryShares
            .Where(s => s.ShareId == shareId && s.EntryId == entryId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now), ct);
        if (stamped == 0)
        {
            return MemoryOpOutcome.ShareNotFound;
        }

        audit.Append(db, Allow(caller, "memory.share.revoked", $"memory/{entryId}", "revoked",
            new Dictionary<string, string?> { ["shareId"] = shareId.ToString() }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return MemoryOpOutcome.Ok;
    }

    /// <summary>Delete (D5): tombstone + content drop + share revocation in one tx; usage rows and the hash survive for audit.</summary>
    public async Task<MemoryOpOutcome> DeleteAsync(MemoryAccessContext caller, Guid entryId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.MemoryEntries.AsNoTracking().FirstOrDefaultAsync(e => e.EntryId == entryId, ct);
        if (row is null || !OwnerTeam(row, caller))
        {
            return await RefuseAsync(db, tx, caller, entryId, "memory.denied", "delete-unreadable", ct);
        }

        var done = await provider.DeleteAsync(db, new AuthorizedMemoryDeletion(entryId, now), ct);
        if (!done)
        {
            return MemoryOpOutcome.NotFound;
        }

        audit.Append(db, Allow(caller, "memory.deleted", $"memory/{entryId}", "deleted",
            new Dictionary<string, string?> { ["contentHash"] = row.ContentHash }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return MemoryOpOutcome.Ok;
    }

    /// <summary>Export (§14.3 core for portable text): NDJSON of the caller-readable, live entries of one resolved scope (appendix §6.1 field names).</summary>
    public async Task<string?> ExportAsync(MemoryAccessContext caller, string scopeSelector, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var scope = await ResolveReadableScopeAsync(db, caller, scopeSelector, ct);
        if (scope is null)
        {
            return null;
        }

        var rows = await db.MemoryEntries.AsNoTracking()
            .Where(e => e.ScopeId == scope.ScopeId && e.Status != "tombstoned")
            .OrderBy(e => e.CreatedAt).ToListAsync(ct);
        var visible = rows.Where(e => MayReadRow(e, caller)).ToList();
        var sb = new StringBuilder();
        foreach (var e in visible)
        {
            sb.AppendLine(JsonSerializer.Serialize(new
            {
                memoryId = e.EntryId.ToString(),
                scope = scope.CanonicalSelector,
                accessControl = new
                {
                    visibility = e.PolicyLabel,
                    allowedPrincipals = new List<string> { $"team:{e.TeamSlug}" },
                },
                provenance = JsonDocument.Parse(e.ProvenanceJson).RootElement.Clone(),
                contentSummary = e.ContentSummary,
                content = e.PolicyLabel == "evidence-only" ? string.Empty : e.Content,
                contentHash = e.ContentHash,
                status = e.Status,
                revision = e.Revision,
            }, Json));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The external attestation path (appendix §6.2): an <c>event.memory.used</c>
    /// records the same usage row an in-kernel recall writes. The event plane
    /// never 500s on memory (§14.6) — an unknown entry or unreadable team
    /// audits a deny and swallows (the arm answers accepted either way).
    /// </summary>
    public async Task RecordUsageAsync(
        MemoryAccessContext caller, string memoryId, string consumer, string justification, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var entry = Guid.TryParse(memoryId, out var id)
            ? await db.MemoryEntries.AsNoTracking().FirstOrDefaultAsync(e => e.EntryId == id, ct)
            : null;
        if (entry is null || !await MayReadAsync(db, entry, caller, ct))
        {
            audit.Append(db, Deny(caller, "memory.used", $"memory/{memoryId}", "attest-unreadable"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            metrics.MemoryDenied("attest-unreadable");
            return;
        }

        db.MemoryUsage.Add(new MemoryUsageEntity
        {
            UsageId = Guid.NewGuid(),
            EntryId = entry.EntryId,
            Consumer = consumer,
            Purpose = "external-attestation",
            Justification = justification,
            WorkId = WorkIdOf(entry),
            CreatedAt = now,
        });
        audit.Append(db, Allow(caller, "memory.used", $"memory/{entry.EntryId}", "attested",
            new Dictionary<string, string?> { ["consumer"] = consumer }));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.MemoryUsed();
    }

    /// <summary>Timeline note for a work-linked candidate landing (event/command arms): own short tx, never blocks routing.</summary>
    public async Task NoteWorkCandidateAsync(MemoryAccessContext caller, Guid workId, Guid entryId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await AppendTimelineAsync(db, workId, "memory.candidate", caller.Actor, entryId, now, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Timeline note for a work-linked candidate landing (the event/command arms call this inside their own tx).</summary>
    public static async Task AppendTimelineAsync(
        FacilitatorDbContext db, Guid workId, string kind, string actor, Guid entryId, DateTime now, CancellationToken ct)
    {
        var version = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefaultAsync(ct);
        if (version is null)
        {
            return;
        }

        db.WorkEvents.Add(new WorkEventEntity
        {
            EventId = Guid.NewGuid(),
            WorkId = workId,
            WorkVersion = version.Value,
            Kind = kind,
            Actor = actor,
            Detail = JsonSerializer.Serialize(new Dictionary<string, string?> { ["entryId"] = entryId.ToString() }, Json),
            CorrelationId = entryId.ToString(),
            CreatedAt = now,
        });
    }

    // ---- resolution + guards -------------------------------------------------

    private async Task<(MemoryScopeEntity? Scope, string Denial)> ResolveWriteScopeAsync(
        FacilitatorDbContext db, MemoryAccessContext caller, string selector, DateTime now, CancellationToken ct)
    {
        if (caller.TeamSlug is null)
        {
            return (null, "caller-teamless");
        }

        var parsed = ParseSelector(selector);
        if (parsed is null)
        {
            return (null, "selector-malformed");
        }

        var (kind, value) = parsed.Value;
        Guid? projectId = null;
        Guid? workId = null;
        string ownerTeam;
        string canonical;
        switch (kind)
        {
            case "project":
                if (!Guid.TryParse(value, out var project))
                {
                    return (null, "selector-unresolvable");
                }

                var owned = await JoinProjectOwnerAsync(db, project, ct);
                if (owned is null)
                {
                    return (null, "selector-unresolvable");
                }

                (projectId, ownerTeam) = owned.Value;
                canonical = $"project:{project:D}";
                break;
            case "work":
                if (!Guid.TryParse(value, out var work))
                {
                    return (null, "selector-unresolvable");
                }

                var link = await db.Works.AsNoTracking()
                    .Where(w => w.WorkId == work)
                    .Select(w => new { w.ProjectId, ProjectTeam = w.Project!.Team!.Slug })
                    .FirstOrDefaultAsync(ct);
                if (link is null)
                {
                    return (null, "selector-unresolvable");
                }

                workId = work;
                projectId = link.ProjectId;
                // A work with no project lands in the caller's team scope lineage.
                ownerTeam = link.ProjectTeam ?? caller.TeamSlug;
                canonical = $"work:{work:D}";
                break;
            case "team":
                // team: targets exactly the caller's team — a foreign team name is
                // a read hint elsewhere, a write denial here.
                if (!string.Equals(value, caller.TeamSlug, StringComparison.Ordinal))
                {
                    return (null, "selector-foreign-team");
                }

                ownerTeam = caller.TeamSlug;
                canonical = $"team:{value}";
                break;
            default: // private: the caller themselves
                if (!(string.Equals(selector, $"private:{caller.Actor}", StringComparison.Ordinal)
                      || caller.Actor.EndsWith($"|{value}", StringComparison.Ordinal)))
                {
                    return (null, "selector-not-self");
                }

                ownerTeam = caller.TeamSlug;
                canonical = $"private:{caller.Actor}";
                break;
        }

        var scope = await db.MemoryScopes.FirstOrDefaultAsync(
            s => s.CanonicalSelector == canonical, ct);
        if (scope is null)
        {
            scope = new MemoryScopeEntity
            {
                ScopeId = Guid.NewGuid(),
                Kind = kind,
                TeamSlug = ownerTeam,
                ProjectId = projectId,
                WorkId = workId,
                CanonicalSelector = canonical,
                RetentionDays = await ProjectRetentionDaysAsync(db, projectId, kind, ct),
                CreatedAt = now,
            };
            db.MemoryScopes.Add(scope);
        }

        if (!string.Equals(scope.TeamSlug, caller.TeamSlug, StringComparison.Ordinal))
        {
            return (null, "scope-foreign-team");
        }

        return (scope, string.Empty);
    }

    /// <summary>Row-local readable law: tombstoned never reads; own team always;
    /// <c>facilitator-visible</c> reads across teams — private can never be public
    /// by construction (D2). A live share is checked separately (async paths).</summary>
    private static bool MayReadRow(MemoryEntryEntity row, MemoryAccessContext caller) =>
        row.Status != "tombstoned"
        && (string.Equals(row.TeamSlug, caller.TeamSlug, StringComparison.Ordinal)
            || row.PolicyLabel == "facilitator-visible");

    /// <summary>The full readable law for materialized rows: row-local law OR a live share to the caller's team.</summary>
    private async Task<bool> MayReadAsync(FacilitatorDbContext db, MemoryEntryEntity row, MemoryAccessContext caller, CancellationToken ct) =>
        MayReadRow(row, caller)
        || (caller.TeamSlug is not null && await db.MemoryShares.AsNoTracking().AnyAsync(
            s => s.EntryId == row.EntryId && s.GranteeTeamSlug == caller.TeamSlug && s.RevokedAt == null, ct));

    private async Task<MemoryScopeEntity?> ResolveReadableScopeAsync(
        FacilitatorDbContext db, MemoryAccessContext caller, string hint, CancellationToken ct)
    {
        var parsed = ParseSelector(hint);
        if (parsed is null)
        {
            return null;
        }

        var (kind, value) = parsed.Value;
        var canonical = kind switch
        {
            "project" when Guid.TryParse(value, out var p) => $"project:{p:D}",
            "work" when Guid.TryParse(value, out var w) => $"work:{w:D}",
            "team" => $"team:{value}",
            "private" => $"private:{caller.Actor}",
            _ => null,
        };
        if (canonical is null)
        {
            return null;
        }

        var scope = await db.MemoryScopes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CanonicalSelector == canonical, ct);
        return scope is not null && string.Equals(scope.TeamSlug, caller.TeamSlug, StringComparison.Ordinal)
            ? scope
            : null;
    }

    private static (string Kind, string Value)? ParseSelector(string selector)
    {
        var colon = selector.IndexOf(':');
        if (colon < 1 || colon == selector.Length - 1)
        {
            return null;
        }

        var kind = selector[..colon];
        return kind is "project" or "work" or "team" or "private" ? (kind, selector[(colon + 1)..]) : null;
    }

    private static async Task<(Guid ProjectId, string TeamSlug)?> JoinProjectOwnerAsync(
        FacilitatorDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Projects.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .Select(p => new { p.ProjectId, Team = p.Team!.Slug })
            .FirstOrDefaultAsync(ct) is { } row ? (row.ProjectId, row.Team) : null;

    /// <summary>D7: per-Project policy rides <c>projects.config</c> jsonb key <c>memory</c> (Plan 14's carrier law); work scopes read <c>workCandidateDays</c>, project scopes <c>candidateDays</c>.</summary>
    private async Task<int?> ProjectRetentionDaysAsync(FacilitatorDbContext db, Guid? projectId, string kind, CancellationToken ct)
    {
        if (projectId is not { } id)
        {
            return null;
        }

        var config = await db.Projects.AsNoTracking()
            .Where(p => p.ProjectId == id).Select(p => p.Config).FirstOrDefaultAsync(ct);
        if (config is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(config);
            if (doc.RootElement.TryGetProperty("memory", out var memory)
                && memory.ValueKind == JsonValueKind.Object
                && memory.TryGetProperty(kind == "work" ? "workCandidateDays" : "candidateDays", out var days)
                && days.TryGetInt32(out var parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // A malformed config is no policy: fall through to the default.
        }

        return null;
    }

    private async Task<DateTime?> CandidateExpiryAsync(
        FacilitatorDbContext db, MemoryScopeEntity scope, string status, DateTime now, CancellationToken ct)
    {
        if (status != "candidate")
        {
            // Reviewed knowledge (REST-human active) expires per Project lifetime, not by the candidate clock (§14.6).
            return null;
        }

        var days = scope.RetentionDays
            ?? await ProjectRetentionDaysAsync(db, scope.ProjectId, scope.Kind, ct)
            ?? options.Retention.CandidateDays;
        return days <= 0 ? now : now.AddDays(days);
    }

    /// <summary>The jsonb provenance document: the frozen candidate sub-schema fields plus the optional linkage (the broker stores what the wire carried).</summary>
    private static string ProvenanceOf(MemoryProvenanceInput p) =>
        JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["source"] = p.Source,
            ["evidenceRef"] = p.EvidenceRef,
            ["createdBy"] = p.CreatedBy,
            ["createdAt"] = p.CreatedAt.ToString("O"),
            ["workId"] = p.WorkId,
            ["executionId"] = p.ExecutionId,
            ["sourceDigest"] = p.SourceDigest,
        }, Json);
    private static bool OwnerTeam(MemoryEntryEntity row, MemoryAccessContext caller) =>
        caller.TeamSlug is not null && string.Equals(row.TeamSlug, caller.TeamSlug, StringComparison.Ordinal);

    private static bool IsOwnTeamSelector(string selector, string teamSlug) =>
        string.Equals(selector, $"team:{teamSlug}", StringComparison.Ordinal);

    private static Guid? WorkIdOf(MemoryEntryEntity row)
    {
        try
        {
            using var doc = JsonDocument.Parse(row.ProvenanceJson);
            return doc.RootElement.TryGetProperty("workId", out var w)
                   && Guid.TryParse(w.GetString(), out var id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- shaping -------------------------------------------------------------

    private async Task<List<MemoryRecallEntry>> LoadRecallEntriesAsync(
        FacilitatorDbContext db, List<MemorySearchHit> hits, DateTime now, CancellationToken ct)
    {
        _ = now;
        var ids = hits.Select(h => h.EntryId).ToList();
        var rows = await db.MemoryEntries.AsNoTracking().Where(e => ids.Contains(e.EntryId)).ToListAsync(ct);
        var scopeIds = rows.Select(r => r.ScopeId).Distinct().ToList();
        var selectors = await db.MemoryScopes.AsNoTracking()
            .Where(s => scopeIds.Contains(s.ScopeId))
            .ToDictionaryAsync(s => s.ScopeId, s => s.CanonicalSelector, ct);
        var order = hits.ToDictionary(h => h.EntryId, h => h.Rank);
        return rows
            .OrderByDescending(r => order[r.EntryId])
            .ThenByDescending(r => r.ObservedAt)
            .Select(r => new MemoryRecallEntry(
                r.EntryId,
                selectors.GetValueOrDefault(r.ScopeId, string.Empty),
                r.Type,
                // D2: evidence-only rows surface metadata + summary, full text lives behind the evidence ref.
                r.PolicyLabel == "evidence-only" ? r.ContentSummary : r.Content,
                r.ContentHash,
                JsonDocument.Parse(r.ProvenanceJson).RootElement.Clone(),
                r.ObservedAt,
                r.EffectiveFrom,
                r.Revision,
                r.Status,
                r.PolicyLabel,
                r.TeamSlug,
                $"fts-rank {order[r.EntryId].ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"))
            .ToList();
    }

    private static MemoryEntryView View(
        MemoryEntryEntity row, string selector, List<MemoryShareEntity> shares, long usage) =>
        new(
            row.EntryId, selector, row.Type, row.Content, row.MediaType, row.ContentSummary,
            row.ContentHash, JsonDocument.Parse(row.ProvenanceJson).RootElement.Clone(),
            row.ObservedAt, row.EffectiveFrom, row.Classification, row.PolicyLabel, row.Status,
            row.Revision, row.SupersedesEntryId, row.ExpiresAt, row.CreatedAt, row.CreatedBy,
            shares.Select(s => (s.ShareId, s.GranteeTeamSlug, s.CreatedAt)).ToList(), usage);

    // ---- audit + small helpers -------------------------------------------------

    private static AuditRecord Allow(
        MemoryAccessContext caller, string action, string resource, string reason, Dictionary<string, string?> detail) =>
        new(caller.Actor, action, resource, "allow", reason, null, Policy,
            JsonSerializer.Serialize(detail, Json));

    private static AuditRecord Deny(MemoryAccessContext caller, string action, string resource, string reason) =>
        new(caller.Actor, action, resource, "deny", reason, null, Policy,
            JsonSerializer.Serialize(new Dictionary<string, string?> { ["reason"] = reason }, Json));

    private async Task<MemoryOpOutcome> RefuseAsync(
        FacilitatorDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        MemoryAccessContext caller, Guid entryId, string action, string reason, CancellationToken ct)
    {
        audit.Append(db, Deny(caller, action, $"memory/{entryId}", reason));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        metrics.MemoryDenied(reason);
        return MemoryOpOutcome.NotFound;
    }

    private async Task<string> SelectorOfAsync(FacilitatorDbContext db, Guid scopeId, CancellationToken ct) =>
        await db.MemoryScopes.AsNoTracking().Where(s => s.ScopeId == scopeId)
            .Select(s => s.CanonicalSelector).FirstOrDefaultAsync(ct) ?? string.Empty;

    private static string Origin(MemoryAccessContext caller) => caller.IsWorkload ? "link" : "rest";

    internal static string Sha256Of(string content) =>
        "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string Summarize(string content) =>
        Truncate(content, 512);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static double ElapsedMs(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>The wire type set, exposed so the REST surface validates bodies against the same list the CHECK enforces.</summary>
    public static IReadOnlyList<string> WireTypes => Types;

    /// <summary>The policy-label set, same law as the CHECK.</summary>
    public static IReadOnlyList<string> WirePolicyLabels => PolicyLabels;
}
