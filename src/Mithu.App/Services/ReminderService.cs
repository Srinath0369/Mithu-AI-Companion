using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Mithu.Core.Interfaces;
using Mithu.Core.Reminders;

namespace Mithu.App.Services;

/// <summary>
/// Owns the reminders while Mithu runs: the in-memory list, saving, and one timer that sleeps until the next alert
/// (at most an hour, and re-checked after sleep, clock and time-zone changes). Due reminders get a native
/// notification; while Mithu is closed Windows shows scheduled ones instead. All on the UI thread.
/// Never logs reminder text.
/// </summary>
public sealed class ReminderService : IDisposable
{
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    /// <summary>How many notifications Windows is asked to show while Mithu is closed.</summary>
    private const int ClosedScheduleLimit = 20;

    private readonly IReminderRepository _repository;
    private readonly IReminderNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<ReminderService> _logger;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private List<Reminder> _all = [];
    private bool _started;
    private bool _listening;
    private bool _preparedForExit;

    public ReminderService(IReminderRepository repository, IReminderNotifier notifier, TimeProvider time, ILogger<ReminderService> logger)
    {
        _repository = repository;
        _notifier = notifier;
        _time = time;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += (_, _) => Recheck();
    }

    /// <summary>The list changed (added, edited, finished, or an alert went off).</summary>
    public event Action? Changed;

    /// <summary>A reminder went off: Mithu reacts too (the notification is already shown).</summary>
    public event Action<Reminder>? Fired;

    /// <summary>Several reminders went off while Mithu was closed.</summary>
    public event Action<int>? Missed;

    /// <summary>A notification was clicked: show Reminders (with this reminder, if any).</summary>
    public event Action<string?>? OpenRequested;

    /// <summary>A short line for Mithu's status text.</summary>
    public event Action<string>? StatusChanged;

    public IReadOnlyList<Reminder> All => _all;

    /// <summary>
    /// True when Mithu will show a reminder itself (on screen, not quiet): the Windows notification then goes
    /// straight to the notification center instead of popping up too.
    /// </summary>
    public Func<bool>? AlertsInApp { get; set; }

    public TimeZoneInfo Zone => _time.LocalTimeZone;

    public DateTimeOffset Now => _time.GetUtcNow();

    public bool StorageWasReset => _repository.StorageWasReset;

    /// <summary>"Today 6:00 PM".</summary>
    public string When(DateTimeOffset utc) => ReminderText.When(utc, Now, Zone);

    /// <summary>
    /// Loads reminders, takes over from notifications scheduled while Mithu was closed, and announces what was missed.
    /// The notification system (and its memory) is only loaded once there is a reminder that could need it.
    /// </summary>
    public void Start(bool launchedFromNotification)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var loaded = Load();
        var now = Now;

        // A pending reminder may have a notification in the notification center whose buttons still need answering.
        if (launchedFromNotification || _all.Any(r => r.IsPending))
        {
            Listen();
        }

        if (!loaded)
        {
            return;
        }

        // Handed to Windows at exit but not shown yet: Mithu is running again, so it shows them itself.
        var stillScheduled = _all.Any(r => r.IsPending && r.NotifiedUtc == r.AlertUtc && r.AlertUtc > now)
            ? _notifier.CancelScheduled().ToHashSet()
            : [];
        var changed = new List<Reminder>();
        for (var i = 0; i < _all.Count; i++)
        {
            var r = _all[i];
            var updated = stillScheduled.Contains(r.Id) && r.IsPending ? r with { NotifiedUtc = null } : r;
            updated = updated.Rezone(Zone, now).CatchUp(Zone, now);
            if (!ReferenceEquals(updated, r))
            {
                _all[i] = updated;
                changed.Add(updated);
            }
        }

        Persist(changed);

        var missed = _all.Where(r => r.NeedsAlert(now)).OrderBy(r => r.AlertUtc).ToList();
        if (missed.Count > 1)
        {
            Listen();
            _notifier.ShowMissed(missed);
            MarkNotified(missed, now);
            _logger.LogInformation("{Count} reminders were missed while Mithu was closed", missed.Count);
            Missed?.Invoke(missed.Count);
        }

