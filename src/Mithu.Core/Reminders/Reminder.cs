namespace Mithu.Core.Reminders;

public enum ReminderPriority
{
    Normal,
    Important,

    /// <summary>The notification stays on screen until answered.</summary>
    Urgent,
}

public enum ReminderStatus
{
    Pending,
    Completed,
    Skipped,
}

public enum RepeatKind
{
    None,
    Daily,

    /// <summary>Monday to Friday.</summary>
    Weekdays,
    Weekly,
    Monthly,

    /// <summary>Every <see cref="Repeat.Every"/> <see cref="Repeat.Unit"/>s.</summary>
    Custom,
}

public enum RepeatUnit
{
    Minutes,
    Hours,
    Days,
    Weeks,
    Months,
}

/// <summary>How a reminder repeats. Days, weeks and months follow the wall clock (9:00 stays 9:00 across DST).</summary>
public sealed record Repeat(RepeatKind Kind, int Every = 1, RepeatUnit Unit = RepeatUnit.Days)
{
    public const int MaxEvery = 999;

    /// <summary>"Every 2 minutes" would only be noise.</summary>
    public const int MinMinutes = 5;

    public static Repeat None { get; } = new(RepeatKind.None);

    public static Repeat Daily { get; } = new(RepeatKind.Daily);

    public static Repeat Weekdays { get; } = new(RepeatKind.Weekdays);

    public static Repeat Weekly { get; } = new(RepeatKind.Weekly);

    public static Repeat Monthly { get; } = new(RepeatKind.Monthly);

    public static Repeat Custom(int every, RepeatUnit unit) =>
        new(RepeatKind.Custom, Math.Clamp(every, unit == RepeatUnit.Minutes ? MinMinutes : 1, MaxEvery), unit);

    public bool IsRepeating => Kind != RepeatKind.None;

    /// <summary>"Daily", "Weekdays", "Every 3 days"… or empty when it doesn't repeat.</summary>
    public string Describe() => Kind switch
    {
        RepeatKind.Daily => "Daily",
        RepeatKind.Weekdays => "Weekdays",
        RepeatKind.Weekly => "Weekly",
        RepeatKind.Monthly => "Monthly",
        RepeatKind.Custom when Every == 1 => $"Every {UnitName(Unit, 1)}",
        RepeatKind.Custom => $"Every {Every} {UnitName(Unit, Every)}",
        _ => string.Empty,
    };

    public static string UnitName(RepeatUnit unit, int count) => (unit, count == 1) switch
    {
        (RepeatUnit.Minutes, true) => "minute",
        (RepeatUnit.Minutes, false) => "minutes",
        (RepeatUnit.Hours, true) => "hour",
        (RepeatUnit.Hours, false) => "hours",
        (RepeatUnit.Days, true) => "day",
        (RepeatUnit.Days, false) => "days",
        (RepeatUnit.Weeks, true) => "week",
        (RepeatUnit.Weeks, false) => "weeks",
        (_, true) => "month",
        _ => "months",
    };
}

