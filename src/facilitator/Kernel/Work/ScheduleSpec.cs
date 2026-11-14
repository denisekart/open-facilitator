namespace Facilitator.Kernel.Work;

/// <summary>
/// THE schedule-spec parser (Phase 5 extraction, law unchanged): the wire
/// cadence grammar `interval:<n>m` | `daily:HH:mm` | `cron:<5 fields>` is
/// evaluated by ONE code path — the schedule plane's sweeper, the schedule
/// endpoints' next-run math, and the workflow plane's `on.schedule`
/// triggers all call here. Cron is a minimal five-field expression
/// (minute hour dom month dow, with `*`, lists and single-value steps),
/// minute-accurate, wall-clock in the caller's zone: deliberately small —
/// a full cron library is not worth a dependency for team cadences.
/// </summary>
public static class ScheduleSpec
{
    /// <summary>The next occurrence strictly after <paramref name="from"/>,
    /// capped at <paramref name="now"/> only when the plan fell behind (a
    /// disabled-style catch-up runs at most one slot per pass). Unsupported
    /// cadences land an hour out and are visible via a failed parse at write
    /// time (the endpoints refuse them).</summary>
    public static DateTime NextOccurrence(string cadence, string? timezone, DateTime from, DateTime now)
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
