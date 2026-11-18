using Facilitator.Kernel.Work;

namespace Facilitator.Tests.Work;

/// <summary>
/// The ONE schedule-spec parser (pure, no DB): the wire cadence grammar
/// <c>interval:&lt;n&gt;m</c> | <c>daily:HH:mm</c> | <c>cron:&lt;5 fields&gt;</c>
/// evaluated minute-accurate, wall-clock in the caller's zone, and the
/// unsupported-cadence hour-out fallback. The sweeper, the endpoint
/// next-run math, and the workflow plane's <c>on.schedule</c> triggers all
/// ride this code path, so the legs are pinned here directly.
/// </summary>
public sealed class ScheduleSpecTests
{
    private static readonly DateTime From = new(2026, 3, 10, 8, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = From;

    [Theory]
    [InlineData("interval:15m", "2026-03-10T08:30:00Z")]
    [InlineData("interval:1h", "2026-03-10T09:15:00Z")] // non-numeric tail: unsupported ⇒ hour out
    [InlineData("interval:0m", "2026-03-10T09:15:00Z")] // zero ⇒ unsupported ⇒ hour out
    [InlineData("interval:-5m", "2026-03-10T09:15:00Z")] // negative unparsable ⇒ hour out
    [InlineData("weekly:mon", "2026-03-10T09:15:00Z")] // unknown grammar ⇒ hour out
    [InlineData("", "2026-03-10T09:15:00Z")]
    public void IntervalAndUnsupportedCadences(string cadence, string expectedUtc)
    {
        var next = ScheduleSpec.NextOccurrence(cadence, null, From, Now);
        Assert.Equal(DateTime.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), next);
    }

    [Fact]
    public void IntervalIsStrictlyAfterFrom()
    {
        var next = ScheduleSpec.NextOccurrence("interval:1m", null, From, Now);
        Assert.True(next > From);
        Assert.Equal(From.AddMinutes(1), next);
    }

