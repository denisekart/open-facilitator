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
            schedule.NextRunAt = NextOccurrence(schedule.Cadence, schedule.Timezone, schedule.NextRunAt, now);
            fired++;
        }

        await db.SaveChangesAsync(ct);
        return fired;
    }

    /// <summary>The next occurrence strictly after <paramref name="from"/>,
    /// capped at <paramref name="now"/> only when the plan fell behind (a
    /// disabled-style catch-up runs at most one slot per pass). Unsupported
    /// cadences land an hour out and are visible via a failed parse at write
    /// time (the endpoints refuse them).</summary>
    internal static DateTime NextOccurrence(string cadence, string? timezone, DateTime from, DateTime now)
    {
        var zone = string.IsNullOrWhiteSpace(timezone)
            ? TimeSpan.Zero
            : TimeZoneInfo.FindSystemTimeZoneById(timezone).GetUtcOffset(from);
        var local = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Unspecified).AddHours(-zone.TotalHours), zone);
        DateTime ComputeNext()
        {
            if (cadence.StartsWith("interval:", StringComparison.Ordinal)
                && int.TryParse(cadence["interval:".Length..].TrimEnd('m'), out var minutes) && minutes > 0)
            {
                return local.AddMinutes(minutes).UtcDateTime;
            }

            if (cadence.StartsWith("daily:", StringComparison.Ordinal)
                && (TimeSpan.TryParseExact(cadence["daily:".Length..], @"h\:mm", null, out var wall)
                    || TimeSpan.TryParseExact(cadence["daily:".Length..], @"hh\:mm", null, out wall)))
            {
                // Wall-clock math in the schedule's zone; UTC = wall - offset.
                var nextWall = local.Date.Add(wall);
                if (new DateTimeOffset(nextWall, zone) <= local)
                {
                    nextWall = nextWall.AddDays(1);
                }

                return DateTime.SpecifyKind(nextWall - zone, DateTimeKind.Utc);
            }

            if (cadence.StartsWith("cron:", StringComparison.Ordinal))
            {
                return Cron.NextUtc(cadence["cron:".Length..], local.UtcDateTime, zone);
            }

            return local.AddHours(1).UtcDateTime;
        }

        return ComputeNext();
    }

    /// <summary>Minimal five-field cron (minute hour dom month dow, with
    /// <c>*</c>, lists and single-value steps): a stored plan, evaluated
    /// minute-accurate. Deliberately small — a full cron library is not worth
    /// a dependency for team cadences.</summary>
    internal static class Cron
    {
        /// <summary>The first minute strictly after <paramref name="afterUtc"/>
        /// matching the expression (wall-clock evaluated in <paramref name="zone"/>).</summary>
        public static DateTime NextUtc(string expression, DateTime afterUtc, TimeSpan zone)
        {
            var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length != 5)
            {
                throw new FormatException($"cron expression needs five fields, got {fields.Length}");
            }

            var minute = ParseField(fields[0], 0, 59);
            var hour = ParseField(fields[1], 0, 23);
            var day = ParseField(fields[2], 1, 31);
            var month = ParseField(fields[3], 1, 12);
            var weekday = ParseField(fields[4], 0, 6);
            var domRestricted = fields[2] != "*";
            var dowRestricted = fields[4] != "*";

            // Cron math is wall-clock in the schedule's zone.
            var cursor = new DateTime(afterUtc.Ticks + TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            var local = cursor + zone;
            for (var steps = 0; steps < 366 * 24 * 60 + 60; steps++, cursor = cursor.AddMinutes(1), local = cursor + zone)
            {
                var dayOk = day.Contains(local.Day) && weekday.Contains((int)local.DayOfWeek);
                if (domRestricted && dowRestricted)
                {
                    dayOk = day.Contains(local.Day) || weekday.Contains((int)local.DayOfWeek);
                }

                if (minute.Contains(local.Minute) && hour.Contains(local.Hour)
                    && month.Contains(local.Month) && dayOk)
                {
                    return cursor;
                }
            }

            throw new FormatException($"cron expression '{expression}' matches no minute within a year");
        }

        private static HashSet<int> ParseField(string field, int min, int max)
        {
            var set = new HashSet<int>();
            foreach (var part in field.Split(','))
            {
                var body = part;
                var step = 1;
                var slash = body.IndexOf('/', StringComparison.Ordinal);
                if (slash >= 0)
                {
                    step = int.Parse(body[(slash + 1)..]);
                    body = body[..slash];
                }

                if (body == "*")
                {
                    for (var v = min; v <= max; v += step)
                    {
                        set.Add(v);
                    }

                    continue;
                }

                var dash = body.IndexOf('-', StringComparison.Ordinal);
                if (dash > 0)
                {
                    var lo = int.Parse(body[..dash]);
                    var hi = int.Parse(body[(dash + 1)..]);
                    for (var v = lo; v <= hi; v += step)
                    {
                        set.Add(v);
                    }

                    continue;
                }

                set.Add(int.Parse(body));
            }

            return set;
        }
    }
}
