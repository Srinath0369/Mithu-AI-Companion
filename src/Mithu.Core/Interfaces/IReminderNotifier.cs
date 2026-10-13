using Mithu.Core.Reminders;

namespace Mithu.Core.Interfaces;

/// <summary>What the user did with a reminder notification.</summary>
public enum ReminderAction
{
    /// <summary>Clicked the notification body: show the reminder in Mithu.</summary>
    Open,
    Done,
    Snooze,
}

/// <param name="ReminderId">Null for the "you missed N reminders" summary.</param>
/// <param name="SnoozeFor">How long, for <see cref="ReminderAction.Snooze"/>.</param>
public sealed record ReminderActivation(ReminderAction Action, string? ReminderId, TimeSpan? SnoozeFor = null);

/// <summary>Native Windows notifications for reminders (shown even when Mithu is hidden, and while it's closed).</summary>
public interface IReminderNotifier
{
    /// <summary>A notification button or body was clicked. Raised on a background thread.</summary>
    event Action<ReminderActivation>? Activated;

    /// <summary>
    /// Shows a reminder now with Snooze and Done. Urgent reminders stay on screen until answered.
    /// <paramref name="quiet"/>: Mithu is alerting in the app, so it goes straight to the notification center without popping up.
    /// </summary>
    void Show(Reminder reminder, string whenText, bool quiet = false);

    /// <summary>One notification instead of many after Mithu was closed: "You missed 3 reminders".</summary>
    void ShowMissed(IReadOnlyList<Reminder> missed);

    /// <summary>Takes a reminder's notification out of the notification center (it was answered in Mithu).</summary>
    void Withdraw(string reminderId);

    /// <summary>Asks Windows to show these at their alert time even though Mithu won't be running.</summary>
    void ScheduleWhileClosed(IReadOnlyList<Reminder> upcoming, Func<Reminder, string> whenText);

    /// <summary>Cancels notifications scheduled by <see cref="ScheduleWhileClosed"/> that Windows hasn't shown yet; returns their reminder ids.</summary>
    IReadOnlyList<string> CancelScheduled();
}
