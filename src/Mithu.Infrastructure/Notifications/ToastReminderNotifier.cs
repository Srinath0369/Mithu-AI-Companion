using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Mithu.Core.Interfaces;
using Mithu.Core.Reminders;

namespace Mithu.Infrastructure.Notifications;

/// <summary>
/// Windows toast notifications for reminders. The toolkit registers Mithu's app id and a COM activator for the
/// current user on first use, so buttons work even from the notification center or after Mithu was closed
/// (Windows starts Mithu to handle the click). Never logs reminder text.
/// </summary>
public sealed class ToastReminderNotifier : IReminderNotifier, IDisposable
{
    private const string Group = "reminders";
    private const string SnoozeInput = "snooze";
    private static readonly int[] SnoozeChoices = [5, 10, 15, 30];

    private readonly ILogger<ToastReminderNotifier> _logger;
    private bool _subscribed;

    public ToastReminderNotifier(ILogger<ToastReminderNotifier> logger)
    {
        _logger = logger;
    }

    private event Action<ReminderActivation>? ActivatedCore;

    public event Action<ReminderActivation>? Activated
    {
        add
        {
            ActivatedCore += value;
            EnsureSubscribed();
        }

        remove => ActivatedCore -= value;
    }

    public void Show(Reminder reminder, string whenText, bool quiet = false) =>
        Safely("show", () => Build(reminder, whenText).Show(t =>
        {
            t.Tag = reminder.Id;
            t.Group = Group;
            t.SuppressPopup = quiet;
        }));

    public void ShowMissed(IReadOnlyList<Reminder> missed) =>
        Safely("show missed", () =>
        {
            var titles = string.Join(" · ", missed.Take(3).Select(r => r.Title)) + (missed.Count > 3 ? " …" : string.Empty);
            new ToastContentBuilder()
                .AddArgument("action", "open")
                .AddText($"You missed {missed.Count} reminders")
                .AddText(titles)
                .AddButton(new ToastButton().SetContent("Show them").AddArgument("action", "open"))
                .Show(t =>
                {
                    t.Tag = "missed";
                    t.Group = Group;
                });
        });

    public void Withdraw(string reminderId) =>
        Safely("withdraw", () => ToastNotificationManagerCompat.History.Remove(reminderId, Group));

    public void ScheduleWhileClosed(IReadOnlyList<Reminder> upcoming, Func<Reminder, string> whenText) =>
        Safely("schedule", () =>
        {
            foreach (var reminder in upcoming)
            {
                Build(reminder, whenText(reminder)).Schedule(reminder.AlertUtc, t =>
                {
                    t.Tag = reminder.Id;
                    t.Group = Group;
                });
            }

            _logger.LogInformation("Scheduled {Count} reminder notifications for while Mithu is closed", upcoming.Count);
        });

    public IReadOnlyList<string> CancelScheduled()
    {
        var ids = new List<string>();
        Safely("cancel scheduled", () =>
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            foreach (var scheduled in notifier.GetScheduledToastNotifications())
            {
                if (scheduled.Group == Group)
                {
                    ids.Add(scheduled.Tag);
                    notifier.RemoveFromSchedule(scheduled);
                }
            }
        });

        return ids;
    }

    private static ToastContentBuilder Build(Reminder reminder, string whenText)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("action", "open")
            .AddArgument("id", reminder.Id)
            .AddText(reminder.Title, hintMaxLines: 2)
            .AddText(reminder.Repeat.IsRepeating ? $"{whenText} · {reminder.Repeat.Describe()}" : whenText);

        if (reminder.Notes.Length > 0)
        {
            builder.AddText(reminder.Notes);
        }

        if (reminder.Priority != ReminderPriority.Normal)
        {
            builder.AddAttributionText(ReminderText.PriorityName(reminder.Priority));
        }

        var box = new ToastSelectionBox(SnoozeInput) { DefaultSelectionBoxItemId = "10" };
        foreach (var minutes in SnoozeChoices)
        {
            box.Items.Add(new ToastSelectionBoxItem(minutes.ToString(CultureInfo.InvariantCulture), $"{minutes} minutes"));
        }

        builder.AddToastInput(box)
            .AddButton(new ToastButton().SetContent("Snooze").AddArgument("action", "snooze").AddArgument("id", reminder.Id)
                .SetBackgroundActivation())
            .AddButton(new ToastButton().SetContent("Done").AddArgument("action", "done").AddArgument("id", reminder.Id)
                .SetBackgroundActivation());

        // Urgent: stays on screen until answered (the "reminder" scenario).
        if (reminder.Priority == ReminderPriority.Urgent)
        {
            builder.SetToastScenario(ToastScenario.Reminder);
        }

        return builder;
    }

    private void EnsureSubscribed()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Safely("listen", () => ToastNotificationManagerCompat.OnActivated += OnActivated);
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var args = ToastArguments.Parse(e.Argument);
            args.TryGetValue("id", out var id);
            var action = args.TryGetValue("action", out var name) ? name : "open";
            ReminderActivation activation = action switch
            {
                "done" => new(ReminderAction.Done, id),
                "snooze" => new(ReminderAction.Snooze, id, TimeSpan.FromMinutes(
                    e.UserInput.TryGetValue(SnoozeInput, out var value) && int.TryParse(value as string, NumberStyles.None, CultureInfo.InvariantCulture, out var m)
                        ? Math.Clamp(m, 1, 24 * 60)
                        : 10)),
                _ => new(ReminderAction.Open, id),
            };

            _logger.LogInformation("Reminder notification: {Action}", activation.Action);
            ActivatedCore?.Invoke(activation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not handle a notification click");
        }
    }

    /// <summary>Notifications are a nice-to-have: a failure (e.g. disabled by policy) never breaks reminders.</summary>
    /// <summary>
    /// Uninstalling: takes Mithu's scheduled notifications back and removes its notification registration (app id,
    /// COM activator) from Windows, so nothing fires or points at a removed Mithu. Best effort.
    /// </summary>
    public static void RemoveFromWindows()
    {
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            foreach (var scheduled in notifier.GetScheduledToastNotifications())
            {
                notifier.RemoveFromSchedule(scheduled);
            }
        }
        catch (Exception)
        {
            // Nothing scheduled, or notifications unavailable: nothing to take back.
        }

        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch (Exception)
        {
            // Never registered: nothing to remove.
        }
    }

    private void Safely(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification {What} failed", what);
        }
    }

    public void Dispose()
    {
        if (_subscribed)
        {
            ToastNotificationManagerCompat.OnActivated -= OnActivated;
        }
    }
}
