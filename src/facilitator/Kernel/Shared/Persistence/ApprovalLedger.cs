using System.Globalization;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Persistence;

/// <summary>Outcome of recording an <c>approval.required</c> envelope.</summary>
public enum ApprovalRecordOutcome
{
    /// <summary>Row inserted (digest recomputed or recorded digest-as-declared).</summary>
    Recorded,

    /// <summary>Row already exists for this approvalId (redelivery is idempotent).</summary>
    Duplicate,

    /// <summary>Kernel-recomputed digest != declared <c>actionDigest</c> — event-class rejection, no row.</summary>
    DigestMismatch,

    /// <summary><c>approvalId</c> is not a GUID — the ledger PK cannot hold it.</summary>
    BadId,
}

/// <summary>Result of <see cref="IApprovalLedger.RecordFromEventAsync"/>.</summary>
public sealed record ApprovalRecordResult(ApprovalRecordOutcome Outcome, ApprovalView? Row = null);

/// <summary>Read projection of one ledger row.</summary>
public sealed record ApprovalView(
    Guid ApprovalId,
    Guid? WorkId,
    string ActionDigest,
    string State,
    string DigestVerification,
    IReadOnlyList<string> EligibleActors,
    DateTime ExpiresAt,
    DateTime CreatedAt,
    DateTime? ConsumedAt,
    string? DecidedBy,
    string? ActorEvidenceRef,
    string? Comment,
    DateTime? DecidedAt);

/// <summary>Outcome of respond/consume/revoke against the ledger.</summary>
public enum ApprovalDecisionOutcome
{
    /// <summary>State advanced (respond decided / consume flipped / revoke applied).</summary>
    Decided,

    /// <summary>Revoke onto a terminal row — intent already satisfied, no state change (200 no-op).</summary>
    NoOp,

    /// <summary>No ledger row for this approvalId.</summary>
    NotFound,

    /// <summary>Still <c>pending</c> (consume/revoke on a row nothing decided yet).</summary>
    NotPending,

    /// <summary>Row is <c>expired</c> (lazy flip included) — respond denies.</summary>
    Expired,

    /// <summary>Row is <c>revoked</c> — revocation-before-apply (appendix §7.3) denies.</summary>
    Revoked,

    /// <summary>Row already decided/consumed — atomic single use (spec §12.2) denies a second respond.</summary>
    AlreadyConsumed,

    /// <summary>Effective actor does not intersect <c>eligibleActors</c> (law D4).</summary>
    Ineligible,
}

/// <summary>Result of a respond/consume/revoke attempt.</summary>
/// <param name="Outcome">Machine outcome (feeds the typed ProblemDetails).</param>
/// <param name="Row">The row's state after the attempt (null when not found).</param>
/// <param name="EvidenceRef">Minted <c>kernel:&lt;id&gt;</c> receipt (consumed approve responds, law D5).</param>
public sealed record ApprovalDecision(ApprovalDecisionOutcome Outcome, ApprovalView? Row = null, string? EvidenceRef = null);

/// <summary>
/// The approvals ledger (Plan 11, spec §12.1–12.2 + appendix §7): the only
/// writer of <c>facilitator.approvals</c>. State transitions are conditional
/// UPDATEs — the database is the single-use arbiter (law D3, no advisory
/// locks); expiry materializes lazily on read/respond (no janitor); every
/// flip is audited through <see cref="Identity.AuditWriter"/> and human
/// decisions append an <c>approval.decided</c> Work timeline note. A consumed
/// approve respond mints the already-consumed <c>kernel:</c> evidence receipt
/// (law D5). Eligibility grammar (law D4): <c>group:</c> (case-sensitive,
/// raw or <c>/teams/</c>-stripped) first, then <c>user:</c> (display name),
/// then <c>oidc:</c> (canonical actor verbatim).
/// </summary>
public interface IApprovalLedger
{
    /// <summary>Record an <c>approval.required</c> envelope (recompute + compare the digest when <c>data.action</c> is present).</summary>
    Task<ApprovalRecordResult> RecordFromEventAsync(JsonElement envelopeRoot, string source, CancellationToken ct);

    /// <summary>Atomic respond: lazy-expire, terminal-state deny, eligibility, one conditional UPDATE; race losers observe the winner's state.</summary>
    Task<ApprovalDecision> RespondAsync(
        string approvalIdRaw,
        string decision,
        string? comment,
        string actorActor,
        IReadOnlyList<string> actorGroups,
        string? actorDisplayName,
        string? evidenceRef,
        bool mintEvidence,
        CancellationToken ct);

