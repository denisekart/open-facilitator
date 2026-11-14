using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Identity;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Ofac.Protocol.Validation;

namespace Facilitator.Kernel.Work;

/// <summary>
/// The schedule sweeper (A10): recurring cadences create work AND a
/// team-visible notification naming the accountable owner — a fired schedule
/// is never a silent item. One pass serializes across replicas with an
/// advisory lock (the NudgeSweeper precedent); a firing that lands is
/// idempotent per (schedule, due slot) through the deterministic command id,
/// so two replicas (or a retry) create the work at most once.
/// </summary>
public sealed class ScheduleSweeper(
    IDbContextFactory<FacilitatorDbContext> contextFactory,
    Notifications.NotificationLedger notifications,
    AuditWriter audit,
    TimeProvider timeProvider,
    ILogger<ScheduleSweeper> logger,
    IOptions<ScheduleSweeper.ScheduleOptions>? options = null) : BackgroundService
{
    /// <summary>Config section for the sweep loop.</summary>
    public sealed class ScheduleOptions
    {
        /// <summary>Config section name.</summary>
        public const string SectionName = "Schedules";

        /// <summary>Seconds between hosted sweep passes.</summary>
        public int SweepIntervalSeconds { get; set; } = 60;
    }

    /// <summary>Advisory key serializing schedule sweeps across kernel replicas.</summary>
    public const int SweepLockKey = 420006;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = Math.Max(1, (options?.Value ?? new ScheduleOptions()).SweepIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), stoppingToken);
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                // The sweep is advisory: one bad pass must never kill the loop.
                logger.LogWarning(ex, "schedule sweep pass failed");
            }
        }
    }

    /// <summary>One sweep (public for fake-clock tests and the admin trigger):
    /// every enabled schedule whose <c>next_run_at</c> fell due fires exactly
    /// once for that slot — the work + notification land, the row advances to
    /// the next slot. Returns the number of firings (a pass skipped because a
    /// peer holds the lock returns 0).</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!await TrySweepLockAsync(db, ct))
            {
                return 0;
            }

            var fired = await SweepLockedAsync(db, ct);
            await tx.CommitAsync(ct);
            return fired;
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

    private async Task<int> SweepLockedAsync(FacilitatorDbContext db, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var due = await db.Schedules
            .Where(s => s.Enabled && s.NextRunAt <= now)
            .OrderBy(s => s.NextRunAt)
            .ToListAsync(ct);
        if (due.Count == 0)
        {
            return 0;
        }

        var fired = 0;
        foreach (var schedule in due)
        {
            var teamSlug = await db.Teams.AsNoTracking()
                .Where(t => t.TeamId == schedule.TeamId).Select(t => t.Slug).FirstOrDefaultAsync(ct);
            var commandId = $"schedule:{schedule.ScheduleId:N}:{schedule.NextRunAt:yyyyMMddHHmm}";
            if (!await db.Outbox.AsNoTracking().AnyAsync(o => o.CommandId == commandId, ct)
                && !await db.Works.AsNoTracking().AnyAsync(w => w.Origin == commandId, ct))
            {
                var projectId = await db.Projects.AsNoTracking()
                    .Where(p => p.TeamId == schedule.TeamId)
                    .OrderBy(p => p.CreatedAt)
                    .Select(p => (Guid?)p.ProjectId).FirstOrDefaultAsync(ct);
                var workId = Guid.NewGuid();
                db.Works.Add(new WorkEntity
                {
                    WorkId = workId,
                    ProjectId = projectId,
                    TeamId = schedule.TeamId,
                    Objective = schedule.Objective,
                    State = "open",
                    Version = 1,
                    OwnerActor = schedule.OwnerActor,
                    Origin = commandId,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                db.WorkEvents.Add(new WorkEventEntity
                {
                    EventId = Guid.NewGuid(),
                    WorkId = workId,
                    WorkVersion = 1,
                    Kind = "created",
                    Actor = $"schedule:{schedule.ScheduleId:N}",
                    Detail = JsonSerializer.Serialize(new Dictionary<string, string?>
                    {
                        ["via"] = "schedule",
                        ["cadence"] = schedule.Cadence,
                        ["owner"] = schedule.OwnerActor,
                    }),
                    CreatedAt = now,
                });

                // A10: the item announces itself — team-visible, naming the
                // accountable owner.
                await notifications.EmitAsync(db, new Notifications.NotificationRequest(
                    Audience: "team",
                    Text: $"Scheduled work: {schedule.Objective}"
                        + (schedule.OwnerActor is { Length: > 0 } owner
                            ? $" (owner {owner})"
                            : " (no owner named - a team operator must take it)"),
                    WorkId: workId,
                    TeamSlug: teamSlug,
                    CommandId: $"notify:{commandId}"), ct);

                audit.Append(db, new AuditRecord(
                    Actor: $"schedule:{schedule.ScheduleId:N}",
                    Action: "schedule.fired",
                    Resource: $"work/{workId}",
                    Decision: "allow",
                    Reason: $"cadence {schedule.Cadence} due {schedule.NextRunAt:O}",
                    EvidenceRef: null,
                    PolicyVersion: "schedules-v1",
                    Detail: "{}"));
            }

            schedule.LastRunAt = schedule.NextRunAt;
            schedule.NextRunAt = ScheduleSpec.NextOccurrence(schedule.Cadence, schedule.Timezone, schedule.NextRunAt, now);
            fired++;
        }

        await db.SaveChangesAsync(ct);
        return fired;
    }

}
