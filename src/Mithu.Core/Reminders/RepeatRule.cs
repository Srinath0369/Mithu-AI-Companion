namespace Mithu.Core.Reminders;

/// <summary>
/// Occurrences of a repeating reminder. Occurrence k is counted from the start, never from the previous one, so a
/// monthly reminder on Jan 31 lands on Feb 28 and then Mar 31 (not Mar 28). Days, weekdays, weeks and months step
/// the wall clock in the reminder's time zone, then convert to UTC; minutes and hours step real time.
/// </summary>
public static class RepeatRule
{
    /// <summary>First occurrence strictly after <paramref name="afterUtc"/> (the start itself for a one-off still ahead).</summary>
    public static DateTimeOffset Next(DateTime startLocal, TimeZoneInfo zone, Repeat repeat, DateTimeOffset afterUtc)
    {
        if (!repeat.IsRepeating)
        {
            return ToUtc(startLocal, zone);
        }

        var k = Estimate(startLocal, zone, repeat, afterUtc);
        while (Occurrence(startLocal, zone, repeat, k) <= afterUtc)
        {
            k++;
        }

        return Occurrence(startLocal, zone, repeat, k);
    }

    /// <summary>Latest occurrence at or before <paramref name="atUtc"/> (the start if none has passed yet).</summary>
    public static DateTimeOffset Latest(DateTime startLocal, TimeZoneInfo zone, Repeat repeat, DateTimeOffset atUtc)
    {
        if (!repeat.IsRepeating)
        {
            return ToUtc(startLocal, zone);
        }

        var k = Estimate(startLocal, zone, repeat, atUtc);
        while (Occurrence(startLocal, zone, repeat, k + 1) <= atUtc)
        {
            k++;
        }

        return Occurrence(startLocal, zone, repeat, k);
    }

    /// <summary>Occurrence number <paramref name="k"/> (0 = the start).</summary>
    public static DateTimeOffset Occurrence(DateTime startLocal, TimeZoneInfo zone, Repeat repeat, long k)
    {
        var every = Math.Max(1, repeat.Every);
        return (repeat.Kind, repeat.Unit) switch
        {
            (RepeatKind.Daily, _) => ToUtc(startLocal.AddDays(k), zone),
            (RepeatKind.Weekly, _) => ToUtc(startLocal.AddDays(7 * k), zone),
            (RepeatKind.Monthly, _) => ToUtc(startLocal.AddMonths((int)k), zone),
            (RepeatKind.Weekdays, _) => ToUtc(NthWeekday(startLocal, k), zone),
            (RepeatKind.Custom, RepeatUnit.Minutes) => ToUtc(startLocal, zone).AddMinutes((double)every * k),
            (RepeatKind.Custom, RepeatUnit.Hours) => ToUtc(startLocal, zone).AddHours((double)every * k),
            (RepeatKind.Custom, RepeatUnit.Days) => ToUtc(startLocal.AddDays((double)every * k), zone),
            (RepeatKind.Custom, RepeatUnit.Weeks) => ToUtc(startLocal.AddDays(7.0 * every * k), zone),
            (RepeatKind.Custom, RepeatUnit.Months) => ToUtc(startLocal.AddMonths((int)(every * k)), zone),
            _ => ToUtc(startLocal, zone),
        };
    }

    /// <summary>
    /// Wall-clock time → UTC. A time skipped by a DST jump (2:30 on spring-forward night) moves forward by the jump
    /// (→ 3:30); a time that happens twice (fall-back night) uses the first one.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(local))
        {
            offset = zone.GetUtcOffset(local.AddHours(-6)); // the offset in force just before the jump
        }
        else if (zone.IsAmbiguousTime(local))
        {
            offset = zone.GetAmbiguousTimeOffsets(local).Max();
        }
        else
        {
            offset = zone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>The zone with this id, or the local zone if it is unknown on this computer.</summary>
    public static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>A safe starting index a little before <paramref name="atUtc"/> (so long gaps don't step one by one).</summary>
    private static long Estimate(DateTime startLocal, TimeZoneInfo zone, Repeat repeat, DateTimeOffset atUtc)
    {
        var elapsed = atUtc - ToUtc(startLocal, zone);
        if (elapsed <= TimeSpan.Zero)
        {
            return 0;
        }

        var every = Math.Max(1, repeat.Every);
        var period = (repeat.Kind, repeat.Unit) switch
        {
            (RepeatKind.Daily, _) => TimeSpan.FromDays(1),
            (RepeatKind.Weekdays, _) => TimeSpan.FromDays(7.0 / 5),
            (RepeatKind.Weekly, _) => TimeSpan.FromDays(7),
            (RepeatKind.Monthly, _) => TimeSpan.FromDays(28),
            (RepeatKind.Custom, RepeatUnit.Minutes) => TimeSpan.FromMinutes(every),
            (RepeatKind.Custom, RepeatUnit.Hours) => TimeSpan.FromHours(every),
            (RepeatKind.Custom, RepeatUnit.Days) => TimeSpan.FromDays(every),
            (RepeatKind.Custom, RepeatUnit.Weeks) => TimeSpan.FromDays(7.0 * every),
            _ => TimeSpan.FromDays(28.0 * every),
        };

        var k = Math.Max(0, (long)(elapsed / period) - 3);
        while (k > 0 && Occurrence(startLocal, zone, repeat, k) > atUtc)
        {
            k = Math.Max(0, k - 3);
        }

        return k;
    }

    /// <summary>The k-th Monday–Friday on or after the start's date, at the start's time.</summary>
    private static DateTime NthWeekday(DateTime start, long k)
    {
        var first = start;
        while (first.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            first = first.AddDays(1);
        }

        // Count from the Monday of the first week so whole weeks are easy.
        var offset = ((int)first.DayOfWeek + 6) % 7; // Monday = 0
        var index = offset + k;
        var monday = first.AddDays(-offset);
        return monday.AddDays((7 * (index / 5)) + (index % 5));
    }
}
