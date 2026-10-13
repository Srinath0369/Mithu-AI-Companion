using System.Globalization;
using System.Text.RegularExpressions;

namespace Mithu.Core.Reminders;

/// <summary>What <see cref="ReminderParser"/> understood. <see cref="WhenLocal"/> is null when no time was given.</summary>
public sealed record ParsedReminder(string Title, DateTime? WhenLocal, Repeat Repeat, ReminderPriority Priority)
{
    public bool HasTime => WhenLocal is not null;
}

/// <summary>
/// Understands everyday reminder phrases, e.g. "Remind me to call Mom tomorrow at 6 PM", "pay rent on the 1st every
/// month", "stretch every 2 hours", "standup weekdays at 9:30", "urgent: send invoice in 20 min".
/// Each phrase it recognises is taken out; what's left is the title. Pure (the caller passes "now").
/// A bare hour ("at 8") means the next 8 o'clock that hasn't passed; on another day 1–6 means afternoon, 7–11 morning.
/// </summary>
public static class ReminderParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Weekday =
        "(monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tues?|wed|thu(?:rs?)?|fri|sat|sun)";

    private const string Month =
        "(jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    private const string Number = @"(\d+|an?|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty|forty|forty-five|fifty|sixty|ninety)";

    private const string Unit = @"(minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|wks?|w|months?)";

    private static readonly Regex Prefix = new(
        @"^\s*(?:please\s+)?(?:(?:set\s+(?:a\s+)?)?reminder\b\s*(?:(?:to|for|about)\b|:)?|remind\s+me\b\s*(?:(?:to|about|that|of)\b)?|don'?t\s+(?:let\s+me\s+)?forget\b\s*(?:(?:to|about)\b)?)\s*",
        Options);

    private static readonly Regex Urgent = new(@"(?:\b(?:urgent(?:ly)?|asap)\b|!!+)\s*:?", Options);
    private static readonly Regex Important = new(@"\b(?:important(?:ly)?|high\s+priority)\b\s*:?", Options);

    // ----- repeat -----
    private static readonly Regex EveryN = new($@"\bevery\s+{Number}\s+{Unit}\b", Options);
    private static readonly Regex EveryOther = new(@"\bevery\s+other\s+(day|week|month)\b", Options);
    private static readonly Regex WeekdaysRepeat = new(@"\b(?:every\s+|on\s+|each\s+)?week\s?days\b|\bevery\s+weekday\b", Options);
    private static readonly Regex EveryWeekday = new($@"\b(?:every|each)\s+{Weekday}s?\b|\bon\s+{Weekday}s\b", Options);
    private static readonly Regex EveryPart = new(@"\b(?:every|each)\s+(morning|afternoon|evening|night)\b", Options);
    private static readonly Regex DailyRepeat = new(@"\b(?:every\s*day|each\s+day|daily)\b", Options);
    private static readonly Regex WeeklyRepeat = new(@"\b(?:every|each)\s+week\b|\bweekly\b", Options);
    private static readonly Regex MonthlyRepeat = new(@"\b(?:every|each)\s+month\b|\bmonthly\b", Options);
    private static readonly Regex HourlyRepeat = new(@"\b(?:every|each)\s+hour\b|\bhourly\b", Options);

    // ----- relative -----
    private static readonly Regex HalfHour = new(@"\bin\s+(?:a\s+)?half\s+(?:an?\s+)?hour\b", Options);
    private static readonly Regex InSpan = new($@"\bin\s+{Number}\s*{Unit}(?:\s+(?:and\s+)?{Number}\s*{Unit})?\b", Options);

    // ----- date -----
    private static readonly Regex DayAfterTomorrow = new(@"\b(?:the\s+)?day\s+after\s+tomorrow\b", Options);
    private static readonly Regex Tomorrow = new(@"\b(?:tomorrow|tmrw|tmr)\b", Options);
    private static readonly Regex Today = new(@"\btoday\b", Options);
    private static readonly Regex Tonight = new(@"\btonight\b", Options);
    private static readonly Regex NextWeek = new(@"\bnext\s+week\b", Options);
    // Short names ("sun", "sat") only after on/next/this, so ordinary words aren't read as days.
    private static readonly Regex NamedDay = new(
        $@"\b(?:(next|this|on|coming)\s+{Weekday}|(monday|tuesday|wednesday|thursday|friday|saturday|sunday))\b", Options);
    private static readonly Regex DayMonth = new($@"\b(?:on\s+)?(?:the\s+)?(\d{{1,2}})(?:st|nd|rd|th)?\s+(?:of\s+)?{Month}\b(?:,?\s+(\d{{4}}))?", Options);
    private static readonly Regex MonthDay = new($@"\b(?:on\s+)?{Month}\s+(\d{{1,2}})(?:st|nd|rd|th)?\b(?:,?\s+(\d{{4}}))?", Options);
    private static readonly Regex OnThe = new(@"\bon\s+the\s+(\d{1,2})(?:st|nd|rd|th)\b", Options);

    // ----- time -----
    private static readonly Regex Noon = new(@"\b(?:at\s+)?(?:noon|midday)\b", Options);
    private static readonly Regex Midnight = new(@"\b(?:at\s+)?midnight\b", Options);
    private static readonly Regex ClockWithMinutes = new(@"(?:\b(?:at|by|@)\s*)?\b(\d{1,2})[:.](\d{2})\s*(a\.?m\.?|p\.?m\.?)?(?![\w])", Options);
    private static readonly Regex ClockWithMeridiem = new(@"(?:\b(?:at|by|@)\s*)?\b(\d{1,2})\s*(a\.?m\.?|p\.?m\.?)(?![\w])", Options);
    private static readonly Regex BareHour = new(@"\b(?:at|@)\s*(\d{1,2})\b(?!\s*(?:minutes?|mins?|hours?|days?|weeks?|months?|st|nd|rd|th)\b)", Options);
    private static readonly Regex PartOfDay = new(@"\b(?:(?:in|this)\s+the\s+|this\s+|in\s+the\s+)?(morning|afternoon|evening|night)\b", Options);

    private static readonly Regex Spaces = new(@"\s{2,}", Options);
    private static readonly Regex LeadingFiller = new(@"^(?:(?:to|about|that|of|on|at|by|for|and|,|:|-)\s+)+", Options);
    private static readonly Regex TrailingFiller = new(@"(?:\s+(?:at|on|by|in|the|every|each|and|from|starting|,))+$", Options);

    private static readonly Regex ClockText = new(@"^\s*(\d{1,2})(?:\s*[:.]\s*(\d{2}))?\s*(a\.?m\.?|p\.?m\.?|a|p)?\s*$", Options);

    /// <summary>A typed time: "6pm", "6:30 PM", "18:30", "7.15", "9" (24-hour when there's no am/pm).</summary>
    public static bool TryParseClock(string text, out TimeSpan time)
    {
        time = default;
        var match = ClockText.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var meridiem = match.Groups[3].Value.Replace(".", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (minute > 59 || hour > 23 || (meridiem.Length > 0 && hour is < 1 or > 12))
        {
            return false;
        }

        hour = meridiem.StartsWith('a') ? hour % 12 : meridiem.StartsWith('p') ? (hour % 12) + 12 : hour;
        time = new TimeSpan(hour, minute, 0);
        return true;
    }

    private static readonly Regex DurationText = new(
        @"^\s*(\d{1,4})\s*(m|min|mins|minutes?|h|hr|hrs|hours?)?\s*(?:(\d{1,2})\s*(m|min|mins|minutes?))?\s*$", Options);

    /// <summary>
    /// A typed snooze length: "20" (minutes), "45 min", "2h", "1h 30m". Between 1 minute and 24 hours.
    /// </summary>
    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;
        var match = DurationText.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var first = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var hours = match.Groups[2].Value.StartsWith('h');
        if (match.Groups[3].Success && !hours)
        {
            return false; // "20m 10m"
        }

        duration = hours ? TimeSpan.FromHours(first) : TimeSpan.FromMinutes(first);
        if (match.Groups[3].Success)
        {
            duration += TimeSpan.FromMinutes(int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        return duration >= TimeSpan.FromMinutes(1) && duration <= TimeSpan.FromHours(24);
    }

    /// <summary>"Remind me…", "Reminder:", "Don't forget…" — text typed on Home that should become a reminder.</summary>
    public static bool LooksLikeReminder(string text) => Prefix.IsMatch(text) && Prefix.Match(text).Length > 0;

    public static ParsedReminder Parse(string text, DateTime nowLocal)
    {
        var s = " " + text.Trim() + " ";
        s = Prefix.Replace(s.TrimStart(), " ", 1);

        // Priority
        var priority = ReminderPriority.Normal;
        if (Take(ref s, Urgent) is not null)
        {
            priority = ReminderPriority.Urgent;
        }
        else if (Take(ref s, Important) is not null)
        {
            priority = ReminderPriority.Important;
        }

        // Repeat (before dates, so "every monday" isn't read as "on monday")
        var repeat = Repeat.None;
        DayOfWeek? repeatDay = null;
        string? repeatPart = null;
        if (Take(ref s, EveryN) is { } everyN)
        {
            var n = NumberValue(everyN.Groups[1].Value);
            repeat = UnitOf(everyN.Groups[2].Value) switch
            {
                RepeatUnit.Days when n == 1 => Repeat.Daily,
                RepeatUnit.Weeks when n == 1 => Repeat.Weekly,
                RepeatUnit.Months when n == 1 => Repeat.Monthly,
                var unit => Repeat.Custom(n, unit),
            };
        }
        else if (Take(ref s, EveryOther) is { } other)
        {
            repeat = Repeat.Custom(2, UnitOf(other.Groups[1].Value));
        }
        else if (Take(ref s, WeekdaysRepeat) is not null)
        {
            repeat = Repeat.Weekdays;
        }
        else if (Take(ref s, EveryWeekday) is { } everyDay)
        {
            repeat = Repeat.Weekly;
            repeatDay = DayOf(everyDay.Groups[1].Success ? everyDay.Groups[1].Value : everyDay.Groups[2].Value);
        }
        else if (Take(ref s, EveryPart) is { } everyPart)
        {
            repeat = Repeat.Daily;
            repeatPart = everyPart.Groups[1].Value;
        }
        else if (Take(ref s, DailyRepeat) is not null)
        {
            repeat = Repeat.Daily;
        }
        else if (Take(ref s, WeeklyRepeat) is not null)
        {
            repeat = Repeat.Weekly;
        }
        else if (Take(ref s, MonthlyRepeat) is not null)
        {
            repeat = Repeat.Monthly;
        }
        else if (Take(ref s, HourlyRepeat) is not null)
        {
            repeat = Repeat.Custom(1, RepeatUnit.Hours);
        }

        // Relative time ("in 20 minutes") wins over everything else.
        TimeSpan? span = null;
        if (Take(ref s, HalfHour) is not null)
        {
            span = TimeSpan.FromMinutes(30);
        }
        else if (Take(ref s, InSpan) is { } inSpan)
        {
            span = SpanOf(inSpan.Groups[1].Value, inSpan.Groups[2].Value);
            if (inSpan.Groups[3].Success)
            {
                span += SpanOf(inSpan.Groups[3].Value, inSpan.Groups[4].Value);
            }
        }

        // Date
        var today = nowLocal.Date;
        DateTime? date = null;
        string? defaultPart = null;
        DayOfWeek? namedDay = null;
        var namedNext = false;
        if (Take(ref s, DayAfterTomorrow) is not null)
        {
            date = today.AddDays(2);
        }
        else if (Take(ref s, Tomorrow) is not null)
        {
            date = today.AddDays(1);
        }
        else if (Take(ref s, Tonight) is not null)
        {
            date = today;
            defaultPart = "night";
        }
        else if (Take(ref s, Today) is not null)
        {
            date = today;
        }
        else if (Take(ref s, NextWeek) is not null)
        {
            date = NextDay(today, DayOfWeek.Monday, includeToday: false);
        }
        else if (Take(ref s, DayMonth) is { } dm)
        {
            date = DateOf(today, int.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture), MonthOf(dm.Groups[2].Value), dm.Groups[3].Value);
        }
        else if (Take(ref s, MonthDay) is { } md)
        {
            date = DateOf(today, int.Parse(md.Groups[2].Value, CultureInfo.InvariantCulture), MonthOf(md.Groups[1].Value), md.Groups[3].Value);
        }
        else if (Take(ref s, OnThe) is { } onThe)
        {
            var day = int.Parse(onThe.Groups[1].Value, CultureInfo.InvariantCulture);
            date = DateOf(today, day, today.Month, string.Empty, rollMonth: true);
        }
        else if (Take(ref s, NamedDay) is { } named)
        {
            namedDay = DayOf(named.Groups[2].Success ? named.Groups[2].Value : named.Groups[3].Value);
            namedNext = named.Groups[1].Value.Equals("next", StringComparison.OrdinalIgnoreCase);
        }

        // Time of day
        TimeSpan? time = null;
        var ambiguous = false;
        var dayShift = 0;
        if (Take(ref s, Noon) is not null)
        {
            time = TimeSpan.FromHours(12);
        }
        else if (Take(ref s, Midnight) is not null)
        {
            time = TimeSpan.Zero;
            dayShift = 1;
        }
        else if (Take(ref s, ClockWithMinutes) is { } hm && ValidClock(hm.Groups[1].Value, hm.Groups[2].Value))
        {
            (time, ambiguous) = ClockOf(int.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(hm.Groups[2].Value, CultureInfo.InvariantCulture), hm.Groups[3].Value);
        }
        else if (Take(ref s, ClockWithMeridiem) is { } hp && int.Parse(hp.Groups[1].Value, CultureInfo.InvariantCulture) is >= 1 and <= 12)
        {
            (time, ambiguous) = ClockOf(int.Parse(hp.Groups[1].Value, CultureInfo.InvariantCulture), 0, hp.Groups[2].Value);
        }
        else if (Take(ref s, BareHour) is { } bare && int.Parse(bare.Groups[1].Value, CultureInfo.InvariantCulture) is >= 0 and <= 23)
        {
            (time, ambiguous) = ClockOf(int.Parse(bare.Groups[1].Value, CultureInfo.InvariantCulture), 0, string.Empty);
        }

        var part = repeatPart ?? (Take(ref s, PartOfDay) is { } p ? p.Groups[1].Value : defaultPart);
        if (time is { } t && ambiguous && part is not null)
        {
            // "8 in the evening", "at 9 tonight"
            var pm = !part.Equals("morning", StringComparison.OrdinalIgnoreCase);
            time = pm && t.Hours < 12 ? t + TimeSpan.FromHours(12) : t;
            ambiguous = false;
        }

        var title = Title(s);

        // ----- Put it together -----
        // A habit ("standup weekdays at 9:30") reads a bare hour like another day would: 7–11 morning, 1–6 afternoon.
        var habit = repeat.IsRepeating;
        DateTime? when;
        if (span is { } delay)
        {
            when = nowLocal + delay;
        }
        else
        {
            if (namedDay is not null || repeatDay is not null)
            {
                var targetDay = namedDay ?? repeatDay!.Value;
                var at = time ?? PartTime(part) ?? TimeSpan.FromHours(9);
                var candidate = NextDay(today, targetDay, includeToday: !namedNext);
                if (candidate == today && candidate + Resolve(at, ambiguous, otherDay: habit, nowLocal, candidate) <= nowLocal)
                {
                    candidate = candidate.AddDays(7);
                }

                date = candidate;
            }

            if (date is { } d)
            {
                var at = time is { } tt ? Resolve(tt, ambiguous, otherDay: habit || d != today, nowLocal, d) : PartTime(part) ?? TimeSpan.FromHours(9);
                when = d.AddDays(dayShift) + at;
            }
            else if (time is { } clock)
            {
                var at = Resolve(clock, ambiguous, otherDay: habit, nowLocal, today);
                var candidate = today.AddDays(dayShift) + at;
                if (candidate <= nowLocal)
                {
                    candidate = today.AddDays(1) + Resolve(clock, ambiguous, otherDay: true, nowLocal, today.AddDays(1));
                }

                when = candidate;
            }
            else if (part is not null)
            {
                var candidate = today + PartTime(part)!.Value;
                when = candidate > nowLocal || repeat.IsRepeating ? candidate : candidate.AddDays(1);
            }
            else if (repeat.IsRepeating)
            {
                when = repeat.Kind == RepeatKind.Custom && repeat.Unit is RepeatUnit.Minutes or RepeatUnit.Hours
                    ? nowLocal + Step(repeat)
                    : NextNine(nowLocal);
            }
            else
            {
                when = null;
            }
        }

        // Clock times are whole minutes; "in 5 minutes" keeps its seconds so it really is 5 minutes.
        if (when is { } w && span is null)
        {
            when = new DateTime(w.Year, w.Month, w.Day, w.Hour, w.Minute, 0, DateTimeKind.Unspecified);
        }

        return new ParsedReminder(title, when, repeat, priority);
    }

    // ===== Helpers =============================================================

    /// <summary>Removes the first match from <paramref name="s"/> and returns it (null if none).</summary>
    private static Match? Take(ref string s, Regex regex)
    {
        var match = regex.Match(s);
        if (!match.Success)
        {
            return null;
        }

        s = s[..match.Index] + " " + s[(match.Index + match.Length)..];
        return match;
    }

    private static string Title(string rest)
    {
        var title = Spaces.Replace(rest, " ").Trim();
        for (var i = 0; i < 3; i++)
        {
            title = LeadingFiller.Replace(title, string.Empty).Trim();
            title = TrailingFiller.Replace(title, string.Empty).Trim();
            title = title.Trim(' ', ',', '.', ';', ':', '-', '!', '?');
        }

        if (title.Length == 0)
        {
            return "Reminder";
        }

        return Reminder.Clean(char.ToUpper(title[0], CultureInfo.CurrentCulture) + title[1..]);
    }

    /// <summary>A bare hour that could be am or pm: the next one ahead today, or the day rule on another day.</summary>
    private static TimeSpan Resolve(TimeSpan time, bool ambiguous, bool otherDay, DateTime nowLocal, DateTime date)
    {
        if (!ambiguous || time.Hours >= 12)
        {
            return time;
        }

        if (otherDay)
        {
            return time.Hours is >= 1 and <= 6 ? time + TimeSpan.FromHours(12) : time;
        }

        if (date + time > nowLocal)
        {
            return time;
        }

        return time + TimeSpan.FromHours(12);
    }

    private static (TimeSpan Time, bool Ambiguous) ClockOf(int hour, int minute, string meridiem)
    {
        var m = meridiem.Replace(".", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (m == "am")
        {
            return (new TimeSpan(hour % 12, minute, 0), false);
        }

        if (m == "pm")
        {
            return (new TimeSpan((hour % 12) + 12, minute, 0), false);
        }

        // 24-hour clock ("18:30", "at 0") is clear; 1–12 could be either.
        return (new TimeSpan(hour % 24, minute, 0), hour is >= 1 and <= 12);
    }

    private static bool ValidClock(string hour, string minute) =>
        int.Parse(hour, CultureInfo.InvariantCulture) is >= 0 and <= 23 && int.Parse(minute, CultureInfo.InvariantCulture) is >= 0 and <= 59;

    private static TimeSpan? PartTime(string? part) => part?.ToLowerInvariant() switch
    {
        "morning" => TimeSpan.FromHours(9),
        "afternoon" => TimeSpan.FromHours(14),
        "evening" => TimeSpan.FromHours(18),
        "night" => TimeSpan.FromHours(20),
        _ => null,
    };

    private static DateTime NextNine(DateTime nowLocal)
    {
        var nine = nowLocal.Date.AddHours(9);
        return nine > nowLocal ? nine : nine.AddDays(1);
    }

    private static TimeSpan Step(Repeat repeat) => repeat.Unit == RepeatUnit.Minutes
        ? TimeSpan.FromMinutes(repeat.Every)
        : TimeSpan.FromHours(repeat.Every);

    private static DateTime NextDay(DateTime today, DayOfWeek day, bool includeToday)
    {
        var days = ((int)day - (int)today.DayOfWeek + 7) % 7;
        if (days == 0 && !includeToday)
        {
            days = 7;
        }

        return today.AddDays(days);
    }

    /// <summary>Day/month with an optional year; without a year a date already gone means next year (or next month).</summary>
    private static DateTime? DateOf(DateTime today, int day, int month, string year, bool rollMonth = false)
    {
        var y = year.Length == 4 ? int.Parse(year, CultureInfo.InvariantCulture) : today.Year;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(y, month))
        {
            return null;
        }

        var date = new DateTime(y, month, day);
        if (year.Length == 0 && date < today)
        {
            if (rollMonth)
            {
                var next = today.AddMonths(1);
                return day <= DateTime.DaysInMonth(next.Year, next.Month) ? new DateTime(next.Year, next.Month, day) : null;
            }

            date = day <= DateTime.DaysInMonth(y + 1, month) ? new DateTime(y + 1, month, day) : date;
        }

        return date;
    }

    private static int MonthOf(string name) => name[..3].ToLowerInvariant() switch
    {
        "jan" => 1,
        "feb" => 2,
        "mar" => 3,
        "apr" => 4,
        "may" => 5,
        "jun" => 6,
        "jul" => 7,
        "aug" => 8,
        "sep" => 9,
        "oct" => 10,
        "nov" => 11,
        _ => 12,
    };

    private static DayOfWeek DayOf(string name) => name[..3].ToLowerInvariant() switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    private static RepeatUnit UnitOf(string unit) => unit.ToLowerInvariant() switch
    {
        "m" or "min" or "mins" or "minute" or "minutes" => RepeatUnit.Minutes,
        "h" or "hr" or "hrs" or "hour" or "hours" => RepeatUnit.Hours,
        "d" or "day" or "days" => RepeatUnit.Days,
        "w" or "wk" or "wks" or "week" or "weeks" => RepeatUnit.Weeks,
        _ => RepeatUnit.Months,
    };

    private static TimeSpan SpanOf(string number, string unit)
    {
        var n = NumberValue(number);
        return UnitOf(unit) switch
        {
            RepeatUnit.Minutes => TimeSpan.FromMinutes(n),
            RepeatUnit.Hours => TimeSpan.FromHours(n),
            RepeatUnit.Days => TimeSpan.FromDays(n),
            RepeatUnit.Weeks => TimeSpan.FromDays(7 * n),
            _ => TimeSpan.FromDays(30 * n),
        };
    }

    private static int NumberValue(string text) => text.ToLowerInvariant() switch
    {
        "a" or "an" or "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        "eight" => 8,
        "nine" => 9,
        "ten" => 10,
        "eleven" => 11,
        "twelve" => 12,
        "fifteen" => 15,
        "twenty" => 20,
        "thirty" => 30,
        "forty" => 40,
        "forty-five" => 45,
        "fifty" => 50,
        "sixty" => 60,
        "ninety" => 90,
        var digits => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? Math.Min(n, 100_000) : 1,
    };
}