    /// <summary>Plan 12's hook: atomically consume an approved row (re-check-before-apply).</summary>
    Task<ApprovalDecision> ConsumeAsync(string approvalIdRaw, string workloadActor, CancellationToken ct);

    /// <summary>Revoke a pending approval; a terminal row is an audited no-op.</summary>
    Task<ApprovalDecision> RevokeAsync(string approvalIdRaw, string revokedBy, string reason, CancellationToken ct);

    /// <summary>One row (lazy-expiring it first); null when never recorded.</summary>
    Task<ApprovalView?> GetAsync(Guid approvalId, CancellationToken ct);

    /// <summary>Rows for a work (lazy-expiring them first), newest first.</summary>
    Task<IReadOnlyList<ApprovalView>> ListForWorkAsync(Guid workId, CancellationToken ct);
}

/// <summary>Ledger-backed <see cref="IApprovalLedger"/> (one short transaction per operation).</summary>
public sealed class ApprovalLedger(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    Identity.AuditWriter audit,
    TimeProvider timeProvider) : IApprovalLedger
{
    /// <inheritdoc />
    public async Task<ApprovalRecordResult> RecordFromEventAsync(JsonElement envelopeRoot, string source, CancellationToken ct)
    {
        var data = envelopeRoot.GetProperty("data");
        var approvalIdRaw = data.TryGetProperty("approvalId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty
            : string.Empty;
        if (!Guid.TryParse(approvalIdRaw, out var approvalId))
        {
            return new ApprovalRecordResult(ApprovalRecordOutcome.BadId);
        }

        var declared = data.GetProperty("actionDigest").GetString() ?? string.Empty;
        var actors = data.GetProperty("eligibleActors").EnumerateArray().Select(x => x.GetString()!).ToArray();
        var expiresAt = ParseTimestamp(data.GetProperty("expiresAt").GetString()!);
        var createdAt = ParseTimestamp(data.GetProperty("createdAt").GetString()!);
        var hasAction = data.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.Object;
        var verification = "recomputed";
        if (hasAction)
        {
            var computed = ApprovalDigest.Compute(approvalIdRaw, action, actors, expiresAt, createdAt);
            if (!string.Equals(computed, declared, StringComparison.Ordinal))
            {
                return new ApprovalRecordResult(ApprovalRecordOutcome.DigestMismatch);
            }
        }
        else
        {
            // 1.0 senders carry no action payload: digest-as-declared, recorded
            // non-authoritatively (law D2) — never silent verification.
            verification = "digest-unverified";
        }

        Guid? workId = data.TryGetProperty("workId", out var wid) && wid.ValueKind == JsonValueKind.String
            && Guid.TryParse(wid.GetString(), out var parsedWork)
            ? parsedWork
            : null;

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Approvals.Add(new ApprovalEntity
        {
            ApprovalId = approvalId,
            WorkId = workId,
            ActionDigest = declared,
            State = "pending",
            Nonce = data.GetProperty("nonce").GetString() ?? string.Empty,
            EligibleActors = JsonSerializer.Serialize(actors),
            ExpiresAt = expiresAt,
            CreatedAt = createdAt,
            DigestVerification = verification,
        });
        if (verification == "digest-unverified")
        {
            audit.Append(db, new Identity.AuditRecord(
                Actor: null,
                Action: "authorization.evaluated:approval",
                Resource: $"approval/{approvalId}",
                Decision: "allow",
                Reason: "digest-unverified",
                EvidenceRef: null,
                PolicyVersion: "approval-v1",
                Detail: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["approvalId"] = approvalIdRaw,
                    ["source"] = source,
                    ["declaredDigest"] = declared,
                })));
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // PK collision: the event legitimately redelivered under a new
            // envelope id — the existing ledger row is the idempotent answer.
            await tx.RollbackAsync(ct);
            var existing = await FindViewAsync(db, approvalId, ct);
            return new ApprovalRecordResult(
                existing is null ? ApprovalRecordOutcome.BadId : ApprovalRecordOutcome.Duplicate, existing);
        }

        await tx.CommitAsync(ct);
        return new ApprovalRecordResult(
            ApprovalRecordOutcome.Recorded,
            new ApprovalView(approvalId, workId, declared, "pending", verification, actors, expiresAt, createdAt, null, null, null, null, null));
    }

    /// <inheritdoc />
    public async Task<ApprovalDecision> RespondAsync(
        string approvalIdRaw,
        string decision,
        string? comment,
        string actorActor,
        IReadOnlyList<string> actorGroups,
        string? actorDisplayName,
        string? evidenceRef,
        bool mintEvidence,
        CancellationToken ct)
    {
        if (!Guid.TryParse(approvalIdRaw, out var approvalId))
        {
            return new ApprovalDecision(ApprovalDecisionOutcome.NotFound);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await LazyExpireAsync(db, approvalId: approvalId, ct);

        var row = await db.Approvals.Where(a => a.ApprovalId == approvalId).FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return new ApprovalDecision(ApprovalDecisionOutcome.NotFound);
        }

        var denied = Terminal(row.State);
        if (denied is not null)
        {
            audit.Append(db, DenyAudit(row, actorActor, denied.Outcome));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return denied with { Row = Map(row) };
        }

        if (!row.EligibleActorsList().IsEligible(actorActor, actorGroups, actorDisplayName))
        {
            audit.Append(db, DenyAudit(row, actorActor, ApprovalDecisionOutcome.Ineligible));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ApprovalDecision(ApprovalDecisionOutcome.Ineligible, Map(row));
        }

        var rows = await db.Approvals
            .Where(a => a.ApprovalId == approvalId && a.State == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.State, decision)
                .SetProperty(a => a.ConsumedAt, decision == "approved" ? now : (DateTime?)null)
                .SetProperty(a => a.DecidedBy, actorActor)
                .SetProperty(a => a.ActorEvidenceRef, evidenceRef)
                .SetProperty(a => a.Comment, comment)
                .SetProperty(a => a.DecidedAt, now), ct);
        if (rows == 0)
        {
            // Race loser: re-read and report the winner's terminal state.
            var raced = await FindViewAsync(db, approvalId, ct);
            await tx.RollbackAsync(ct);
            return new ApprovalDecision(Terminal(raced?.State ?? "pending")?.Outcome ?? ApprovalDecisionOutcome.NotFound, raced);
        }

        var evidenceId = Guid.NewGuid().ToString("N");
        var minted = mintEvidence && decision == "approved";
        if (minted)
        {
            db.Evidence.Add(new EvidenceEntity
            {
                EvidenceId = evidenceId,
                Subject = actorActor,
                Claims = JsonSerializer.Serialize(new Dictionary<string, object?> { ["groups"] = actorGroups }),
                IssuedAt = now,
                ExpiresAt = row.ExpiresAt,
                ConsumedAt = now,
            });
        }

        audit.Append(db, new Identity.AuditRecord(
            Actor: actorActor,
            Action: "authorization.evaluated:approval",
            Resource: $"approval/{approvalId}",
            Decision: "allow",
            Reason: decision,
            EvidenceRef: minted ? $"kernel:{evidenceId}" : evidenceRef,
            PolicyVersion: "approval-v1",
            Detail: Detail(row, decision, actorActor)));

        if (row.WorkId is { } workId)
        {
            var version = await db.Works.AsNoTracking().Where(w => w.WorkId == workId).Select(w => (long?)w.Version).FirstOrDefaultAsync(ct);
            if (version is not null)
            {
                db.WorkEvents.Add(new WorkEventEntity
                {
                    EventId = Guid.NewGuid(),
                    WorkId = workId,
                    WorkVersion = version.Value,
                    Kind = "approval.decided",
                    Actor = actorActor,
                    Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
                    {
                        ["approvalId"] = approvalIdRaw,
                        ["decision"] = decision,
                        ["actor"] = actorActor,
                        ["evidenceRef"] = minted ? $"kernel:{evidenceId}" : null,
                    }),
                    CorrelationId = approvalIdRaw,
                    CreatedAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        var after = await FindViewAsync(db, approvalId, ct);
        return new ApprovalDecision(ApprovalDecisionOutcome.Decided, after, minted ? $"kernel:{evidenceId}" : null);
    }

    /// <inheritdoc />
    public async Task<ApprovalDecision> ConsumeAsync(string approvalIdRaw, string workloadActor, CancellationToken ct)
    {
        if (!Guid.TryParse(approvalIdRaw, out var approvalId))
        {
            return new ApprovalDecision(ApprovalDecisionOutcome.NotFound);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var rows = await db.Approvals
            .Where(a => a.ApprovalId == approvalId && a.State == "approved")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "consumed"), ct);
        var row = await FindViewAsync(db, approvalId, ct);
        if (row is null)
        {
            return new ApprovalDecision(ApprovalDecisionOutcome.NotFound);
        }

        if (rows == 1)
        {
            audit.Append(db, new Identity.AuditRecord(
                Actor: workloadActor,
                Action: "authorization.evaluated:approval",
                Resource: $"approval/{approvalId}",
                Decision: "allow",
                Reason: "consumed",
                EvidenceRef: null,
                PolicyVersion: "approval-v1",
                Detail: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["approvalId"] = approvalIdRaw,
                    ["actionDigest"] = row.ActionDigest,
                    ["consumedBy"] = workloadActor,
                })));
            await db.SaveChangesAsync(ct);
            return new ApprovalDecision(ApprovalDecisionOutcome.Decided, await FindViewAsync(db, approvalId, ct));
        }

        return new ApprovalDecision(row.State switch
        {
            "revoked" => ApprovalDecisionOutcome.Revoked,
            "expired" => ApprovalDecisionOutcome.Expired,
            "pending" => ApprovalDecisionOutcome.NotPending,
            _ => ApprovalDecisionOutcome.AlreadyConsumed,
        }, row);
    }

    /// <inheritdoc />
    public async Task<ApprovalDecision> RevokeAsync(string approvalIdRaw, string revokedBy, string reason, CancellationToken ct)
    {
        if (!Guid.TryParse(approvalIdRaw, out var approvalId))
        {
            return new ApprovalDecision(ApprovalDecisionOutcome.NotFound);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var rows = await db.Approvals
            .Where(a => a.ApprovalId == approvalId && a.State == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "revoked"), ct);
        var row = await FindViewAsync(db, approvalId, ct);
        if (row is null)
        {
            return new ApprovalDecision(ApprovalDecisionOutcome.NotFound);
        }

        var noOp = rows == 0;
        audit.Append(db, new Identity.AuditRecord(
            Actor: revokedBy,
            Action: "authorization.evaluated:approval",
            Resource: $"approval/{approvalId}",
            Decision: "allow",
            Reason: noOp ? "no-op" : "revoked",
            EvidenceRef: null,
            PolicyVersion: "approval-v1",
            Detail: JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["approvalId"] = approvalIdRaw,
                ["revokedBy"] = revokedBy,
                ["reason"] = reason,
                ["state"] = row.State,
            })));
        await db.SaveChangesAsync(ct);
        return new ApprovalDecision(noOp ? ApprovalDecisionOutcome.NoOp : ApprovalDecisionOutcome.Decided, row);
    }

    /// <inheritdoc />
    public async Task<ApprovalView?> GetAsync(Guid approvalId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await LazyExpireAsync(db, approvalId: approvalId, ct);
        await db.SaveChangesAsync(ct);
        return await FindViewAsync(db, approvalId, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApprovalView>> ListForWorkAsync(Guid workId, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var flipped = await db.Approvals
            .Where(a => a.WorkId == workId && a.State == "pending" && a.ExpiresAt <= now)
            .Select(a => a.ApprovalId)
            .ToListAsync(ct);
        if (flipped.Count > 0)
        {
            await db.Approvals
                .Where(a => flipped.Contains(a.ApprovalId) && a.State == "pending")
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "expired"), ct);
            foreach (var id in flipped)
            {
                audit.Append(db, ExpiredAudit(id));
            }

            await db.SaveChangesAsync(ct);
        }

        var rows = await db.Approvals.AsNoTracking()
            .Where(a => a.WorkId == workId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        return rows.Select(Map).ToArray();
    }

    // Lazy expiry (law D3): conditional flip + one audit row per actual flip,
    // inside the caller's transaction so a flip never outlives its response.
    private async Task LazyExpireAsync(FacilitatorDbContext db, Guid approvalId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expired = await db.Approvals
            .Where(a => a.ApprovalId == approvalId && a.State == "pending" && a.ExpiresAt <= now)
            .Select(a => a.ApprovalId)
            .ToListAsync(ct);
        if (expired.Count == 0)
        {
            return;
        }

        await db.Approvals
            .Where(a => a.ApprovalId == approvalId && a.State == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "expired"), ct);
        audit.Append(db, ExpiredAudit(approvalId));
    }

    private Identity.AuditRecord ExpiredAudit(Guid approvalId) => new(
        Actor: null,
        Action: "authorization.evaluated:approval",
        Resource: $"approval/{approvalId}",
        Decision: "deny",
        Reason: "expired",
        EvidenceRef: null,
        PolicyVersion: "approval-v1",
        Detail: JsonSerializer.Serialize(new Dictionary<string, object?> { ["approvalId"] = approvalId.ToString() }));

    private static ApprovalDecision? Terminal(string state) => state switch
    {
        "expired" => new ApprovalDecision(ApprovalDecisionOutcome.Expired),
        "revoked" => new ApprovalDecision(ApprovalDecisionOutcome.Revoked),
        "approved" or "consumed" or "denied" => new ApprovalDecision(ApprovalDecisionOutcome.AlreadyConsumed),
        _ => null,
    };

    private Identity.AuditRecord DenyAudit(ApprovalEntity row, string actor, ApprovalDecisionOutcome outcome) => new(
        Actor: actor,
        Action: "authorization.evaluated:approval",
        Resource: $"approval/{row.ApprovalId}",
        Decision: "deny",
        Reason: outcome switch
        {
            ApprovalDecisionOutcome.Expired => "already-expired",
            ApprovalDecisionOutcome.Revoked => "already-revoked",
            ApprovalDecisionOutcome.Ineligible => "actor-ineligible",
            _ => "already-consumed",
        },
        EvidenceRef: row.ActionDigest,
        PolicyVersion: "approval-v1",
        Detail: Detail(row, "deny", actor));

    private string Detail(ApprovalEntity row, string decision, string actor) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["approvalId"] = row.ApprovalId.ToString(),
            ["actionDigest"] = row.ActionDigest,
            ["digestVerification"] = row.DigestVerification,
            ["decision"] = decision,
            ["actor"] = actor,
        });

    private static async Task<ApprovalView?> FindViewAsync(FacilitatorDbContext db, Guid approvalId, CancellationToken ct)
    {
        var row = await db.Approvals.AsNoTracking().FirstOrDefaultAsync(a => a.ApprovalId == approvalId, ct);
        return row is null ? null : Map(row);
    }

    private static ApprovalView Map(ApprovalEntity e) => new(
        e.ApprovalId, e.WorkId, e.ActionDigest, e.State, e.DigestVerification,
        e.EligibleActorsList(), e.ExpiresAt, e.CreatedAt, e.ConsumedAt,
        e.DecidedBy, e.ActorEvidenceRef, e.Comment, e.DecidedAt);

    private static DateTime ParseTimestamp(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}