/// <summary>
/// A reminder. <see cref="StartLocal"/> (wall-clock time in <see cref="TimeZoneId"/>) anchors the repeat pattern;
/// <see cref="DueUtc"/> is the current occurrence. A snooze moves only the alert (<see cref="AlertUtc"/>), not the
/// occurrence. <see cref="NotifiedUtc"/> stops the same alert from being shown twice.
/// Completing or skipping a repeating reminder moves it to its next occurrence and keeps a finished copy as history.
/// Immutable: change with the methods below. User content: never log title or notes.
/// </summary>
public sealed record Reminder(
    string Id,
    string Title,
    string Notes,
    DateTime StartLocal,
    string TimeZoneId,
    Repeat Repeat,
    DateTimeOffset DueUtc,
    ReminderPriority Priority,
    ReminderStatus Status,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? SnoozedUntilUtc = null,
    DateTimeOffset? NotifiedUtc = null,
    DateTimeOffset? DoneUtc = null)
{
    public const int MaxTitleLength = 200;

    /// <summary>When it should go off: the snooze time if snoozed, otherwise the occurrence.</summary>
    public DateTimeOffset AlertUtc => SnoozedUntilUtc ?? DueUtc;

    public bool IsPending => Status == ReminderStatus.Pending;

    public bool IsSnoozed => SnoozedUntilUtc is not null;

    /// <summary>Its time has come and it hasn't been completed, skipped or snoozed past now.</summary>
    public bool IsOverdue(DateTimeOffset nowUtc) => IsPending && AlertUtc <= nowUtc;

    /// <summary>Due and not yet announced for this occurrence or snooze.</summary>
    public bool NeedsAlert(DateTimeOffset nowUtc) => IsOverdue(nowUtc) && (NotifiedUtc is null || NotifiedUtc < AlertUtc);

    /// <summary>
    /// A new reminder at <paramref name="startLocal"/>. A repeating one whose start has passed begins at its next
    /// occurrence; a one-off keeps the time it was given (even in the past; the UI warns first).
    /// </summary>
    public static Reminder Create(
        string title, string notes, DateTime startLocal, TimeZoneInfo zone, Repeat repeat, ReminderPriority priority, DateTimeOffset nowUtc)
    {
        var start = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified);
        return new Reminder(
            Guid.NewGuid().ToString("N"), Clean(title), notes.Trim(), start, zone.Id, repeat,
            FirstDue(start, zone, repeat, nowUtc), priority, ReminderStatus.Pending, nowUtc, nowUtc);
    }

    /// <summary>The time or repeat changed: a fresh pending reminder from the new start.</summary>
    public Reminder Reschedule(DateTime startLocal, TimeZoneInfo zone, Repeat repeat, DateTimeOffset nowUtc)
    {
        var start = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified);
        return this with
        {
            StartLocal = start,
            TimeZoneId = zone.Id,
            Repeat = repeat,
            DueUtc = FirstDue(start, zone, repeat, nowUtc),
            Status = ReminderStatus.Pending,
            SnoozedUntilUtc = null,
            NotifiedUtc = null,
            DoneUtc = null,
            UpdatedUtc = nowUtc,
        };
    }

    /// <summary>Remind again in <paramref name="delay"/> (from now). The occurrence itself doesn't move.</summary>
    public Reminder Snooze(TimeSpan delay, DateTimeOffset nowUtc) => this with { SnoozedUntilUtc = nowUtc + delay, UpdatedUtc = nowUtc };

    /// <summary>
    /// Completes or skips the current occurrence. A one-off just changes status. A repeating one moves to its next
    /// occurrence after both now and the current one, and returns a finished copy of this occurrence for history.
    /// </summary>
    public (Reminder Updated, Reminder? History) Finish(ReminderStatus how, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (how == ReminderStatus.Pending)
        {
            throw new ArgumentOutOfRangeException(nameof(how));
        }

        var done = this with { Status = how, DoneUtc = nowUtc, SnoozedUntilUtc = null, UpdatedUtc = nowUtc };
        if (!Repeat.IsRepeating)
        {
            return (done, null);
        }

        var after = DueUtc > nowUtc ? DueUtc : nowUtc;
        var next = RepeatRule.Next(StartLocal, zone, Repeat, after);
        var history = done with { Id = Guid.NewGuid().ToString("N"), Repeat = Repeat.None, CreatedUtc = nowUtc };
        var updated = this with { DueUtc = next, SnoozedUntilUtc = null, NotifiedUtc = null, UpdatedUtc = nowUtc };
        return (updated, history);
    }

    /// <summary>
    /// A repeating reminder whose next occurrence has also passed (e.g. Mithu was closed) jumps to the latest
    /// occurrence that has passed, so it is announced once rather than once per missed occurrence.
    /// Returns the same instance when nothing changes.
    /// </summary>
    public Reminder CatchUp(TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (!IsPending || !Repeat.IsRepeating)
        {
            return this;
        }

        var next = RepeatRule.Next(StartLocal, zone, Repeat, DueUtc);
        if (next > nowUtc)
        {
            return this;
        }

        var latest = RepeatRule.Latest(StartLocal, zone, Repeat, nowUtc);
        return this with { DueUtc = latest, SnoozedUntilUtc = null, NotifiedUtc = null };
    }

    /// <summary>
    /// The computer moved to another time zone: keep the wall-clock time (9:00 stays 9:00 where you are now).
    /// </summary>
    public Reminder Rezone(TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        if (!IsPending || TimeZoneId == zone.Id)
        {
            return this;
        }

        var oldZone = RepeatRule.FindZone(TimeZoneId);
        var dueLocal = TimeZoneInfo.ConvertTime(DueUtc, oldZone).DateTime;
        var due = RepeatRule.ToUtc(dueLocal, zone);
        return this with { TimeZoneId = zone.Id, DueUtc = due, UpdatedUtc = nowUtc };
    }

    /// <summary>A pending copy ("Title (copy)") with the same time and repeat.</summary>
    public Reminder Duplicate(TimeZoneInfo zone, DateTimeOffset nowUtc) =>
        Create($"{Title} (copy)", Notes, StartLocal, zone, Repeat, Priority, nowUtc) with
        {
            DueUtc = Repeat.IsRepeating ? DueUtc : FirstDue(StartLocal, zone, Repeat, nowUtc),
        };

    public static string Clean(string title)
    {
        var text = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= MaxTitleLength ? text : text[..MaxTitleLength].TrimEnd();
    }

    /// <summary>
    /// A one-off: its start. A repeating one: its first occurrence still ahead (which isn't always the start:
    /// "weekdays" set on a Sunday begins on Monday).
    /// </summary>
    private static DateTimeOffset FirstDue(DateTime startLocal, TimeZoneInfo zone, Repeat repeat, DateTimeOffset nowUtc) =>
        repeat.IsRepeating ? RepeatRule.Next(startLocal, zone, repeat, nowUtc) : RepeatRule.ToUtc(startLocal, zone);
}
