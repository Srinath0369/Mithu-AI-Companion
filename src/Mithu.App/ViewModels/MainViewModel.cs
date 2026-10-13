using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mithu.App.Animation;
using Mithu.Core.Layout;
using Mithu.Core.Models;

namespace Mithu.App.ViewModels;

/// <summary>Pages of the expanded panel.</summary>
public enum PanelPage
{
    Home,
    Focus,
    Notes,
    Reminders,
    AiSettings,
    Ask,
    DesktopControl,
    Todos,

    /// <summary>The first-run setup (welcome, name, AI, permissions).</summary>
    Setup,
}

/// <summary>
/// State of the floating companion: which layout is shown, greeting/status text and the draft input.
/// The draft survives every mode change so Esc never discards what the user typed.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly FaceAnimationCoordinator _face;
    private readonly ClickReactionDriver _clickReactions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MainViewModel> _logger;
    private WindowMode _modeBeforePeek = WindowMode.Hidden;

    public MainViewModel(
        FaceAnimationCoordinator face,
        ClickReactionDriver clickReactions,
        FocusViewModel focus,
        NotesViewModel notes,
        RemindersViewModel reminders,
        AiSettingsViewModel aiSettings,
        AskViewModel ask,
        TimeProvider timeProvider,
        ILogger<MainViewModel> logger,
        DesktopControlViewModel? desktopControl = null,
        TodosViewModel? todos = null,
        ClaudeCodeViewModel? claudeCode = null,
        SetupViewModel? setup = null)
    {
        Setup = setup;
        if (setup is not null)
        {
            setup.AiRequested += () =>
            {
                _aiFromSetup = true;
                OpenAiSettings();
            };
            setup.Closed += finished =>
            {
                Page = PanelPage.Home;
                if (finished)
                {
                    StatusText = $"Hi, I'm {Companion.Name}! Click me any time.";
                    face.React(FaceMood.Celebrating, TimeSpan.FromSeconds(2));
                }
            };
        }

        ClaudeCode = claudeCode;
        Todos = todos;
        if (todos is not null)
        {
            todos.StatusChanged += Say;
        }

        DesktopControl = desktopControl;
        if (desktopControl is not null)
        {
            desktopControl.StatusChanged += Say;
        }

        Ask = ask;
        Ask.StatusChanged += Say;

        // When the AI can't be reached, the chat can still do the job the old way.
        Ask.ReminderWithoutAi = text =>
        {
            var ok = reminders.QuickCreate(text);
            if (ok && reminders.IsEditing)
            {
                Page = PanelPage.Reminders; // no time in the text: the editor opens to pick one
            }

            return ok;
        };
        Ask.NoteWithoutAi = notes.QuickSaveAsync;
        _face = face;
        _clickReactions = clickReactions;
        _timeProvider = timeProvider;
        _logger = logger;
        _greeting = HeaderLines.Greeting(timeProvider.GetLocalNow());
        Focus = focus;
        Notes = notes;
        Reminders = reminders;
        AiSettings = aiSettings;
        AiSettings.StatusChanged += Say;

        // AI turned on/off in Settings: the Home input's hint and the chat button follow.
        AiSettings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiSettingsViewModel.IsOn))
            {
                OnPropertyChanged(nameof(IsAiReady));
            }
        };
        _clickReactions.StatusChanged += Say;
        Notes.StatusChanged += Say;
        Reminders.StatusChanged += Say;

        // The alert was answered (Snooze, Done, or elsewhere): the peek that carried it slides back.
        Reminders.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemindersViewModel.HasAlert) && !Reminders.HasAlert && Mode == WindowMode.Peek)
            {
                Mode = _modeBeforePeek;
            }
        };
        Focus.StatusChanged += text => StatusText = text;
        Ask.Attached += OnScreenshotAttached;
        Focus.StateChanged += OnFocusStateChanged;
        if (Focus.StartupMessage is { } message)
        {
            _statusText = message;
        }
    }

    /// <summary>The focus timer (panel card, header countdown, notch progress).</summary>
    public FocusViewModel Focus { get; }

    /// <summary>Notes (Notes page and Quick Note from the Home input).</summary>
    public NotesViewModel Notes { get; }

    /// <summary>Reminders (Reminders page, Home tile and "Up next", "remind me…" from the Home input).</summary>
    public RemindersViewModel Reminders { get; }

    /// <summary>Which page the expanded panel shows.</summary>
    [ObservableProperty]
    private PanelPage _page;

    partial void OnPageChanged(PanelPage value) => UpdateRemindersVisible();

    [RelayCommand]
    private void OpenFocus() => Page = PanelPage.Focus;

    [RelayCommand]
    private void OpenReminders() => ShowReminders(null);

    /// <summary>AI settings (provider, key, model…): the AI page of the panel.</summary>
    public AiSettingsViewModel AiSettings { get; }

    /// <summary>The AI chat (Ask page; the Home input goes here once AI is set up).</summary>
    public AskViewModel Ask { get; }

    /// <summary>AI is on: Home's input talks to Mithu's AI.</summary>
    public bool IsAiReady => Ask.IsReady;

    /// <summary>Opens the chat (Home's chat button).</summary>
    [RelayCommand]
    private void OpenAsk()
    {
        Mode = WindowMode.Expanded;
        Ask.Open();
        Page = PanelPage.Ask;
    }

    /// <summary>
    /// Home's screenshot button: pick part of the screen, then ask about it in the chat. Anything typed on Home
    /// moves to the chat's message box as the question.
    /// </summary>
    [RelayCommand]
    private void ScreenshotAndAsk()
    {
        _screenshotFromHome = true;
        Ask.CaptureCommand.Execute(null);
    }

    private bool _screenshotFromHome;

    /// <summary>The screenshot is in the chat: Home's text goes with it (a cancelled capture leaves it on Home).</summary>
    private void OnScreenshotAttached()
    {
        if (_screenshotFromHome && Draft.Trim().Length > 0)
        {
            Ask.Draft = Draft.Trim();
            Draft = string.Empty;
        }

        _screenshotFromHome = false;
    }

    /// <summary>A peek's own title ("Claude Code finished") instead of the greeting; cleared when the peek goes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPeekTitle))]
    private string? _peekTitle;

    public bool ShowPeekTitle => Mode == WindowMode.Peek && !string.IsNullOrEmpty(PeekTitle);

    /// <summary>The peek only tells something (e.g. Claude Code finished): no Focus answer button.</summary>
    [ObservableProperty]
    private bool _peekIsMessage;

    /// <summary>What Claude Code is doing (Home card and the header line).</summary>
    public ClaudeCodeViewModel? ClaudeCode { get; }

    /// <summary>To-do lists (To-do page and Home's tile).</summary>
    public TodosViewModel? Todos { get; }

    /// <summary>Opens the To-do page (Home's tile).</summary>
    [RelayCommand]
    private void OpenTodos()
    {
        Mode = WindowMode.Expanded;
        Todos?.Refresh();
        Page = PanelPage.Todos;
    }

    /// <summary>The first-run setup (also ⋯ → Run setup again).</summary>
    public SetupViewModel? Setup { get; }

    private bool _aiFromSetup;

    /// <summary>Opens the full panel on the setup, from its first step.</summary>
    public void StartSetup()
    {
        if (Setup is null)
        {
            return;
        }

        Mode = WindowMode.Expanded;
        Setup.Begin();
        Page = PanelPage.Setup;
    }

    /// <summary>Desktop control settings (apps, files, volume; allowed folders).</summary>
    public DesktopControlViewModel? DesktopControl { get; }

    private bool _desktopFromAiSettings;

    /// <summary>Opens Desktop control settings (from AI settings or ⋯ → Desktop control…).</summary>
    [RelayCommand]
    public void OpenDesktopControl()
    {
        _desktopFromAiSettings = Page == PanelPage.AiSettings;
        Mode = WindowMode.Expanded;
        DesktopControl?.Load();
        Page = PanelPage.DesktopControl;
    }

    /// <summary>Opens the full panel on AI settings (⋯ menu → AI settings…).</summary>
    [RelayCommand]
    public void OpenAiSettings()
    {
        Mode = WindowMode.Expanded;
        AiSettings.Load();
        Page = PanelPage.AiSettings;
    }

    /// <summary>
    /// A reminder went off while Mithu is on screen: its header shows it with Snooze / Done (the peek slides out
    /// when Mithu is small; the open panel shows it in place).
    /// </summary>
    public void ShowReminderAlert(Core.Reminders.Reminder reminder)
    {
        Reminders.ShowAlert(reminder);
        ShowPeek();
    }

    /// <summary>Clicking the alert's text: open that reminder.</summary>
    [RelayCommand]
    private void OpenAlert()
    {
        var id = Reminders.Alert?.Id;
        Reminders.ClearAlert();
        ShowReminders(id);
    }

    /// <summary>Opens the full panel on Reminders (with one reminder open, e.g. from a notification).</summary>
    public void ShowReminders(string? reminderId)
    {
        Mode = WindowMode.Expanded;
        Reminders.Show(reminderId);
        Page = PanelPage.Reminders;
    }

    [RelayCommand]
    private void OpenNotes()
    {
        // From Home, Notes opens on the active list (an open note is kept).
        if (!Notes.IsEditing && Notes.ShowArchived)
        {
            Notes.ShowArchived = false;
        }

        Notes.Refresh();
        Page = PanelPage.Notes;
    }

    /// <summary>← Back (or Esc) on a page: an open note → its list; the archive → Notes; otherwise → Home.</summary>
    [RelayCommand]
    public void GoBack()
    {
        if (Page == PanelPage.Notes && Notes.IsEditing)
        {
            Notes.CloseEditorCommand.Execute(null);
            return;
        }

        if (Page == PanelPage.Notes && Notes.ShowArchived)
        {
            Notes.ShowArchived = false;
            return;
        }

        if (Page == PanelPage.Reminders && Reminders.Back())
        {
            return;
        }

        if (Page == PanelPage.Ask && Ask.Back())
        {
            return;
        }

        if (Page == PanelPage.DesktopControl && _desktopFromAiSettings)
        {
            Page = PanelPage.AiSettings;
            return;
        }

        // AI settings opened from the setup: back to the setup (showing what was set up).
        if (Page == PanelPage.AiSettings && _aiFromSetup)
        {
            _aiFromSetup = false;
            Setup?.Refresh();
            Page = PanelPage.Setup;
            return;
        }

        // In the setup, Back is the previous step (never out to Home: Skip is for that).
        if (Page == PanelPage.Setup)
        {
            Setup?.Back();
            return;
        }

        Page = PanelPage.Home;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHidden), nameof(IsExpanded), nameof(ShowHeaderPause))]
    [NotifyCanExecuteChangedFor(nameof(SmallerCommand), nameof(BiggerCommand))]
    private WindowMode _mode = WindowMode.Compact;

    /// <summary>Last open size the user picked (hover on the notch returns here).</summary>
    [ObservableProperty]
    private WindowMode _lastVisibleMode = WindowMode.Expanded;

    [ObservableProperty]
    private string _greeting;

    [ObservableProperty]
    private string _statusText = "Ready when you are";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDraft))]
    [NotifyCanExecuteChangedFor(nameof(SubmitDraftCommand))]
    private string _draft = string.Empty;

    /// <summary>Shortcut text shown in hints ("Ctrl+Shift+Space").</summary>
    [ObservableProperty]
    private string _hotkeyText = "Ctrl+Shift+Space";

    public bool IsHidden => Mode == WindowMode.Hidden;

    /// <summary>Pinned open (Claude Code is working): clicking elsewhere closes to compact, not to the notch.</summary>
    [ObservableProperty]
    private bool _stayCompact;

    /// <summary>Where clicking elsewhere tucks Mithu: the notch, or compact while pinned.</summary>
    public WindowMode TuckedMode => StayCompact ? WindowMode.Compact : WindowMode.Hidden;

    public bool IsExpanded => Mode == WindowMode.Expanded;

    /// <summary>Pause/resume in the header: only in Compact (the expanded panel's card has its own).</summary>
    public bool ShowHeaderPause => Mode == WindowMode.Compact && Focus.IsCounting;

    public bool HasDraft => !string.IsNullOrWhiteSpace(Draft);

    partial void OnModeChanged(WindowMode value)
    {
        if (value != WindowMode.Peek)
        {
            PeekTitle = null;
        }

        OnPropertyChanged(nameof(ShowPeekTitle));
        if (ModeNavigator.IsVisibleSize(value))
        {
            LastVisibleMode = value;
        }

        // Opening the panel during a session goes straight to the Focus page. Otherwise Notes stays where it was
        // (so an open note is still there), and the Focus page falls back to Home.
        if (value == WindowMode.Expanded)
        {
            if (Focus.IsActive)
            {
                Page = PanelPage.Focus;
            }
            else if (Page == PanelPage.Focus)
            {
                Page = PanelPage.Home;
            }
        }

        // Tucked away: forget any smile/anger so Mithu comes back calm.
        if (value == WindowMode.Hidden)
        {
            _clickReactions.Reset();
            Reminders.ClearAlert();
        }

        UpdateRemindersVisible();
    }

    /// <summary>Reminder times ("in 5 min") are kept fresh only while Home or the Reminders page is on screen.</summary>
    private void UpdateRemindersVisible() =>
        Reminders.SetVisible(Mode == WindowMode.Expanded && Page is PanelPage.Home or PanelPage.Reminders);

    /// <summary>▼ arrow / Ctrl+Down / scroll down.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBigger))]
    private void Bigger() => Mode = ModeNavigator.Bigger(Mode);

    private bool CanGoBigger() => Mode != WindowMode.Expanded;

    /// <summary>▲ arrow / Esc / Ctrl+Up / scroll up.</summary>
    [RelayCommand(CanExecute = nameof(CanGoSmaller))]
    private void Smaller() => Mode = ModeNavigator.Smaller(Mode);

    private bool CanGoSmaller() => Mode != WindowMode.Hidden;

    /// <summary>Clicking the hidden tab (notch or face bubble) always opens the full panel.</summary>
    public void Reveal() => Mode = WindowMode.Expanded;

    /// <summary>
    /// Slides out a short message (e.g. "Focus done · Take 5?") without taking keyboard focus.
    /// Not needed when the full panel is already open: its card shows the same thing.
    /// </summary>
    public void ShowPeek()
    {
        if (Mode is WindowMode.Expanded or WindowMode.Peek)
        {
            return;
        }

        _modeBeforePeek = Mode;
        Mode = WindowMode.Peek;
    }

    /// <summary>Closes the peek back to the size it came from ("Later", timeout, or its action was taken).</summary>
    [RelayCommand]
    public void DismissPeek()
    {
        Reminders.ClearAlert();
        if (Mode == WindowMode.Peek)
        {
            Mode = _modeBeforePeek;
        }
    }

    /// <summary>Once the user answered the peek (started a break, skipped…), it has done its job.</summary>
    private void OnFocusStateChanged()
    {
        OnPropertyChanged(nameof(ShowHeaderPause));
        if (Mode == WindowMode.Peek && Focus.State != Core.Focus.FocusState.Finished && !Reminders.HasAlert)
        {
            DismissPeek();
        }
    }

    /// <summary>
    /// Face click. Hidden: opens the full panel (the only click that resizes).
    /// Open: single click smiles; continuous clicking punches; when the punching stops Mithu gets angry
    /// for a few seconds, then calms down. Never changes size (use the arrows for that).
    /// </summary>
    [RelayCommand]
    private void FaceClicked()
    {
        if (Mode == WindowMode.Hidden)
        {
            Reveal();
            return;
        }

        // Focusing: no punching or anger, just a gentle smile and a word of encouragement.
        if (Focus.IsFocusing)
        {
            Focus.Encourage();
            return;
        }

        _clickReactions.Click();
    }

    /// <summary>
    /// Quick Note: Enter in the Home input saves the text as a note (#hashtags become tags). The box is cleared only
    /// once the note is safely saved; if saving fails the text stays.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasDraft))]
    private async Task SubmitDraftAsync()
    {
        if (!HasDraft)
        {
            return;
        }

        var text = Draft;

        // AI is on: everything typed here goes to Mithu, who can now set reminders and save notes itself.
        if (IsAiReady)
        {
            Draft = string.Empty;
            Mode = WindowMode.Expanded;
            Ask.Open();
            Page = PanelPage.Ask;
            await Ask.AskAsync(text);
            return;
        }

        // "Remind me to call Mom tomorrow at 6 PM" becomes a reminder; without a time the editor opens to pick one.
        if (Core.Reminders.ReminderParser.LooksLikeReminder(text))
        {
            if (Reminders.QuickCreate(text))
            {
                Draft = string.Empty;
                if (Reminders.IsEditing)
                {
                    Page = PanelPage.Reminders;
                }

                _face.React(FaceMood.Happy);
            }
            else
            {
                _face.React(FaceMood.Concerned, TimeSpan.FromSeconds(2));
            }

            return;
        }

        if (await Notes.QuickSaveAsync(text))
        {
            if (Draft == text)
            {
                Draft = string.Empty;
            }

            _face.React(FaceMood.Happy);
        }
        else
        {
            _face.React(FaceMood.Concerned, TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Status line; during a focus round or break it also shows under the timer for a moment.</summary>
    private void Say(string text)
    {
        StatusText = text;
        if (Focus.IsCounting)
        {
            Focus.ShowWhisper(text);
        }
    }

    /// <summary>Feature tiles that arrive in Phase 2.</summary>
    [RelayCommand]
    private void ComingSoon(string feature)
    {
        _face.React(FaceMood.Happy, TimeSpan.FromMilliseconds(900));
        StatusText = $"{feature} is coming in the next update";
    }

    private readonly HeaderLines _header = new();
    private DateTimeOffset? _tuckedAt;

    /// <summary>
    /// The header's title for this opening (<see cref="HeaderLines"/>): a greeting once per part of the day, then the
    /// next reminder or open to-dos, else a friendly line. Called when the companion comes out of hiding (not while
    /// it's on screen, so the line never changes under the user's eyes).
    /// </summary>
    public void RefreshGreeting()
    {
        var now = _timeProvider.GetLocalNow();
        var next = Reminders.NextForHeader();
        Greeting = _header.Next(now, new HeaderContext(
            next?.Title,
            next?.In,
            next?.Overdue ?? false,
            next?.Ahead,
            Todos?.OpenCount ?? 0,
            _tuckedAt is { } tucked ? now - tucked : null));
    }

    partial void OnModeChanged(WindowMode oldValue, WindowMode newValue)
    {
        if (newValue == WindowMode.Hidden)
        {
            _tuckedAt = _timeProvider.GetLocalNow();
        }
        else if (oldValue == WindowMode.Hidden && newValue is WindowMode.Compact or WindowMode.Expanded)
        {
            RefreshGreeting();
        }
    }
}
