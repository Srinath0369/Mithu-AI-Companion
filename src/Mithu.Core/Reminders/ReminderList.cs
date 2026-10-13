using System.Globalization;

namespace Mithu.Core.Reminders;

/// <summary>Which reminders a list shows.</summary>
public enum ReminderView
{
    /// <summary>Pending, not yet due (snoozed ones included).</summary>
    Upcoming,

    /// <summary>Pending and due: waiting for Done, Skip or Snooze.</summary>
    Overdue,

    /// <summary>Completed or skipped.</summary>
    Done,
}

/// <summary>Date filter: by the reminder's time (Upcoming/Overdue) or when it was finished (Done).</summary>
public enum ReminderDateFilter
{
    Any,
    Today,
    Tomorrow,

    /// <summary>Yesterday for finished reminders.</summary>
    Yesterday,

    /// <summary>The current Monday–Sunday week.</summary>
    ThisWeek,
}

public sealed record ReminderFilter(
    ReminderView View,
    string? Search = null,
    ReminderPriority? Priority = null,
    ReminderDateFilter Date = ReminderDateFilter.Any);

/// <summary>Filtering, ordering and counting reminders (pure; the caller passes now and the local zone).</summary>
public static class ReminderList
{
    /// <summary>
    /// Upcoming: soonest first. Overdue: most urgent first, then the oldest. Done: most recently finished first.
    /// </summary>
    public static IReadOnlyList<Reminder> Apply(IEnumerable<Reminder> all, ReminderFilter filter, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var words = (filter.Search ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;

        var matches = all.Where(r => ViewOf(r, nowUtc) == filter.View)
            .Where(r => filter.Priority is null || r.Priority == filter.Priority)
            .Where(r => words.All(w => r.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                                       || r.Notes.Contains(w, StringComparison.CurrentCultureIgnoreCase)))
            .Where(r => InDate(DateOf(r, filter.View, zone), today, filter.Date));

        return filter.View switch
        {
            ReminderView.Upcoming => matches.OrderBy(r => r.AlertUtc).ThenByDescending(r => r.Priority).ToList(),
            ReminderView.Overdue => matches.OrderByDescending(r => r.Priority).ThenBy(r => r.AlertUtc).ToList(),
            _ => matches.OrderByDescending(r => r.DoneUtc ?? r.UpdatedUtc).ToList(),
        };
    }

    public static ReminderView ViewOf(Reminder reminder, DateTimeOffset nowUtc) =>
        !reminder.IsPending ? ReminderView.Done
        : reminder.IsOverdue(nowUtc) ? ReminderView.Overdue
        : ReminderView.Upcoming;

    public static int Count(IEnumerable<Reminder> all, ReminderView view, DateTimeOffset nowUtc) =>
        all.Count(r => ViewOf(r, nowUtc) == view);

    /// <summary>For Home's "Up next" card: the most pressing overdue reminder, else the next one coming.</summary>
    public static Reminder? NextUp(IEnumerable<Reminder> all, DateTimeOffset nowUtc)
    {
        var pending = all.Where(r => r.IsPending).ToList();
        return pending.Where(r => r.IsOverdue(nowUtc)).OrderByDescending(r => r.Priority).ThenBy(r => r.AlertUtc).FirstOrDefault()
            ?? pending.OrderBy(r => r.AlertUtc).FirstOrDefault();
    }

    /// <summary>When the next pending reminder goes off (null if none): the scheduler sleeps until then.</summary>
    public static DateTimeOffset? NextAlert(IEnumerable<Reminder> all, DateTimeOffset nowUtc) =>
        all.Where(r => r.IsPending && r.AlertUtc > nowUtc).Select(r => (DateTimeOffset?)r.AlertUtc).Min();

    private static DateTime DateOf(Reminder r, ReminderView view, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(view == ReminderView.Done ? r.DoneUtc ?? r.UpdatedUtc : r.AlertUtc, zone).Date;

    private static bool InDate(DateTime date, DateTime today, ReminderDateFilter filter)
    {
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        return filter switch
        {
            ReminderDateFilter.Today => date == today,
            ReminderDateFilter.Tomorrow => date == today.AddDays(1),
            ReminderDateFilter.Yesterday => date == today.AddDays(-1),
            ReminderDateFilter.ThisWeek => date >= monday && date < monday.AddDays(7),
            _ => true,
        };
    }
}

/// <summary>Friendly times for the UI: "Today 6:00 PM", "Tomorrow 9:00 AM", "Fri 9:00 AM", "12 Oct 9:00 AM".</summary>
public static class ReminderText
{
    public static string When(DateTimeOffset utc, DateTimeOffset nowUtc, TimeZoneInfo zone, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = TimeZoneInfo.ConvertTime(utc, zone).DateTime;
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        var time = local.ToString("t", culture);
        var days = (local.Date - today).Days;
        return days switch
        {
            0 => $"Today {time}",
            1 => $"Tomorrow {time}",
            -1 => $"Yesterday {time}",
            > 1 and < 7 => $"{local.ToString("ddd", culture)} {time}",
            _ when local.Year == today.Year => $"{local.ToString("d MMM", culture)} {time}",
            _ => $"{local.ToString("d MMM yyyy", culture)} {time}",
        };
    }

    /// <summary>"in 5 min", "in 2 h", "in 3 days", "5 min ago", "2 days ago", "now".</summary>
    public static string Relative(DateTimeOffset utc, DateTimeOffset nowUtc)
    {
        var span = utc - nowUtc;
        var ahead = span > TimeSpan.Zero;
        var size = span.Duration();
        var text = size.TotalMinutes < 1 ? null
            : size.TotalMinutes < 60 ? $"{(int)size.TotalMinutes} min"
            : size.TotalHours < 24 ? $"{(int)size.TotalHours} h"
            : (int)size.TotalDays == 1 ? "1 day"
            : $"{(int)size.TotalDays} days";
        return text is null ? "now" : ahead ? $"in {text}" : $"{text} ago";
    }

    public static string PriorityName(ReminderPriority priority) => priority switch
    {
        ReminderPriority.Important => "Important",
        ReminderPriority.Urgent => "Urgent",
        _ => "Normal",
    };
}
