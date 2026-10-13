using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Mithu.App.Animation;
using Mithu.App.ViewModels;
using Mithu.App.Views;
using Mithu.Core.Focus;
using Mithu.Core.Interfaces;
using Mithu.Core.Models;
using Mithu.Core.Mood;
using Mithu.Infrastructure.Windows;

namespace Mithu.App.Services;

/// <summary>
/// Orchestrates the companion shell: window, tray, hotkey, settings, quiet/fullscreen mode
/// and reactions to display, session and power changes.
/// </summary>
public sealed class ShellController : IShellActions, IDisposable
{
    private readonly FloatingWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly FaceAnimationCoordinator _face;
    private readonly TrayService _tray;
    private readonly GlobalHotkeyService _hotkey;
    private readonly ScreenshotHotkeyService _screenshotHotkey;
    private readonly ISettingsStore _settings;
    private readonly IStartupRegistration _startup;
    private readonly WindowPlacementService _placement;
    private readonly FullscreenWatcher _fullscreen;
    private readonly ReminderService _reminders;
    private readonly ClaudeCodeService _claude;
    private readonly UpdateService _updates;
    private readonly ILogger<ShellController> _logger;
    private readonly Dispatcher _dispatcher;

    private bool _hiddenForFullscreen;
    private bool _capturing;
    private bool _screenshotHotkeyWarned;

