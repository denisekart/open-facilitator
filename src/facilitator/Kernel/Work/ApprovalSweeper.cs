using System.Text.Json;
using System.Text.Json.Nodes;

using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The consent sweep: approvals age out LOUD. Lazy expiry only flips a row
/// when someone touches it, so an untouched approval silently rots and a slow
/// process could still act on a consent that has passed (N6). This sweep
/// flips due approvals to <c>expired</c> (and their held actions with them),
/// nudges once before the deadline, and parks the owning work
/// <c>blocked</c> with reason <c>consent-expired</c> — a stalled consent is a
/// human signal, never a silent wait. One pass takes an advisory lock so
/// replicas sweep once. Policy: <c>consent-policy</c>
/// <c>{nudgeBeforeMinutes}</c>; no row = no nudging (expiry still sweeps).
/// </summary>
public sealed class ApprovalSweeper(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<ApprovalSweeper> logger,
    ApprovalSweeper.ApprovalOptions options,
    Facilitator.Kernel.Notifications.NotificationLedger? notifications = null) : BackgroundService
{
    /// <summary>Admin-config resource carrying the consent knobs.</summary>
    public const string PolicyResource = "consent-policy";

    /// <summary>Host options: the sweep cadence.</summary>
    public sealed class ApprovalOptions
    {
        public const string SectionName = "Consent";

        public int SweepIntervalSeconds { get; set; } = 60;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.SweepIntervalSeconds)), stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                // The sweep is advisory: one bad pass must never kill the loop.
                logger.LogWarning(ex, "consent sweep pass failed");
            }
        }
    }

    /// <summary>Advisory lock key: distinct from every other sweeper.</summary>
    public const int SweepLockKey = 420006;

    /// <summary>One sweep: nudge due-but-alive approvals, expire due approvals
    /// (held actions too, work parked). Returns the rows expired.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await TrySweepLockAsync(db, ct))
            {
                await tx.RollbackAsync(ct);
                return 0;
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var nudged = await NudgeOnceAsync(db, now, ct);
            var expired = await ExpireOnceAsync(db, now, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            if (nudged > 0 || expired > 0)
            {
                logger.LogInformation("consent sweep: {Nudged} nudged, {Expired} expired", nudged, expired);
            }

            return expired;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<bool> TrySweepLockAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"SELECT pg_try_advisory_xact_lock({SweepLockKey})";
        return (bool)(await command.ExecuteScalarAsync(ct) ?? false);
    }

    private async Task<int> NudgeOnceAsync(FacilitatorDbContext db, DateTime now, CancellationToken ct)
    {
        var leadMinutes = await PolicyNudgeMinutesAsync(db, ct);
        if (leadMinutes is not { } lead)
        {
            return 0;
        }

        var due = await db.Approvals
            .Where(a => a.State == "pending" && a.NudgedAt == null
                && a.ExpiresAt > now && a.ExpiresAt <= now.AddMinutes(lead))
            .Select(a => a.ApprovalId)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        await db.Approvals
            .Where(a => due.Contains(a.ApprovalId))
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.NudgedAt, now), ct);
        await db.HeldActions
            .Where(h => due.Contains(h.ApprovalId) && h.State == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.NudgeAt, now), ct);

        // Attention, grouped per WORK (one nudge per conversation, not per
        // approval): a timeline row plus, when the work is bound to a
        // conversation, a reply there. Never zero readers.
        var workIds = await db.Approvals
            .Where(a => due.Contains(a.ApprovalId) && a.WorkId != null)
            .Select(a => a.WorkId!.Value).Distinct().ToListAsync(ct);
        foreach (var workId in workIds)
        {
            await AppendNudgeNoticeAsync(db, workId, due.Count, now, ct);
            var version = await db.Works.Where(w => w.WorkId == workId).Select(w => w.Version).FirstOrDefaultAsync(ct);
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = version,
                Kind = "approval.nudged",
                Actor = "consent-sweeper",
                Detail = new JsonObject
                {
                    ["approvals"] = new JsonArray(due.Select(d => JsonValue.Create(d.ToString())).ToArray<JsonNode>()),
                    ["note"] = "consent lapses soon",
                }.ToJsonString(),
                CreatedAt = now,
            });
        }

        return due.Count;
    }

    /// <summary>The nudge notice (step 4: through the notification ledger —
    /// a reply in the work's bound conversation with the team channel as its
    /// fallback rung; unbound works carry the timeline row alone).</summary>
    private async Task AppendNudgeNoticeAsync(FacilitatorDbContext db, Guid workId, int count, DateTime now, CancellationToken ct)
    {
        if (notifications is null)
        {
            return;
        }

        var binding = await db.CommunicationBindings.AsNoTracking().FirstOrDefaultAsync(b => b.WorkId == workId, ct);
        var teamSlug = await db.Works.AsNoTracking()
            .Where(w => w.WorkId == workId).Select(w => w.Team!.Slug).FirstOrDefaultAsync(ct);
        var notice = Guid.NewGuid();
        var target = binding is null
            ? null
            : new Facilitator.Kernel.Integrations.ConversationTarget(
                binding.Provider, null, binding.Channel, binding.RootMessageId, binding.ParentMessageId,
                null, "thread", null);
        await notifications.EmitAsync(db, new Facilitator.Kernel.Notifications.NotificationRequest(
            Audience: "team",
            Text: count == 1
                ? "Consent for this work lapses soon - it needs a decision."
                : $"Consent for this work lapses soon - {count} approvals await a decision.",
            WorkId: workId,
            TeamSlug: teamSlug,
            Target: target,
            CommandId: $"consent-nudge:{notice}"), ct);
    }

    private async Task<int> ExpireOnceAsync(FacilitatorDbContext db, DateTime now, CancellationToken ct)
    {
        var due = await db.Approvals
            .Where(a => a.State == "pending" && a.ExpiresAt <= now)
            .Select(a => new { a.ApprovalId, a.WorkId })
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var ids = due.Select(a => a.ApprovalId).ToArray();
        await db.Approvals
            .Where(a => ids.Contains(a.ApprovalId) && a.State == "pending")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.State, "expired"), ct);
        await db.HeldActions
            .Where(h => ids.Contains(h.ApprovalId) && h.State == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.State, "expired")
                .SetProperty(h => h.UpdatedAt, now), ct);

        // Park the work: a stalled consent means a human must speak (C6) —
        // blocked, reason named, never a silent queue.
        foreach (var (workId, approvalId) in due
                     .Where(a => a.WorkId is not null)
                     .Select(a => (a.WorkId!.Value, a.ApprovalId)))
        {
            var work = await db.Works.FirstOrDefaultAsync(w => w.WorkId == workId, ct);
            if (work is null
                || !StateGuard.TryParseWork(work.State, out var from)
                || !StateGuard.CanTransition(from, WorkState.Blocked))
            {
                continue;
            }

            work.State = WorkState.Blocked.Wire();
            work.Version += 1;
            work.UpdatedAt = now;
            db.WorkEvents.Add(new WorkEventEntity
            {
                EventId = Guid.NewGuid(),
                WorkId = workId,
                WorkVersion = work.Version,
                Kind = "work.consent-expired",
                Actor = "consent-sweeper",
                Detail = new JsonObject
                {
                    ["approvalId"] = approvalId.ToString(),
                    ["reason"] = "consent-expired",
                }.ToJsonString(),
                CreatedAt = now,
            });
        }

        return due.Count;
    }

    /// <summary>The policy read: minutes before expiry to nudge; null = no
    /// policy row (or unparseable) = no nudging.</summary>
    internal static async Task<int?> PolicyNudgeMinutesAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var payload = await db.AdminConfig.AsNoTracking()
            .Where(c => c.Resource == PolicyResource)
            .Select(c => c.Payload)
            .FirstOrDefaultAsync(ct);
        if (payload is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(payload)?["nudgeBeforeMinutes"]?.GetValue<int>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
