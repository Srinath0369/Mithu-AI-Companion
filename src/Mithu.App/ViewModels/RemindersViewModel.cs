using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithu.App.Services;
using Mithu.Core.Reminders;

namespace Mithu.App.ViewModels;

/// <summary>A reminder as shown in the list.</summary>
public sealed class ReminderItem(Reminder reminder, ReminderView view, string whenText, string detail)
{
    public Reminder Reminder { get; } = reminder;

    public string Title => Reminder.Title;

    /// <summary>"Today 6:00 PM".</summary>
    public string WhenText { get; } = whenText;

    /// <summary>"in 2 h · Daily", "3 h overdue", "Snoozed until 6:10 PM", "Completed · Today 6:05 PM".</summary>
    public string Detail { get; } = detail;

    public ReminderPriority Priority => Reminder.Priority;

    public bool IsOverdue => view == ReminderView.Overdue;

    public bool IsDone => view == ReminderView.Done;

    public bool IsPending => !IsDone;

    public bool IsRepeating => Reminder.Repeat.IsRepeating;

    public bool HasNotes => Reminder.Notes.Length > 0;

    public string NotesPreview => Reminder.Notes.ReplaceLineEndings(" ");
}

/// <summary>A choice chip (tab, preset, repeat, priority) that knows whether it is selected.</summary>
public sealed partial class ReminderChip(string key, string label, Action<ReminderChip> select) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty]
    private string _label = label;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => select(this);
}

/// <summary>
/// Reminders page: Upcoming / Overdue / Done lists with search and filters, quick add in plain English
/// ("call Mom tomorrow at 6 PM") with a live preview, and an editor (presets, date and time, repeat, priority, notes).
/// </summary>
public sealed partial class RemindersViewModel : ObservableObject
{
    private static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(6);
    public static readonly int[] SnoozeMinutes = [5, 10, 15, 30];

    private readonly ReminderService _service;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _undoTimer;

    private Reminder? _editing;           // the reminder being edited (null = a new one)
    private DateTimeOffset? _shownUtc;     // the time the editor opened with (an unchanged time isn't rescheduled)
    private bool _loadingEditor;
    private TimeSpan? _relative;           // "In 15 min" preset: counted from when Save is pressed
    private IReadOnlyList<Reminder> _undoRestore = [];
    private IReadOnlyList<string> _undoRemove = [];

    public RemindersViewModel(ReminderService service)
    {
        _service = service;

        Tabs = [Chip("Upcoming", "Upcoming", SelectTab), Chip("Overdue", "Overdue", SelectTab), Chip("Done", "Done", SelectTab)];
        Presets =
        [
            Chip("5", "5 min", SelectPreset), Chip("15", "15 min", SelectPreset), Chip("30", "30 min", SelectPreset),
            Chip("60", "1 hour", SelectPreset), Chip("tomorrow", "Tomorrow", SelectPreset), Chip("custom", "Custom", SelectPreset),
        ];
        Repeats =
        [
            Chip(nameof(RepeatKind.None), "Never", SelectRepeat), Chip(nameof(RepeatKind.Daily), "Daily", SelectRepeat),
            Chip(nameof(RepeatKind.Weekdays), "Weekdays", SelectRepeat), Chip(nameof(RepeatKind.Weekly), "Weekly", SelectRepeat),
            Chip(nameof(RepeatKind.Monthly), "Monthly", SelectRepeat), Chip(nameof(RepeatKind.Custom), "Custom", SelectRepeat),
        ];
        Priorities =
        [
            Chip(nameof(ReminderPriority.Normal), "Normal", SelectPriority),
            Chip(nameof(ReminderPriority.Important), "Important", SelectPriority),
            Chip(nameof(ReminderPriority.Urgent), "Urgent", SelectPriority),
        ];
        Tabs[0].IsSelected = true;

        // Keeps "in 5 min" fresh while the list is on screen.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += (_, _) => Reload();

        _undoTimer = new DispatcherTimer { Interval = UndoDuration };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            CanUndo = false;
        };