    public ShellController(
        FloatingWindow window,
        MainViewModel viewModel,
        FaceAnimationCoordinator face,
        TrayService tray,
        GlobalHotkeyService hotkey,
        ScreenshotHotkeyService screenshotHotkey,
        ISettingsStore settings,
        IStartupRegistration startup,
        WindowPlacementService placement,
        FullscreenWatcher fullscreen,
        ReminderService reminders,
        ClaudeCodeService claude,
        UpdateService updates,
        ILogger<ShellController> logger)
    {
        _window = window;
        _viewModel = viewModel;
        _face = face;
        _tray = tray;
        _hotkey = hotkey;
        _screenshotHotkey = screenshotHotkey;
        _settings = settings;
        _startup = startup;
        _placement = placement;
        _fullscreen = fullscreen;
        _reminders = reminders;
        _claude = claude;
        _updates = updates;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsCompanionVisible => _window.IsVisible;

    public string HotkeyText => _hotkey.Current?.ToString() ?? "no shortcut";

    public void Start(bool launchedAtStartup, bool launchedFromNotification = false)
    {
        var s = _settings.Current;

        // Restore size: quiet mode starts hidden; otherwise the last size.
        _viewModel.LastVisibleMode = s.LastVisibleMode;
        _viewModel.Mode = s.QuietMode ? WindowMode.Hidden : s.LastMode;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Mode) && _viewModel.Mode != WindowMode.Peek)
            {
                _logger.LogInformation("Size changed to {Mode}", _viewModel.Mode);
                _settings.Update(x => x with { LastMode = _viewModel.Mode, LastVisibleMode = _viewModel.LastVisibleMode });
            }
        };

        ApplyVisualSettings(s);
        _window.OptionsMenuFactory = () => OptionsMenuBuilder.Build(this, includeShowHide: false);

        // Hotkey (fallback: keep working without it; tray still shows the companion).
        var hotkeyOk = _hotkey.Register(s.Hotkey);
        _hotkey.Pressed += (_, _) => OnHotkey();
        _viewModel.HotkeyText = HotkeyText;

        // Screenshot shortcut (Ctrl+Shift+S) and the chat's 👀 button.
        _screenshotHotkey.Pressed += (_, _) => _ = CaptureScreenAsync();
        _viewModel.Ask.CaptureRequested += () => _ = CaptureScreenAsync();
        _viewModel.Ask.LookAtScreen = async () => await Vision.ScreenCapture.SnapScreenBehindAsync(_window); // "look at my screen"
        SyncScreenshotHotkey();

        _tray.Create(this);

        if (!hotkeyOk)
        {
            _viewModel.StatusText = $"Shortcut {s.Hotkey} is used by another app";
            _face.React(FaceMood.Concerned, TimeSpan.FromSeconds(2));
        }

        // Fullscreen awareness (event-driven).
        _fullscreen.Changed += (_, isFullscreen) => _dispatcher.BeginInvoke(() => OnFullscreenChanged(isFullscreen));
        _fullscreen.Start();

        // System events (raised on a background thread → marshal to UI).
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SystemEvents.TimeChanged += OnTimeChanged;

        // The companion's name (chosen in the setup) shows everywhere at once.
        Companion.Name = s.CompanionName;
        _settings.Changed += (_, _) => _dispatcher.BeginInvoke(() =>
        {
            Companion.Name = _settings.Current.CompanionName;
            _tray.Refresh();
            SyncScreenshotHotkey();
        });
        _viewModel.Focus.SessionFinished += OnFocusSessionFinished;

        _reminders.AlertsInApp = CanAlertInApp;
        _reminders.Fired += OnReminderFired;
        _reminders.Missed += OnRemindersMissed;
        _reminders.OpenRequested += OnReminderOpenRequested;
        _reminders.StatusChanged += text => _viewModel.StatusText = text;

        // Claude Code status: listen again if it was on; a chime and a peek when it finishes or needs the user.
        if (_viewModel.ClaudeCode is { } claudeStatus)
        {
            claudeStatus.Finished += session => OnClaudeCode(session, needsYou: false);
            claudeStatus.NeedsYou += session => OnClaudeCode(session, needsYou: true);
            claudeStatus.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ClaudeCodeViewModel.BusyState))
                {
                    UpdateClaudeFace(claudeStatus.BusyState);
                }
                else if (e.PropertyName == nameof(ClaudeCodeViewModel.IsBusy))
                {
                    UpdateClaudePin();
                }
            };
        }

        _claude.Start();

        // A reminder alert waits for an answer: Mithu listens until it's answered.
        _viewModel.Reminders.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RemindersViewModel.HasAlert))
            {
                _face.SetWaiting(ReminderWait, _viewModel.Reminders.HasAlert);
            }
        };

        StartSleepWatch();

        // An installed Mithu with an update address looks for new versions (⋯ → Updates).
        _updates.Ready += version => _dispatcher.BeginInvoke(() =>
        {
            _viewModel.StatusText = $"Mithu {version} is ready: it installs when Mithu restarts.";
            _tray.Refresh();
        });
        _updates.Start();

        _window.Show();
        if (!_viewModel.IsHidden)
        {
            _viewModel.RefreshGreeting(); // opened at start: this is the greeting for this part of the day
        }

        // A brand-new install: the first-run setup opens (welcome, name, AI, permissions).
        if (!s.SetupDone && !launchedFromNotification)
        {
            _viewModel.StartSetup();
        }

        _reminders.Start(launchedFromNotification);
        if (_fullscreen.IsFullscreenActive)
        {
            OnFullscreenChanged(true);
        }

        _logger.LogInformation(
            "Shell started (mode {Mode}, docked {Docked}, startup launch: {Startup})",
            _viewModel.Mode, s.DockToTop, launchedAtStartup);
    }

    // ===== IShellActions (⚙ menu and tray) =================================

    public OptionsState GetOptions()
    {
        var s = _settings.Current;
        return new OptionsState(s.DockToTop, s.CloseOnClickAway, s.AlwaysOnTop, s.QuietMode, s.ReducedMotion, SafeIsLaunchAtLogin(), s.PanelScale, s.ClaudeCode.Enabled, s.ClaudeCode.Notify, s.ClaudeCode.StayOpen, s.CheckForUpdates, s.ShowBorders);
    }

    public void ToggleVisibility()
    {
        if (_window.IsVisible)
        {
            _window.Hide();
        }
        else
        {
            ShowCompanion();
        }
    }

    public void ShowCompanion()
    {
        _hiddenForFullscreen = false;
        if (!_viewModel.IsHidden && !_window.IsVisible)
        {
            _viewModel.RefreshGreeting(); // coming back without a size change (opening from the notch does it itself)
        }

        _window.Show();
        if (_viewModel.IsHidden)
        {
            _viewModel.Reveal();
        }
        else
        {
            _window.RefreshPlacement();
        }
    }

    public void SetDockToTop(bool enabled)
    {
        _settings.Update(s => s with { DockToTop = enabled });
        ApplyVisualSettings(_settings.Current);
        _viewModel.StatusText = enabled ? "Stuck to the top edge" : "Floating freely — drag me anywhere";
    }

    public void SetCloseOnClickAway(bool enabled)
    {
        _settings.Update(s => s with { CloseOnClickAway = enabled });
        ApplyVisualSettings(_settings.Current);
    }

    public void SetAlwaysOnTop(bool enabled)
    {
        _settings.Update(s => s with { AlwaysOnTop = enabled });
        ApplyVisualSettings(_settings.Current);
    }

    public void SetQuietMode(bool enabled)
    {
        _settings.Update(s => s with { QuietMode = enabled });
        if (enabled)
        {
            _viewModel.Mode = WindowMode.Hidden;
        }
    }

    public void SetClaudeCode(bool enabled)
    {
        if (enabled)
        {
            var ok = _claude.TryEnable(out var message);
            _viewModel.StatusText = message;
            if (!ok)
            {
                _face.React(FaceMood.Concerned, TimeSpan.FromSeconds(2));
            }
        }
        else
        {
            _viewModel.StatusText = _claude.Disable();
        }

        _tray.Refresh();
    }

    public void SetClaudeNotify(bool enabled)
    {
        _settings.Update(s => s with { ClaudeCode = s.ClaudeCode with { Notify = enabled } });
        _viewModel.StatusText = enabled ? "I'll tell you when Claude Code finishes or needs you." : "No Claude Code alerts.";
    }

    public void SetClaudeStayOpen(bool enabled)
    {
        _settings.Update(s => s with { ClaudeCode = s.ClaudeCode with { StayOpen = enabled } });
        _viewModel.StatusText = enabled ? "I'll stay open while Claude Code works." : "I'll tuck away as usual.";
        UpdateClaudePin();
    }

    /// <summary>Mithu came out of the notch by itself for Claude Code (so it goes back when the work is done).</summary>
    private bool _claudePoppedOut;

    /// <summary>
    /// "Stay open while it works": when Claude Code starts, a tucked-away Mithu opens to compact and clicking
    /// elsewhere won't tuck it away; when it's done, Mithu goes back where it was (unless the user opened it more).
    /// Not in quiet mode or fullscreen. ▲ still tucks it away by hand.
    /// </summary>
    private void UpdateClaudePin()
    {
        var pin = _settings.Current.ClaudeCode is { Enabled: true, StayOpen: true } && _viewModel.ClaudeCode?.IsBusy == true;
        _viewModel.StayCompact = pin;
        if (pin)
        {
            if (_viewModel.IsHidden && CanAlertInApp())
            {
                _viewModel.Mode = WindowMode.Compact;
                _claudePoppedOut = true;
            }
        }
        else if (_claudePoppedOut)
        {
            _claudePoppedOut = false;
            if (_viewModel.Mode == WindowMode.Compact)
            {
                _viewModel.Mode = WindowMode.Hidden;
            }
        }
    }

    /// <summary>The face mood Mithu put on for Claude Code (so it only ever takes back its own).</summary>
    private FaceMood? _claudeMood;

    /// <summary>
    /// Mithu looks focused while Claude Code works and attentive while it waits for the user; only when Mithu isn't
    /// showing a mood of its own (focus timer, chat), and back to normal afterwards.
    /// </summary>
    private void UpdateClaudeFace(Core.ClaudeCode.ClaudeState? state)
    {
        // Waiting for the user's OK: all ears (whatever else Mithu is doing).
        _face.SetWaiting(ClaudeWait, state == Core.ClaudeCode.ClaudeState.NeedsYou);
        if (_viewModel.Ask.IsBusy)
        {
            return; // Mithu's own chat owns the face while it answers
        }

        FaceMood? want = state switch
        {
            Core.ClaudeCode.ClaudeState.Thinking => FaceMood.Thinking,
            Core.ClaudeCode.ClaudeState.Working => FaceMood.Focused,
            _ => null,
        };

        if (want is { } mood)
        {
            if (_face.BaseMood == FaceMood.Idle || _face.BaseMood == _claudeMood)
            {
                _face.SetBaseMood(mood);
                _claudeMood = mood;
            }
        }
        else if (_claudeMood is { } mine)
        {
            if (_face.BaseMood == mine)
            {
                _face.SetBaseMood(FaceMood.Idle);
            }

            _claudeMood = null;
        }
    }

    /// <summary>Claude Code finished, or is waiting for permission: a chime and a short peek (not in quiet mode or fullscreen).</summary>
    private void OnClaudeCode(Core.ClaudeCode.ClaudeSession session, bool needsYou)
    {
        if (!_settings.Current.ClaudeCode.Notify || !CanAlertInApp())
        {
            return;
        }

        SoftChime.Play();
        _viewModel.PeekIsMessage = true;
        _viewModel.ShowPeek();
        if (_viewModel.Mode == WindowMode.Peek)
        {
            _viewModel.PeekTitle = needsYou ? "Claude Code needs you" : "Claude Code finished";
        }

        // Under the title: "4 min · 12 edits, 3 commands" / "Running the tests"
        _viewModel.StatusText = session.Activity
            .Replace("Finished · ", string.Empty, StringComparison.Ordinal)
            .Replace("Needs your OK: ", string.Empty, StringComparison.Ordinal);
        if (!needsYou)
        {
            _face.React(FaceMood.Happy, TimeSpan.FromSeconds(2)); // needing the user: it's already listening
        }
    }

    public void SetPanelScale(double scale)
    {
        _settings.Update(s => s with { PanelScale = scale });
        var applied = _window.SetPanelScale(scale);
        _viewModel.StatusText = applied < scale - 0.001
            ? $"That size doesn't fit this screen: using {applied:0.##}×"
            : scale > 1 ? $"Bigger panel: {scale:0.##}×" : "Normal size";
    }

    public void SetShowBorders(bool enabled)
    {
        _settings.Update(s => s with { ShowBorders = enabled });
        ApplyVisualSettings(_settings.Current);
    }

    public void SetReducedMotion(bool enabled)
    {
        _settings.Update(s => s with { ReducedMotion = enabled });
        ApplyVisualSettings(_settings.Current);
    }

    public void SetLaunchAtLogin(bool enabled)
    {
        try
        {
            _startup.SetEnabled(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _logger.LogError(ex, "Could not change launch-at-login");
            _viewModel.StatusText = "Couldn't change 'Start with Windows'";
            _face.React(FaceMood.Concerned);
        }

        _tray.Refresh();
    }

    public void ResetPosition()
    {
        _placement.Reset();
        _settings.Update(s => s with { DockToTop = true });
        ApplyVisualSettings(_settings.Current);
        ShowCompanion();
    }

    public void RunSetup()
    {
        ShowCompanion();
        _viewModel.StartSetup();
        _window.Activate();
    }

    public void OpenAiSettings()
    {
        ShowCompanion();
        _viewModel.OpenAiSettings();
        _window.Activate(); // chosen from a menu: typing a key should go here
    }

    public void OpenDesktopControl()
    {
        ShowCompanion();
        _viewModel.OpenDesktopControl();
        _window.Activate();
    }

    public bool UpdatesAvailable => _updates.IsAvailable;

    public string? UpdateReadyVersion => _updates.ReadyVersion;

    public async void CheckForUpdates()
    {
        _viewModel.StatusText = "Checking for updates…";
        _viewModel.StatusText = await _updates.CheckAsync();
    }

    public void RestartToUpdate()
    {
        _updates.ApplyOnExit(restart: true);
        Exit();
    }

    public void SetCheckForUpdates(bool enabled)
    {
        _settings.Update(s => s with { CheckForUpdates = enabled });
        _viewModel.StatusText = enabled ? "I'll look for updates now and then." : "No automatic update checks.";
    }

    public void Exit()
    {
        _logger.LogInformation("Exit requested");
        _window.AllowClose = true;
        _window.Close();
        Application.Current.Shutdown();
    }

    // ===== Screenshots ======================================================

    /// <summary>
    /// Takes a screenshot to ask about: Mithu steps aside, the screen freezes under a picker (drag an area,
    /// click a window, Enter for the whole screen, Esc to cancel), then Mithu comes back on the chat with the
    /// screenshot attached. Nothing is sent until the user sends the message.
    /// </summary>
    public async Task CaptureScreenAsync()
    {
        if (_capturing)
        {
            return;
        }

        _capturing = true;
        var wasVisible = _window.IsVisible;
        Vision.CaptureOverlay? overlay = null;
        try
        {
            if (wasVisible)
            {
                _window.Hide();
                await Task.Delay(ScreenSettleDelay); // let Windows repaint what was behind Mithu
            }

            var snapshot = Vision.ScreenCapture.SnapMonitorUnderCursor();
            overlay = new Vision.CaptureOverlay(snapshot);
            var pick = await overlay.PickAsync();
            if (pick is not { } chosen)
            {
                if (wasVisible)
                {
                    _window.Show();
                    _window.RefreshPlacement();
                }

                return;
            }

            var png = Vision.ScreenCapture.Crop(snapshot, chosen.Rect);
            _logger.LogInformation("Screenshot taken ({Kind}, {Width}×{Height})", chosen.Kind, chosen.Rect.Width, chosen.Rect.Height);
            ShowCompanion();
            _viewModel.OpenAskCommand.Execute(null);
            _window.Activate(); // the user just acted: typing should go to the message box
            _viewModel.Ask.Attach(png);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException
            or InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            _logger.LogError(ex, "Screenshot failed");
            ShowCompanion();
            _viewModel.StatusText = "Couldn't take the screenshot";
            _face.React(FaceMood.Concerned, TimeSpan.FromSeconds(2));
        }
        finally
        {
            overlay?.Close(); // after Mithu is forward (see CaptureOverlay.Finish)
            _capturing = false;

            // The frozen screen and the picker's copies are garbage now: free them once the picker has closed.
            _ = _dispatcher.BeginInvoke(MemoryRelief.AfterBigImages, DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>How long to wait after hiding Mithu before copying the screen.</summary>
    private static readonly TimeSpan ScreenSettleDelay = TimeSpan.FromMilliseconds(180);

    /// <summary>Registers Ctrl+Shift+S while the setting is on (and frees it for other apps when off).</summary>
    private void SyncScreenshotHotkey()
    {
        var wanted = _settings.Current.Ai.ScreenshotShortcut;
        if (wanted == (_screenshotHotkey.Current is not null))
        {
            return;
        }

        if (!wanted)
        {
            _screenshotHotkey.Unregister();
            return;
        }

        if (!_screenshotHotkey.Register(Core.Ai.AiSettings.ScreenshotHotkey) && !_screenshotHotkeyWarned)
        {
            _screenshotHotkeyWarned = true; // say it once; the 👀 button in the chat still works
            _viewModel.StatusText = $"{Core.Ai.AiSettings.ScreenshotHotkey} is used by another app";
        }
    }

    // ===== Event handlers ===================================================

    private void OnHotkey()
    {
        // Toggle: if Mithu is open and focused, tuck it away.
        if (_window.IsVisible && _window.IsActive && !_viewModel.IsHidden)
        {
            _viewModel.Mode = _settings.Current.DockToTop ? WindowMode.Hidden : _viewModel.Mode;
            if (!_settings.Current.DockToTop)
            {
                _window.Hide();
            }

            return;
        }

        _hiddenForFullscreen = false;
        if (!_viewModel.IsHidden && !_window.IsVisible)
        {
            _viewModel.RefreshGreeting();
        }

        _window.Show();

        // Open straight to the input so the user can type immediately.
        _viewModel.Mode = WindowMode.Expanded;

        // Focus after the (possibly just-created) panel has loaded, so clicking elsewhere will close it.
        _dispatcher.BeginInvoke(_window.FocusInput, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// A focus round or break ran out: a soft chime and a short peek, without taking keyboard focus.
    /// Never interrupts quiet mode, a fullscreen app, or a companion the user hid.
    /// </summary>
    private void OnFocusSessionFinished(FocusSession session)
    {
        if (_settings.Current.QuietMode || _hiddenForFullscreen || !_window.IsVisible)
        {
            _logger.LogDebug("{Kind} finished quietly (quiet mode, fullscreen or hidden)", session.Kind);
            return;
        }

        SoftChime.Play();
        var peeking = _viewModel.Mode != WindowMode.Expanded;
        _viewModel.PeekIsMessage = false;
        _viewModel.ShowPeek();

        // The face was tucked in the notch when the round ended, so its celebration went unseen.
        // Replay it once the peek has slid out.
        if (peeking)
        {
            var celebrate = new DispatcherTimer { Interval = PeekCelebrationDelay };
            celebrate.Tick += (_, _) =>
            {
                celebrate.Stop();
                if (_viewModel.Mode == WindowMode.Peek)
                {
                    _face.React(session.Kind == FocusKind.Break ? FaceMood.Happy : FaceMood.Celebrating, TimeSpan.FromSeconds(3));
                    _face.Bounce();
                }
            };
            celebrate.Start();
        }
    }

    /// <summary>
    /// A reminder went off. The Windows notification is the alert (it works even when Mithu is hidden);
    /// Mithu just looks up and shows it in the status line.
    /// </summary>
    private void OnReminderFired(Core.Reminders.Reminder reminder)
    {
        _viewModel.StatusText = $"⏰ {reminder.Title}";
        if (!CanAlertInApp())
        {
            return; // the Windows notification pops up instead
        }

        // Mithu slides out with the reminder (Snooze / Done right there), with a soft chime, without taking focus.
        SoftChime.Play();
        _viewModel.ShowReminderAlert(reminder);
        var react = new DispatcherTimer { Interval = PeekCelebrationDelay };
        react.Tick += (_, _) =>
        {
            react.Stop();
            _face.React(reminder.Priority == Core.Reminders.ReminderPriority.Urgent ? FaceMood.Concerned : FaceMood.Happy, TimeSpan.FromSeconds(2));
            _face.Bounce();
        };
        react.Start();
    }

    /// <summary>Mithu is on screen and allowed to speak up (not quiet mode, no fullscreen app).</summary>
    private bool CanAlertInApp() => !_settings.Current.QuietMode && !_hiddenForFullscreen && _window.IsVisible;

    private void OnRemindersMissed(int count)
    {
        _viewModel.StatusText = $"You missed {count} reminders while I was away";
        _face.React(FaceMood.Concerned, TimeSpan.FromSeconds(2));
    }

    /// <summary>A notification was clicked: bring Mithu up on Reminders (a click gives Mithu the right to take focus).</summary>
    private void OnReminderOpenRequested(string? reminderId)
    {
        ShowCompanion();
        _viewModel.ShowReminders(reminderId);
        _window.Activate();
    }

    /// <summary>The clock or time zone was changed (by the user or by Windows time sync).</summary>
    private void OnTimeChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(_reminders.OnClockChanged);

    /// <summary>Roughly when the peek has finished sliding out.</summary>
    private static readonly TimeSpan PeekCelebrationDelay = TimeSpan.FromMilliseconds(500);

    private void OnFullscreenChanged(bool isFullscreen)
    {
        if (!_settings.Current.AutoHideInFullscreen)
        {
            return;
        }

        if (isFullscreen && _window.IsVisible)
        {
            _logger.LogDebug("Fullscreen app detected: hiding companion");
            _hiddenForFullscreen = true;
            _window.Hide();
        }
        else if (!isFullscreen && _hiddenForFullscreen)
        {
            _logger.LogDebug("Fullscreen ended: restoring companion");
            _hiddenForFullscreen = false;
            _window.Show();
            _window.RefreshPlacement();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            _logger.LogInformation("Display configuration changed: re-clamping window");
            _window.RefreshPlacement();
        });

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            var locked = e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect;
            _face.SetPaused(locked || !_window.IsVisible || _viewModel.IsHidden);
        });

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _dispatcher.BeginInvoke(() =>
            {
                _viewModel.RefreshGreeting();
                _viewModel.Focus.Refresh(); // a round may have ended while asleep
                _reminders.OnClockChanged(); // and reminders may have come due
                _window.RefreshPlacement();
                _fullscreen.Recheck();
            });
        }
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
        {
            ApplyVisualSettings(_settings.Current);
        }
    }

    // ===== Dozing off =======================================================

    private const string ReminderWait = "reminder-alert";
    private const string ClaudeWait = "claude-code";
    private DispatcherTimer? _sleepTimer;

    /// <summary>
    /// Mithu dozes when the user is away (<see cref="SleepPolicy"/>): a check every 30 s while awake (one cheap
    /// Win32 call), every second while asleep so any mouse or key wakes it at once. MITHU_SLEEP_IDLE_SECONDS
    /// shortens the wait (for testing).
    /// </summary>
    private void StartSleepWatch()
    {
        TimeSpan? testIdle = int.TryParse(Environment.GetEnvironmentVariable("MITHU_SLEEP_IDLE_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
        _sleepTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = testIdle is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1) };
        _sleepTimer.Tick += (_, _) =>
        {
            var idle = Interop.NativeMethods.IdleTime();
            var sleepy = testIdle is { } t ? idle >= t : SleepPolicy.ShouldSleep(idle, DateTime.Now.TimeOfDay);
            _face.SetSleepy(sleepy);
            _sleepTimer.Interval = sleepy || testIdle is not null ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30);
        };
        _sleepTimer.Start();
    }

    // ===== Helpers ==========================================================


    private void ApplyVisualSettings(AppSettings s)
    {
        // Honour both the app toggle and Windows "Show animations in Windows".
        var reduced = s.ReducedMotion || !SystemParameters.ClientAreaAnimation;
        _window.SetReducedMotion(reduced);
        _window.SetBorders(s.ShowBorders);
        _viewModel.Ask.AnimateTyping = !reduced; // replies appear as they arrive instead of being typed out
        _window.Topmost = s.AlwaysOnTop;
        _window.SetDockOptions(s.DockToTop, s.CloseOnClickAway);
        _window.SetPanelScale(s.PanelScale);
    }

    private bool SafeIsLaunchAtLogin()
    {
        try
        {
            return _startup.IsEnabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        // Don't lose the last few keystrokes of a note being edited.
        _viewModel.Notes.Flush();

        // Windows shows the next reminders while Mithu isn't running.
        _reminders.PrepareForExit();
        _reminders.Dispose();

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _viewModel.Focus.SessionFinished -= OnFocusSessionFinished;
        _fullscreen.Dispose();
        _hotkey.Dispose();
        _claude.Dispose();
        _screenshotHotkey.Dispose();
        _tray.Dispose();
        _sleepTimer?.Stop();

        // A downloaded update installs once Mithu has closed (no restart).
        _updates.ApplyOnExit();
        _updates.Dispose();
        _face.Dispose();
    }
}