        Recheck();
    }

    // ===== Changes from the UI =================================================

    /// <summary>Adds or updates reminders (one transaction). False if they couldn't be saved.</summary>
    public bool Save(params IReadOnlyList<Reminder> reminders)
    {
        if (!Persist(reminders))
        {
            return false;
        }

        foreach (var r in reminders)
        {
            var index = _all.FindIndex(x => x.Id == r.Id);
            if (index >= 0)
            {
                _all[index] = r;
            }
            else
            {
                _all.Add(r);
            }

            // Answered or moved in Mithu: its notification is out of date.
            if (_listening && !r.IsOverdue(Now))
            {
                _notifier.Withdraw(r.Id);
            }
        }

        Recheck();
        return true;
    }

    public bool Delete(string id)
    {
        try
        {
            _repository.Delete(id);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not delete reminder");
            StatusChanged?.Invoke("Couldn't delete the reminder. Please try again.");
            return false;
        }

        _all.RemoveAll(r => r.Id == id);
        if (_listening)
        {
            _notifier.Withdraw(id);
        }

        Recheck();
        return true;
    }

    public Reminder? Find(string id) => _all.Find(r => r.Id == id);

    /// <summary>Done (or Skip) for the current occurrence; a repeating reminder moves on and keeps a history entry.</summary>
    public bool Finish(string id, ReminderStatus how) => Finish(id, how, out _);

    /// <param name="history">The finished copy kept for a repeating reminder (null for a one-off).</param>
    public bool Finish(string id, ReminderStatus how, out Reminder? history)
    {
        history = null;
        if (Find(id) is not { IsPending: true } r)
        {
            return false;
        }

        (var updated, history) = r.Finish(how, Zone, Now);
        return history is null ? Save(updated) : Save(updated, history);
    }

    /// <summary>"Remind me again in…" for a reminder that has gone off (snoozing one still ahead would bring it forward).</summary>
    public bool Snooze(string id, TimeSpan delay) =>
        Find(id) is { IsPending: true } r && r.IsOverdue(Now) && Save(r.Snooze(delay, Now));

    /// <summary>Back to pending (from Done). It doesn't go off again for a time that has already passed.</summary>
    public bool Reopen(string id)
    {
        if (Find(id) is not { IsPending: false } r)
        {
            return false;
        }

        var now = Now;
        return Save(r with { Status = ReminderStatus.Pending, DoneUtc = null, NotifiedUtc = r.AlertUtc <= now ? now : null, UpdatedUtc = now });
    }

    // ===== Time ===============================================================

    /// <summary>After sleep, a clock change or a time-zone change.</summary>
    public void OnClockChanged()
    {
        TimeZoneInfo.ClearCachedData();
        var now = Now;
        var changed = new List<Reminder>();
        for (var i = 0; i < _all.Count; i++)
        {
            var updated = _all[i].Rezone(Zone, now);
            if (!ReferenceEquals(updated, _all[i]))
            {
                _all[i] = updated;
                changed.Add(updated);
            }
        }

        Persist(changed);
        Recheck();
    }

    /// <summary>Mithu is closing: let Windows show the next few reminders while it isn't running.</summary>
    public void PrepareForExit()
    {
        // Once only (shutdown can dispose the shell twice: the app, then the DI container).
        if (!_started || _preparedForExit)
        {
            return;
        }

        _preparedForExit = true;
        _timer.Stop();
        var now = Now;
        var upcoming = _all.Where(r => r.IsPending && r.AlertUtc > now.AddSeconds(5))
            .OrderBy(r => r.AlertUtc)
            .Take(ClosedScheduleLimit)
            .ToList();
        if (upcoming.Count == 0)
        {
            return;
        }

        _notifier.ScheduleWhileClosed(upcoming, r => r.Repeat.IsRepeating || r.IsSnoozed ? When(r.DueUtc) : When(r.AlertUtc));

        // Windows will announce them; if Mithu starts before they're due, Start() takes them back.
        Persist(upcoming.Select(r => r with { NotifiedUtc = r.AlertUtc }).ToList());
    }

    // ===== Internals ===========================================================

    /// <summary>Catches up repeating reminders, announces anything due, and sleeps until the next alert.</summary>
    private void Recheck()
    {
        _timer.Stop();
        var now = Now;
        var changed = new List<Reminder>();
        for (var i = 0; i < _all.Count; i++)
        {
            var updated = _all[i].CatchUp(Zone, now);
            if (!ReferenceEquals(updated, _all[i]))
            {
                _all[i] = updated;
                changed.Add(updated);
            }
        }

        Persist(changed);

        foreach (var due in _all.Where(r => r.NeedsAlert(now)).OrderBy(r => r.AlertUtc).ToList())
        {
            Listen();
            _notifier.Show(due, When(due.DueUtc), quiet: AlertsInApp?.Invoke() == true);
            MarkNotified([due], now);
            _logger.LogInformation("Reminder went off ({Priority})", due.Priority);
            Fired?.Invoke(Find(due.Id) ?? due);
        }

        if (ReminderList.NextAlert(_all, now) is { } next)
        {
            var wait = next - now;
            _timer.Interval = wait > MaxSleep ? MaxSleep : wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait;
            _timer.Start();
        }

        Changed?.Invoke();
    }

    /// <summary>Starts listening for notification clicks (loads the notification system on first call).</summary>
    private void Listen()
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        _notifier.Activated += activation => _dispatcher.BeginInvoke(() => Handle(activation));
    }

    private void Handle(ReminderActivation activation)
    {
        switch (activation.Action)
        {
            case ReminderAction.Done when activation.ReminderId is { } id:
                if (Finish(id, ReminderStatus.Completed))
                {
                    StatusChanged?.Invoke("Done ✓");
                }

                break;

            case ReminderAction.Snooze when activation.ReminderId is { } id:
                var delay = activation.SnoozeFor ?? TimeSpan.FromMinutes(10);
                if (Snooze(id, delay))
                {
                    StatusChanged?.Invoke($"Snoozed for {(int)delay.TotalMinutes} min");
                }

                break;

            default:
                OpenRequested?.Invoke(activation.ReminderId);
                break;
        }
    }

    private void MarkNotified(IReadOnlyList<Reminder> reminders, DateTimeOffset now)
    {
        var updated = reminders.Select(r => r with { NotifiedUtc = now }).ToList();
        foreach (var r in updated)
        {
            var index = _all.FindIndex(x => x.Id == r.Id);
            if (index >= 0)
            {
                _all[index] = r;
            }
        }

        Persist(updated);
    }

    private bool Load()
    {
        try
        {
            _all = _repository.GetAll().ToList();
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not load reminders");
            StatusChanged?.Invoke("Couldn't open your reminders.");
            return false;
        }
    }

    private bool Persist(IReadOnlyList<Reminder> reminders)
    {
        if (reminders.Count == 0)
        {
            return true;
        }

        try
        {
            _repository.Save(reminders);
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not save reminders");
            StatusChanged?.Invoke("Couldn't save your reminder. Please try again.");
            return false;
        }
    }

    private static bool IsStorageError(Exception ex) =>
        ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;

    public void Dispose() => _timer.Stop();
}
