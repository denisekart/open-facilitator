using System.Text.Json;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Shared.Identity;

/// <summary>One append-ready audit row (already normalized — no raw tokens).</summary>
/// <param name="Actor">Canonical actor (<c>oidc:iss|sub</c>) or null for unauthenticated context.</param>
/// <param name="Action">Decision subject, e.g. <c>authorization.evaluated:work.write</c>.</param>
/// <param name="Resource">Route/path covered.</param>
/// <param name="Decision">allow | deny.</param>
/// <param name="Reason">Machine reason code (no-role-match, no-binding, ...).</param>
/// <param name="EvidenceRef">Optional evidence pointer.</param>
/// <param name="PolicyVersion">Role-rule policy version at decision time.</param>
/// <param name="Detail">jsonb-safe context document.</param>
public sealed record AuditRecord(
    string? Actor,
    string Action,
    string? Resource,
    string Decision,
    string? Reason,
    string? EvidenceRef,
    string? PolicyVersion,
    string Detail);

/// <summary>
/// The single audit append helper (appendix §3.2 step 5) — every plan after
/// 09 writes its audit rows through here. Append shape mirrors
/// <see cref="IngressStore"/>: <see cref="IDbContextFactory{TContext}"/> +
/// <see cref="TimeProvider"/>, client-side Guid. Exceptions propagate — an
/// audit failure on an allow path is a 503, never a silent drop.
/// </summary>
public sealed class AuditWriter(IDbContextFactory<FacilitatorDbContext> contextFactory, TimeProvider timeProvider)
{
    /// <summary>Append one decision row. Never swallows persistence errors.</summary>
    public async Task WriteAsync(AuditRecord record, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.Audit.Add(Build(record));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Stage one decision row on a caller-owned context so the audit row
    /// commits atomically with the state change it records (Plan 11 ledger).
    /// </summary>
    public void Append(FacilitatorDbContext db, AuditRecord record) => db.Audit.Add(Build(record));

    private AuditEventEntity Build(AuditRecord record) => new()
    {
        AuditId = Guid.NewGuid(),
        OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
        Actor = record.Actor,
        Action = record.Action,
        Resource = record.Resource,
        Decision = record.Decision,
        Reason = record.Reason,
        EvidenceRef = record.EvidenceRef,
        PolicyVersion = record.PolicyVersion,
        Detail = string.IsNullOrWhiteSpace(record.Detail) ? "{}" : record.Detail,
        TraceId = System.Diagnostics.Activity.Current?.TraceId.ToHexString(),
    };
}