/// <summary>Eligibility grammar (law D4): <c>group:</c> first, then <c>user:</c>, then <c>oidc:</c>.</summary>
internal static class EligibilityExtensions
{
    private const string TeamsPrefix = "/teams/";

    /// <summary>The jsonb <c>eligible_actors</c> document as strings.</summary>
    public static IReadOnlyList<string> EligibleActorsList(this ApprovalEntity row) =>
        JsonDocument.Parse(row.EligibleActors).RootElement.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToArray();

    /// <summary>Does the actor intersect the eligible set? Case-sensitive group match: raw path or its <c>/teams/</c>-stripped suffix.</summary>
    public static bool IsEligible(this IReadOnlyList<string> eligibleActors, string actorActor, IReadOnlyList<string> groups, string? displayName)
    {
        foreach (var entry in eligibleActors)
        {
            if (!entry.StartsWith("group:", StringComparison.Ordinal))
            {
                continue;
            }

            var path = entry["group:".Length..];
            var stripped = path.StartsWith(TeamsPrefix, StringComparison.Ordinal) ? path[TeamsPrefix.Length..] : path;
            if (groups.Any(g => string.Equals(g, path, StringComparison.Ordinal)
                || string.Equals(g, stripped, StringComparison.Ordinal)
                || (g.StartsWith(TeamsPrefix, StringComparison.Ordinal)
                    && string.Equals(g[TeamsPrefix.Length..], stripped, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        foreach (var entry in eligibleActors)
        {
            if (entry.StartsWith("user:", StringComparison.Ordinal))
            {
                var name = entry["user:".Length..];
                if (displayName is not null && string.Equals(displayName, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (entry.StartsWith("oidc:", StringComparison.Ordinal)
                && string.Equals(entry, actorActor, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