    [Theory]
    // 09:00 UTC is still ahead of 08:15 ⇒ today's wall slot.
    [InlineData("daily:09:00", "2026-03-10T09:00:00Z")]
    // 07:30 already passed ⇒ rolls to tomorrow's wall slot.
    [InlineData("daily:07:30", "2026-03-11T07:30:00Z")]
    // Exactly-now is not "strictly after" ⇒ tomorrow.
    [InlineData("daily:08:15", "2026-03-11T08:15:00Z")]
    // Single-digit hour accepted (h:mm form).
    [InlineData("daily:9:00", "2026-03-10T09:00:00Z")]
    // Malformed time ⇒ unsupported ⇒ hour out.
    [InlineData("daily:25:00", "2026-03-10T09:15:00Z")]
    [InlineData("daily:09", "2026-03-10T09:15:00Z")]
    public void DailyIsWallClockUtcMath(string cadence, string expectedUtc)
    {
        var next = ScheduleSpec.NextOccurrence(cadence, null, From, Now);
        Assert.Equal(DateTime.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), next);
    }

    [Fact]
    public void DailyInNamedZoneAnchorsTheOffsetSubtractedWallClock()
    {
        // As-built zone math (both legs subtract the offset): the wall
        // frame is `from - offset` and the answer is `wall - offset`, so an
        // east zone's wall frame lags UTC by the offset. 2026-03-10 is
        // before the EU switch ⇒ +01:00: the frame reads 07:15, the 09:00
        // wall slot is ahead, and the answer is 09:00 - 01:00 = 08:00 UTC
        // the same day. (A zero offset — every other suite's cadence — is
        // plain wall-clock UTC.)
        var next = ScheduleSpec.NextOccurrence("daily:09:00", "Europe/Berlin", From, Now);
        Assert.Equal(new DateTime(2026, 3, 10, 8, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void DailyZoneOffsetAheadOfUtcStillAnchorsWallClock()
    {
        // UTC+5:30: 08:15 UTC = 13:45 local; 14:00 local slot is ahead ⇒
        // today 14:00 local = 08:30 UTC.
        var next = ScheduleSpec.NextOccurrence("daily:14:00", "Asia/Kolkata", From, Now);
        Assert.Equal(new DateTime(2026, 3, 10, 8, 30, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void UnknownTimeZoneThrows() =>
        Assert.Throws<TimeZoneNotFoundException>(
            () => ScheduleSpec.NextOccurrence("daily:09:00", "Mars/Olympus_Mons", From, Now));

    [Theory]
    // Every minute: the minute strictly after `from`.
    [InlineData("* * * * *", "2026-03-10T08:16:00Z")]
    // Half-hour steps via a list.
    [InlineData("0,30 * * * *", "2026-03-10T08:30:00Z")]
    // Single-value step: */20 = {0,20,40} ⇒ next is 08:20.
    [InlineData("*/20 * * * *", "2026-03-10T08:20:00Z")]
    // Range with step.
    [InlineData("10-40/15 8 * * *", "2026-03-10T08:25:00Z")]
    // Hour-boundary walk: 08:16..08:59 miss, 09:00 hits.
    [InlineData("0 9 * * *", "2026-03-10T09:00:00Z")]
    // Day-of-week: 2026-03-10 is a Tuesday (2); next Friday (5) 12:00.
    [InlineData("0 12 * * 5", "2026-03-13T12:00:00Z")]
    // dom AND dow both restricted ⇒ OR semantics: Wednesday (3) of the same
    // week matches the dow arm even though the 4th of the month is a Friday.
    [InlineData("0 0 4 * 3", "2026-03-11T00:00:00Z")]
    // dom-only restriction.
    [InlineData("0 0 15 * *", "2026-03-15T00:00:00Z")]
    public void CronLegs(string expression, string expectedUtc)
    {
        var next = ScheduleSpec.NextOccurrence("cron:" + expression, null, From, Now);
        Assert.Equal(DateTime.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), next);
    }

    [Fact]
    public void CronIsWallClockInZones()
    {
        // As-built (see the daily-zone test): the seeded instant is
        // `from - 5:30` = 2026-03-09T21:16Z, the cron walk adds the zone
        // back to name wall-clock fields, so 2026-03-10 09:00 wall answers
        // 2026-03-10T03:30Z — the same offset-subtracted law as the daily
        // leg, one code path.
        var next = ScheduleSpec.NextOccurrence("cron:0 9 * * *", "Asia/Kolkata", From, Now);
        Assert.Equal(new DateTime(2026, 3, 10, 3, 30, 0, DateTimeKind.Utc), next);
    }

    [Theory]
    [InlineData("cron:* * *", "five fields")] // four fields
    [InlineData("cron:* * * * * *", "five fields")] // six fields
    [InlineData("cron:not a cron", "five fields")] // words
    public void CronFieldCountFailsClosed(string cadence, string messageFragment)
    {
        var error = Assert.Throws<FormatException>(() => ScheduleSpec.NextOccurrence(cadence, null, From, Now));
        Assert.Contains(messageFragment, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CronBadStepAndRangeThrow()
    {
        // A non-numeric field body lands in int.Parse — FormatException
        // (the endpoints refuse the cadence at write time; the sweeper
        // never sees an unparsed spec).
        Assert.Throws<FormatException>(() => ScheduleSpec.NextOccurrence("cron:a * * * *", null, From, Now));
        Assert.Throws<FormatException>(() => ScheduleSpec.NextOccurrence("cron:*/x * * * *", null, From, Now));
    }

    [Fact]
    public void CronNeverMatchesIsVisible()
    {
        // February 30 never exists: the year-walk bails with a FormatException.
        var error = Assert.Throws<FormatException>(
            () => ScheduleSpec.NextOccurrence("cron:0 0 30 2 *", null, From, Now));
        Assert.Contains("matches no minute within a year", error.Message, StringComparison.Ordinal);
    }
}