        _service.Changed += Reload;
    }

    /// <summary>A short line for Mithu's status text.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>The editor opened (true = new reminder): the page puts the cursor in the title.</summary>
    public event Action<bool>? EditorOpened;

    // ===== List =============================================================

    public ObservableCollection<ReminderItem> Items { get; } = [];

    public IReadOnlyList<ReminderChip> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDoneView))]
    private ReminderView _view;

    public bool IsDoneView => View == ReminderView.Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    private string _search = string.Empty;

    public bool HasSearch => Search.Length > 0;

    [ObservableProperty]
    private bool _isSearchOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterText), nameof(HasFilter))]
    private ReminderPriority? _priorityFilter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterText), nameof(HasFilter))]
    private ReminderDateFilter _dateFilter;

    public bool HasFilter => PriorityFilter is not null || DateFilter != ReminderDateFilter.Any;

    /// <summary>"Filter", or what is filtered ("Today · Urgent").</summary>
    public string FilterText
    {
        get
        {
            var parts = new List<string>();
            if (DateFilter != ReminderDateFilter.Any)
            {
                parts.Add(DateName(DateFilter));
            }

            if (PriorityFilter is { } p)
            {
                parts.Add(ReminderText.PriorityName(p));
            }

            return parts.Count == 0 ? "Filter" : string.Join(" · ", parts);
        }
    }

    public bool HasItems => Items.Count > 0;

    public string EmptyText => HasSearch || HasFilter ? "No reminders match."
        : View switch
        {
            ReminderView.Overdue => "Nothing overdue. Nice!",
            ReminderView.Done => "Nothing finished yet.",
            _ => "No reminders yet.\nType one above, like “call Mom tomorrow at 6 PM”.",
        };

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private string _undoText = string.Empty;

    // ===== Home ==============================================================

    /// <summary>Tile badge: "2 due" (overdue) or the number coming up; empty when there's nothing.</summary>
    public string TileBadge
    {
        get
        {
            var overdue = ReminderList.Count(_service.All, ReminderView.Overdue, _service.Now);
            var upcoming = ReminderList.Count(_service.All, ReminderView.Upcoming, _service.Now);
            return overdue > 0 ? $"{overdue} due" : upcoming > 0 ? upcoming.ToString(CultureInfo.CurrentCulture) : string.Empty;
        }
    }

    public bool HasTileBadge => TileBadge.Length > 0;

    public bool TileBadgeIsDue => ReminderList.Count(_service.All, ReminderView.Overdue, _service.Now) > 0;

    /// <summary>"Up next" card on Home.</summary>
    public string UpNextTitle => ReminderList.NextUp(_service.All, _service.Now)?.Title ?? "Nothing planned — enjoy the calm";

    public string UpNextWhen
    {
        get
        {
            if (ReminderList.NextUp(_service.All, _service.Now) is not { } r)
            {
                return string.Empty;
            }

            return r.IsOverdue(_service.Now)
                ? $"Overdue · {_service.When(r.AlertUtc)}"
                : $"{_service.When(r.AlertUtc)} · {ReminderText.Relative(r.AlertUtc, _service.Now)}";
        }
    }

    public bool HasUpNext => ReminderList.NextUp(_service.All, _service.Now) is not null;

    /// <summary>The next reminder for the header line: its title, "in 20 min", overdue, and how far ahead.</summary>
    public (string Title, string In, bool Overdue, TimeSpan Ahead)? NextForHeader()
    {
        if (ReminderList.NextUp(_service.All, _service.Now) is not { } r)
        {
            return null;
        }

        var now = _service.Now;
        return (r.Title, ReminderText.Relative(r.AlertUtc, now), r.IsOverdue(now), r.AlertUtc - now);
    }

    public bool UpNextIsOverdue => ReminderList.NextUp(_service.All, _service.Now)?.IsOverdue(_service.Now) == true;

    // ===== In-app alert ======================================================

    /// <summary>The reminder that just went off, shown in Mithu's header (peek) until answered or dismissed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlert), nameof(AlertTitle), nameof(AlertDetail), nameof(AlertIsUrgent))]
    private Reminder? _alert;

    public bool HasAlert => Alert is not null;

    public string AlertTitle => Alert is { } r ? $"⏰ {r.Title}" : string.Empty;

    /// <summary>"Today 6:00 PM · Urgent · Bring the charger".</summary>
    public string AlertDetail
    {
        get
        {
            if (Alert is not { } r)
            {
                return string.Empty;
            }

            var parts = new List<string> { _service.When(r.DueUtc) };
            if (r.Priority != ReminderPriority.Normal)
            {
                parts.Add(ReminderText.PriorityName(r.Priority));
            }

            if (r.Notes.Length > 0)
            {
                parts.Add(r.Notes.ReplaceLineEndings(" "));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Urgent alerts stay out until answered.</summary>
    public bool AlertIsUrgent => Alert?.Priority == ReminderPriority.Urgent;

    public void ShowAlert(Reminder reminder) => Alert = reminder;

    public void ClearAlert() => Alert = null;

    [RelayCommand]
    private void AlertDone()
    {
        if (Alert is { } r)
        {
            Alert = null;
            Finish(r, ReminderStatus.Completed);
        }
    }

    /// <summary>Snooze the alert for a chosen length (menu choice or a typed custom one).</summary>
    public void SnoozeAlert(TimeSpan delay)
    {
        if (Alert is { } r)
        {
            Alert = null;
            if (_service.Snooze(r.Id, delay))
            {
                Say($"Snoozed for {DescribeDelay(delay)} · back at {_service.When(_service.Now + delay)}");
            }
        }
    }

    /// <summary>"10 min", "1 h", "1 h 30 min".</summary>
    public static string DescribeDelay(TimeSpan delay) =>
        delay.TotalMinutes < 60 ? $"{(int)delay.TotalMinutes} min"
        : delay.Minutes == 0 ? $"{(int)delay.TotalHours} h"
        : $"{(int)delay.TotalHours} h {delay.Minutes} min";

    // ===== Quick add =========================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuickPreview), nameof(HasQuickPreview))]
    [NotifyCanExecuteChangedFor(nameof(QuickAddCommand))]
    private string _quickText = string.Empty;

    public bool HasQuickPreview => QuickText.Trim().Length > 0;

    /// <summary>What Enter will create: "Call Mom · Tomorrow 6:00 PM · Daily · Urgent".</summary>
    public string QuickPreview
    {
        get
        {
            if (!HasQuickPreview)
            {
                return string.Empty;
            }

            var p = Parse(QuickText);
            var parts = new List<string> { p.Title };
            parts.Add(p.WhenLocal is { } when ? _service.When(RepeatRule.ToUtc(when, _service.Zone)) : "pick a time ↵");
            if (p.Repeat.IsRepeating)
            {
                parts.Add(p.Repeat.Describe());
            }

            if (p.Priority != ReminderPriority.Normal)
            {
                parts.Add(ReminderText.PriorityName(p.Priority));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Enter in the quick box: saves straight away when a time was given, otherwise opens the editor to pick one.</summary>
    [RelayCommand(CanExecute = nameof(HasQuickPreview))]
    private void QuickAdd()
    {
        if (QuickCreate(QuickText))
        {
            QuickText = string.Empty;
        }
    }

    /// <summary>
    /// Creates a reminder from plain English (also used by Home: "remind me to…"). With a time it is saved at once;
    /// without one the editor opens with the title filled in. False if nothing was saved or opened.
    /// </summary>
    public bool QuickCreate(string text)
    {
        var p = Parse(text);
        if (p.WhenLocal is not { } when)
        {
            OpenEditor(null, p);
            return true;
        }

        var reminder = Reminder.Create(p.Title, string.Empty, when, _service.Zone, p.Repeat, p.Priority, _service.Now);
        if (!_service.Save(reminder))
        {
            return false;
        }

        Say($"Reminder set · {_service.When(reminder.AlertUtc)}");
        ShowTab(ReminderView.Upcoming);
        return true;
    }

    // ===== Editor ============================================================

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _editTitle = string.Empty;

    [ObservableProperty]
    private string _editNotes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateText), nameof(WhenPreview), nameof(WhenIsPast), nameof(CanSave))]
    private DateTime _editDate = DateTime.Today;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenPreview), nameof(WhenIsPast), nameof(TimeIsInvalid), nameof(CanSave))]
    private string _editTimeText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenPreview), nameof(WhenIsPast), nameof(CanSave), nameof(IsCustomRepeat))]
    private RepeatKind _editRepeat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenPreview), nameof(CanSave), nameof(CustomUnitText))]
    private string _customEveryText = "2";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenPreview), nameof(CustomUnitText))]
    private RepeatUnit _customUnit = RepeatUnit.Days;

    [ObservableProperty]
    private ReminderPriority _editPriority;

    public IReadOnlyList<ReminderChip> Presets { get; }

    public IReadOnlyList<ReminderChip> Repeats { get; }

    public IReadOnlyList<ReminderChip> Priorities { get; }

    public bool IsNew => _editing is null;

    public string EditorTitle => IsNew ? "New reminder" : "Edit reminder";

    public bool IsCustomRepeat => EditRepeat == RepeatKind.Custom;

    public string CustomUnitText => Repeat.UnitName(CustomUnit, int.TryParse(CustomEveryText, out var n) ? n : 2);

    /// <summary>"Tue 13 Oct".</summary>
    public string DateText => EditDate.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    public bool TimeIsInvalid => !ReminderParser.TryParseClock(EditTimeText, out _);

    /// <summary>A one-off time that has already gone by.</summary>
    public bool WhenIsPast => _relative is null && EditRepeat == RepeatKind.None && EditedDue() is { } due && due <= _service.Now;

    /// <summary>"Tomorrow 6:00 PM · in 20 h", or why it can't be saved.</summary>
    public string WhenPreview
    {
        get
        {
            if (_relative is { } span)
            {
                return $"{(int)span.TotalMinutes} min after you save";
            }

            if (TimeIsInvalid)
            {
                return "Type a time like 6:30 PM or 18:30";
            }

            if (EditedDue() is not { } due)
            {
                return string.Empty;
            }

            if (WhenIsPast)
            {
                return "That time has passed — pick a later one";
            }

            var repeat = BuildRepeat();
            var first = repeat.IsRepeating && due <= _service.Now
                ? RepeatRule.Next(EditedLocal()!.Value, _service.Zone, repeat, _service.Now)
                : due;
            var text = $"{_service.When(first)} · {ReminderText.Relative(first, _service.Now)}";
            return repeat.IsRepeating ? $"{text} · {repeat.Describe()}" : text;
        }
    }

    public bool CanSave => EditTitle.Trim().Length > 0 && (_relative is not null || (!TimeIsInvalid && !WhenIsPast))
        && (EditRepeat != RepeatKind.Custom || int.TryParse(CustomEveryText, out var n) && n is >= 1 and <= Repeat.MaxEvery);

    partial void OnEditDateChanged(DateTime value) => DateOrTimeEdited();

    partial void OnEditTimeTextChanged(string value) => DateOrTimeEdited();

    partial void OnEditTitleChanged(string value) => SaveEditCommand.NotifyCanExecuteChanged();

    partial void OnCustomEveryTextChanged(string value) => SaveEditCommand.NotifyCanExecuteChanged();

    partial void OnEditRepeatChanged(RepeatKind value)
    {
        foreach (var chip in Repeats)
        {
            chip.IsSelected = chip.Key == value.ToString();
        }

        SaveEditCommand.NotifyCanExecuteChanged();
    }

    partial void OnEditPriorityChanged(ReminderPriority value)
    {
        foreach (var chip in Priorities)
        {
            chip.IsSelected = chip.Key == value.ToString();
        }
    }

    [RelayCommand]
    private void NewReminder() => OpenEditor(null, null);

    [RelayCommand]
    private void Edit(ReminderItem item) => OpenEditor(item.Reminder, null);

    /// <summary>The calendar picked a day.</summary>
    public void PickDate(DateTime date)
    {
        EditDate = date.Date;
        if (TimeIsInvalid)
        {
            EditTimeText = FormatTime(new TimeSpan(9, 0, 0));
        }
    }

    [RelayCommand]
    private void SetUnit(RepeatUnit unit) => CustomUnit = unit;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void SaveEdit()
    {
        if (!CanSave)
        {
            return;
        }

        var now = _service.Now;
        // "In 15 min" counts from now, to the second; a typed time is a whole minute.
        var local = _relative is { } span
            ? TimeZoneInfo.ConvertTime(now + span, _service.Zone).DateTime
            : EditedLocal()!.Value;
        local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, _relative is null ? 0 : local.Second);
        var repeat = BuildRepeat();
        var title = EditTitle.Trim();

        Reminder reminder;
        if (_editing is null)
        {
            reminder = Reminder.Create(title, EditNotes, local, _service.Zone, repeat, EditPriority, now);
        }
        else
        {
            var timeChanged = _relative is not null || repeat != _editing.Repeat
                || EditedDue() != _shownUtc || !_editing.IsPending;
            var details = _editing with
            {
                Title = Reminder.Clean(title),
                Notes = EditNotes.Trim(),
                Priority = EditPriority,
                UpdatedUtc = now,
            };
            reminder = timeChanged ? details.Reschedule(local, _service.Zone, repeat, now) : details;
        }

        if (!_service.Save(reminder))
        {
            return;
        }

        Say(_editing is null ? $"Reminder set · {_service.When(reminder.AlertUtc)}" : "Reminder updated");
        IsEditing = false;
        _editing = null;
        ShowTab(reminder.IsOverdue(now) ? ReminderView.Overdue : ReminderView.Upcoming);
    }

    /// <summary>← Back from the editor: nothing is saved.</summary>
    [RelayCommand]
    public void CloseEditor()
    {
        IsEditing = false;
        _editing = null;
        Reload();
    }

    [RelayCommand]
    private void DeleteEditing()
    {
        if (_editing is { } r)
        {
            IsEditing = false;
            _editing = null;
            Delete(r);
        }
        else
        {
            CloseEditor();
        }
    }

    [RelayCommand]
    private void DuplicateEditing()
    {
        if (_editing is { } r)
        {
            var copy = r.Duplicate(_service.Zone, _service.Now);
            if (_service.Save(copy))
            {
                Say("Duplicated — you're editing the copy");
                OpenEditor(copy, null);
            }
        }
    }

    // ===== Item actions =======================================================

    [RelayCommand]
    private void Complete(ReminderItem item) => Finish(item.Reminder, ReminderStatus.Completed);

    [RelayCommand]
    private void Skip(ReminderItem item) => Finish(item.Reminder, ReminderStatus.Skipped);

    public void Snooze(ReminderItem item, int minutes)
    {
        if (_service.Snooze(item.Reminder.Id, TimeSpan.FromMinutes(minutes)))
        {
            Say($"Snoozed for {DescribeDelay(TimeSpan.FromMinutes(minutes))}");
        }
    }

    [RelayCommand]
    private void Reopen(ReminderItem item)
    {
        if (_service.Reopen(item.Reminder.Id))
        {
            Say("Back on your list");
        }
    }

    [RelayCommand]
    private void Duplicate(ReminderItem item)
    {
        var copy = item.Reminder.Duplicate(_service.Zone, _service.Now);
        if (_service.Save(copy))
        {
            Say("Duplicated");
        }
    }

    [RelayCommand]
    private void DeleteItem(ReminderItem item) => Delete(item.Reminder);

    [RelayCommand]
    private void Undo()
    {
        _undoTimer.Stop();
        CanUndo = false;
        foreach (var id in _undoRemove)
        {
            _service.Delete(id);
        }

        if (_undoRestore.Count > 0 && _service.Save(_undoRestore))
        {
            Say("Undone");
        }

        _undoRestore = [];
        _undoRemove = [];
    }

    // ===== Filters ============================================================

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchOpen = !IsSearchOpen;
        if (!IsSearchOpen)
        {
            Search = string.Empty;
        }
    }

    public void SetPriorityFilter(ReminderPriority? priority) => PriorityFilter = priority;

    public void SetDateFilter(ReminderDateFilter date) => DateFilter = date;

    [RelayCommand]
    private void ClearFilters()
    {
        PriorityFilter = null;
        DateFilter = ReminderDateFilter.Any;
    }

    partial void OnSearchChanged(string value) => Reload();

    partial void OnPriorityFilterChanged(ReminderPriority? value) => Reload();

    partial void OnDateFilterChanged(ReminderDateFilter value) => Reload();

    partial void OnViewChanged(ReminderView value)
    {
        foreach (var tab in Tabs)
        {
            tab.IsSelected = tab.Key == value.ToString();
        }

        Reload();
    }

    public static string DateName(ReminderDateFilter filter) => filter switch
    {
        ReminderDateFilter.Today => "Today",
        ReminderDateFilter.Tomorrow => "Tomorrow",
        ReminderDateFilter.Yesterday => "Yesterday",
        ReminderDateFilter.ThisWeek => "This week",
        _ => "Any day",
    };

    // ===== Page ===============================================================

    /// <summary>The page (or Home with its "Up next" card) is on screen: keep relative times fresh.</summary>
    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Reload();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    /// <summary>Opens on the most useful list (Overdue if anything is due), optionally with one reminder open.</summary>
    public void Show(string? reminderId)
    {
        if (IsEditing && reminderId is null)
        {
            return;
        }

        if (reminderId is not null && _service.Find(reminderId) is { } r)
        {
            OpenEditor(r, null);
            return;
        }

        IsEditing = false;
        ShowTab(ReminderList.Count(_service.All, ReminderView.Overdue, _service.Now) > 0 ? ReminderView.Overdue : View);
    }

    /// <summary>← Back: the editor → the list; search → closed. Returns false when the page itself should close.</summary>
    public bool Back()
    {
        if (IsEditing)
        {
            CloseEditor();
            return true;
        }

        if (IsSearchOpen)
        {
            ToggleSearch();
            return true;
        }

        return false;
    }

    // ===== Internals ============================================================

    private void Reload()
    {
        var now = _service.Now;
        var zone = _service.Zone;

        // Answered somewhere else (the Windows notification, the list): the alert has done its job.
        if (Alert is { } alert && _service.Find(alert.Id)?.IsOverdue(now) != true)
        {
            Alert = null;
        }
        var list = ReminderList.Apply(_service.All, new ReminderFilter(View, Search, PriorityFilter, DateFilter), now, zone);

        Items.Clear();
        foreach (var r in list)
        {
            Items.Add(ToItem(r, now));
        }

        var overdue = ReminderList.Count(_service.All, ReminderView.Overdue, now);
        var upcoming = ReminderList.Count(_service.All, ReminderView.Upcoming, now);
        Tabs[0].Label = upcoming > 0 ? $"Upcoming  {upcoming}" : "Upcoming";
        Tabs[1].Label = overdue > 0 ? $"Overdue  {overdue}" : "Overdue";

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(TileBadge));
        OnPropertyChanged(nameof(HasTileBadge));
        OnPropertyChanged(nameof(TileBadgeIsDue));
        OnPropertyChanged(nameof(UpNextTitle));
        OnPropertyChanged(nameof(UpNextWhen));
        OnPropertyChanged(nameof(HasUpNext));
        OnPropertyChanged(nameof(UpNextIsOverdue));
        if (IsEditing)
        {
            OnPropertyChanged(nameof(WhenPreview));
            OnPropertyChanged(nameof(WhenIsPast));
        }
    }

    private ReminderItem ToItem(Reminder r, DateTimeOffset now)
    {
        var view = ReminderList.ViewOf(r, now);
        var parts = new List<string>();
        string when;
        switch (view)
        {
            case ReminderView.Done:
                when = _service.When(r.DueUtc);
                parts.Add($"{(r.Status == ReminderStatus.Skipped ? "Skipped" : "Completed")} {_service.When(r.DoneUtc ?? r.UpdatedUtc)}");
                break;

            case ReminderView.Overdue:
                when = _service.When(r.AlertUtc);
                parts.Add(ReminderText.Relative(r.AlertUtc, now).Replace(" ago", " overdue", StringComparison.Ordinal));
                break;

            default:
                when = _service.When(r.AlertUtc);
                parts.Add(r.IsSnoozed ? $"Snoozed · {ReminderText.Relative(r.AlertUtc, now)}" : ReminderText.Relative(r.AlertUtc, now));
                break;
        }

        if (r.Repeat.IsRepeating && view != ReminderView.Done)
        {
            parts.Add(r.Repeat.Describe());
        }

        return new ReminderItem(r, view, when, string.Join(" · ", parts));
    }

    private void Finish(Reminder r, ReminderStatus how)
    {
        if (!_service.Finish(r.Id, how, out var history))
        {
            return;
        }

        OfferUndo(how == ReminderStatus.Completed ? "Done ✓" : "Skipped", [r], history is null ? [] : [history.Id]);
        Say(r.Repeat.IsRepeating
            ? $"{(how == ReminderStatus.Completed ? "Done" : "Skipped")} · next {_service.When(_service.Find(r.Id)!.DueUtc)}"
            : how == ReminderStatus.Completed ? "Done ✓ Nice work!" : "Skipped");
    }

    private void Delete(Reminder r)
    {
        if (_service.Delete(r.Id))
        {
            OfferUndo("Reminder deleted", [r], []);
        }
    }

    private void OfferUndo(string text, IReadOnlyList<Reminder> restore, IReadOnlyList<string> remove)
    {
        _undoRestore = restore;
        _undoRemove = remove;
        UndoText = text;
        CanUndo = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void OpenEditor(Reminder? reminder, ParsedReminder? parsed)
    {
        _loadingEditor = true;
        _editing = reminder;
        _relative = null;
        var now = _service.Now;
        if (reminder is not null)
        {
            var local = TimeZoneInfo.ConvertTime(reminder.IsPending ? reminder.AlertUtc : reminder.DueUtc, _service.Zone).DateTime;
            EditTitle = reminder.Title;
            EditNotes = reminder.Notes;
            EditDate = local.Date;
            EditTimeText = FormatTime(local.TimeOfDay);
            EditPriority = reminder.Priority;
            EditRepeat = reminder.Repeat.Kind;
            CustomEveryText = reminder.Repeat.Kind == RepeatKind.Custom ? reminder.Repeat.Every.ToString(CultureInfo.CurrentCulture) : "2";
            CustomUnit = reminder.Repeat.Kind == RepeatKind.Custom ? reminder.Repeat.Unit : RepeatUnit.Days;
            SelectPresetKey("custom");
            _shownUtc = EditedDue();
        }
        else
        {
            _shownUtc = null;
            // New: an hour from now on the next half hour, unless the typed text said otherwise.
            var local = TimeZoneInfo.ConvertTime(now, _service.Zone).DateTime.AddHours(1);
            local = local.Date.AddHours(local.Hour).AddMinutes(local.Minute < 30 ? 30 : 60);
            EditTitle = parsed?.Title is { } title && title != "Reminder" ? title : string.Empty;
            EditNotes = string.Empty;
            EditDate = local.Date;
            EditTimeText = FormatTime(local.TimeOfDay);
            EditPriority = parsed?.Priority ?? ReminderPriority.Normal;
            EditRepeat = parsed?.Repeat.Kind ?? RepeatKind.None;
            CustomEveryText = parsed?.Repeat.Kind == RepeatKind.Custom ? parsed.Repeat.Every.ToString(CultureInfo.CurrentCulture) : "2";
            CustomUnit = parsed?.Repeat.Kind == RepeatKind.Custom ? parsed.Repeat.Unit : RepeatUnit.Days;
            SelectPresetKey("custom");
        }

        OnEditRepeatChanged(EditRepeat);
        OnEditPriorityChanged(EditPriority);
        _loadingEditor = false;
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(EditorTitle));
        OnPropertyChanged(nameof(WhenPreview));
        OnPropertyChanged(nameof(CanSave));
        SaveEditCommand.NotifyCanExecuteChanged();
        IsEditing = true;
        EditorOpened?.Invoke(reminder is null);
    }

    private void SelectTab(ReminderChip chip) => View = Enum.Parse<ReminderView>(chip.Key);

    private void ShowTab(ReminderView view)
    {
        if (View == view)
        {
            Reload();
        }
        else
        {
            View = view;
        }
    }

    private void SelectPreset(ReminderChip chip)
    {
        var local = TimeZoneInfo.ConvertTime(_service.Now, _service.Zone).DateTime;
        _loadingEditor = true;
        switch (chip.Key)
        {
            case "tomorrow":
                _relative = null;
                EditDate = local.Date.AddDays(1);
                EditTimeText = FormatTime(new TimeSpan(9, 0, 0));
                break;

            case "custom":
                _relative = null;
                break;

            default:
                var minutes = int.Parse(chip.Key, CultureInfo.InvariantCulture);
                _relative = TimeSpan.FromMinutes(minutes);
                var at = local.AddMinutes(minutes);
                EditDate = at.Date;
                EditTimeText = FormatTime(new TimeSpan(at.Hour, at.Minute, 0));
                break;
        }

        _loadingEditor = false;
        SelectPresetKey(chip.Key);
        OnPropertyChanged(nameof(WhenPreview));
        OnPropertyChanged(nameof(WhenIsPast));
        OnPropertyChanged(nameof(CanSave));
        SaveEditCommand.NotifyCanExecuteChanged();
    }

    private void SelectPresetKey(string key)
    {
        foreach (var chip in Presets)
        {
            chip.IsSelected = chip.Key == key;
        }
    }

    /// <summary>The user typed a time or picked a day: the time is now exact ("Custom").</summary>
    private void DateOrTimeEdited()
    {
        if (_loadingEditor)
        {
            return;
        }

        _relative = null;
        SelectPresetKey("custom");
        SaveEditCommand.NotifyCanExecuteChanged();
    }

    private void SelectRepeat(ReminderChip chip) => EditRepeat = Enum.Parse<RepeatKind>(chip.Key);

    private void SelectPriority(ReminderChip chip) => EditPriority = Enum.Parse<ReminderPriority>(chip.Key);

    private Repeat BuildRepeat() => EditRepeat switch
    {
        RepeatKind.Custom => Repeat.Custom(int.TryParse(CustomEveryText, out var n) ? n : 1, CustomUnit),
        var kind => new Repeat(kind),
    };

    private DateTime? EditedLocal() =>
        ReminderParser.TryParseClock(EditTimeText, out var time) ? EditDate.Date + time : null;

    private DateTimeOffset? EditedDue() => EditedLocal() is { } local ? RepeatRule.ToUtc(local, _service.Zone) : null;

    private ParsedReminder Parse(string text) =>
        ReminderParser.Parse(text, TimeZoneInfo.ConvertTime(_service.Now, _service.Zone).DateTime);

    private static string FormatTime(TimeSpan time) => DateTime.Today.Add(time).ToString("t", CultureInfo.CurrentCulture);

    private static ReminderChip Chip(string key, string label, Action<ReminderChip> select) => new(key, label, select);

    private void Say(string text) => StatusChanged?.Invoke(text);
}
