using System.Text.Json;

using Facilitator.Kernel.Shared;
using Facilitator.Kernel.Shared.Persistence;

using Microsoft.EntityFrameworkCore;

namespace Facilitator.Kernel.Teams;

/// <summary>
/// Schedules (A10): a team's recurring "create this work" cadences. The
/// roster authority law applies (operator anywhere, team-operator for its own
/// team). A firing creates the work AND a team-visible notification — never a
/// silent item; the sweeper does the firing, the surface only plans.
/// </summary>
public static partial class TeamEndpoints
{
    private static void MapSchedules(RouteGroupBuilder group)
    {
        group.MapGet("/teams/{slug}/schedules", async (
            string slug,
            TeamRosterStore roster,
            IDbContextFactory<FacilitatorDbContext> contextFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await ReadAuthorityAsync(http, slug, roster, ct) is { } denial)
            {
                return denial;
            }

            await using var db = await contextFactory.CreateDbContextAsync(ct);
            return Results.Ok(await db.Schedules.AsNoTracking()
                .Where(s => s.Team!.Slug == slug)
                .OrderBy(s => s.NextRunAt)
                .Select(s => new
                {
                    s.ScheduleId,
                    s.Objective,
                    s.Cadence,
                    s.Timezone,
                    s.NextRunAt,
                    s.LastRunAt,
                    s.Enabled,
                    s.OwnerActor,
                    s.CreatedBy,
                    s.CreatedAt,
                })
                .ToListAsync(ct));
        });

        group.MapPut("/teams/{slug}/schedules/{id:guid}", async (
            string slug,
            Guid id,
            HttpRequest request,
            TeamRosterStore roster,
            IDbContextFactory<FacilitatorDbContext> contextFactory,
            TimeProvider time,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await AuthorityMayAsync(http, slug, roster, ct) is { } denial)
            {
                return denial;
            }

            if (!ChangeReason(request, out var reason))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid(ChangeReasonHint));
            }

            var body = await RequestBodies.ReadAsync(request, ct);
            if (body.TooLarge)
            {
                return Results.Problem(ProblemErrors.PayloadTooLarge(body.Bytes));
            }

            return await RequestBodyJson.ParseAsync(body, async document =>
            {
                var root = document.RootElement;
                var objective = root.TryGetProperty("objective", out var o) ? o.GetString() : null;
                var cadence = root.TryGetProperty("cadence", out var c) ? c.GetString() : null;
                var timezone = root.TryGetProperty("timezone", out var z) ? z.GetString() : null;
                var owner = root.TryGetProperty("ownerActor", out var oa) ? oa.GetString() : null;
                var enabled = !root.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False;
                if (string.IsNullOrWhiteSpace(objective) || !IsValidCadence(cadence))
                {
                    return Results.Problem(ProblemErrors.ConfigInvalid(
                        """cadence must be interval:<n>m | daily:HH:mm | cron:<5 fields>; objective required"""));
                }

                if (timezone is { Length: > 0 } zone && !IsValidZone(zone))
                {
                    return Results.Problem(ProblemErrors.ConfigInvalid($"unknown timezone '{zone}'"));
                }

                await using var db = await contextFactory.CreateDbContextAsync(ct);
                var team = await db.Teams.FirstOrDefaultAsync(t => t.Slug == slug, ct);
                if (team is null)
                {
                    return Results.Problem(ProblemErrors.TeamUnknown(slug));
                }

                var now = time.GetUtcNow().UtcDateTime;
                var row = await db.Schedules.FirstOrDefaultAsync(s => s.ScheduleId == id, ct);
                if (row is not null && row.TeamId != team.TeamId)
                {
                    return Results.Problem(ProblemErrors.TeamNotVisible(slug));
                }

                if (row is null)
                {
                    db.Schedules.Add(new ScheduleEntity
                    {
                        ScheduleId = id,
                        TeamId = team.TeamId,
                        Objective = objective,
                        Cadence = cadence!,
                        Timezone = timezone,
                        NextRunAt = Facilitator.Kernel.Work.ScheduleSweeper.NextOccurrence(cadence!, timezone, now, now),
                        Enabled = enabled,
                        OwnerActor = owner,
                        CreatedBy = ActorOf(http),
                        CreatedAt = now,
                    });
                }
                else
                {
                    row.Objective = objective;
                    row.Cadence = cadence!;
                    row.Timezone = timezone;
                    row.Enabled = enabled;
                    row.OwnerActor = owner;
                    row.NextRunAt = Facilitator.Kernel.Work.ScheduleSweeper.NextOccurrence(cadence!, timezone, row.LastRunAt ?? now, now);
                }

                await db.SaveChangesAsync(ct);
                return Results.Ok(new { scheduleId = id, nextRunAt = row?.NextRunAt });
            });
        });

        group.MapPost("/teams/{slug}/schedules/{id:guid}/disable", async (
            string slug,
            Guid id,
            HttpRequest request,
            TeamRosterStore roster,
            IDbContextFactory<FacilitatorDbContext> contextFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (await AuthorityMayAsync(http, slug, roster, ct) is { } denial)
            {
                return denial;
            }

            if (!ChangeReason(request, out _))
            {
                return Results.Problem(ProblemErrors.ConfigInvalid(ChangeReasonHint));
            }

            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var row = await db.Schedules.FirstOrDefaultAsync(s => s.ScheduleId == id && s.Team!.Slug == slug, ct);
            if (row is null)
            {
                return Results.Problem(ProblemErrors.Create(
                    "schedule-unknown", "Not found", StatusCodes.Status404NotFound, $"no schedule {id} on team {slug}"));
            }

            row.Enabled = false;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static bool IsValidCadence(string? cadence)
    {
        if (cadence is not { Length: > 0 })
        {
            return false;
        }

        if (cadence.StartsWith("interval:", StringComparison.Ordinal))
        {
            var tail = cadence["interval:".Length..];
            return tail.EndsWith('m') && int.TryParse(tail[..^1], out var minutes) && minutes > 0;
        }

        if (cadence.StartsWith("daily:", StringComparison.Ordinal))
        {
            var tail = cadence["daily:".Length..];
            return TimeSpan.TryParseExact(tail, @"h\:mm", null, out _)
                || TimeSpan.TryParseExact(tail, @"hh\:mm", null, out _);
        }

        if (cadence.StartsWith("cron:", StringComparison.Ordinal))
        {
            try
            {
                Facilitator.Kernel.Work.ScheduleSweeper.Cron.NextUtc(
                    cadence["cron:".Length..], new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsValidZone(string zone)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(zone);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
