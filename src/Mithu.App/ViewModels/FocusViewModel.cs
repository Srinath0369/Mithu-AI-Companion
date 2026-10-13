using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mithu.App.Animation;
using Mithu.Core.Focus;
using Mithu.Core.Interfaces;
using Mithu.Core.Models;
using Mithu.Core.Mood;

namespace Mithu.App.ViewModels;

/// <summary>Which view the Focus page shows.</summary>
public enum FocusCard
{
    /// <summary>Intention, length, rounds, break + Start.</summary>
    Setup,

    /// <summary>A focus round or break is counting down (or paused).</summary>
    Countdown,

    /// <summary>A countdown ran out: offer the break / next round, or celebrate the end of the session.</summary>
    Finished,
}

/// <summary>One focus-length chip ("25 min", or "Custom").</summary>
public sealed partial class FocusChoice(int minutes, string label, Action<FocusChoice> select) : ObservableObject
{
    /// <summary>Preset length in minutes; 0 for the Custom chip.</summary>
    public int Minutes { get; } = minutes;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => select(this);
}

/// <summary>
/// The focus timer as the UI sees it: setup choices, countdown text, progress, card state and commands.
/// <see cref="FocusTimer"/> (Core) owns the rules; this class owns the single <see cref="DispatcherTimer"/>:
/// once a second only while the countdown text is on screen, a slow step while only the notch line shows,
/// and otherwise a single wake-up at the deadline. Saves on every state change, never per tick.
/// </summary>
public sealed partial class FocusViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(20);

    // Fire just after the displayed second changes, so the text never lags behind.
    private static readonly TimeSpan SecondMargin = TimeSpan.FromMilliseconds(15);

    /// <summary>How long a short message replaces the title under the timer.</summary>
    private static readonly TimeSpan WhisperDuration = TimeSpan.FromSeconds(3);

    /// <summary>Gentle lines when the face is clicked during a focus round (no punching while you work).</summary>
    private static readonly string[] Cheers =
    [
        "You've got this!",
        "Nice and steady.",
        "I'm right here with you.",
        "One thing at a time.",
        "Keep going, you're doing great.",
    ];

    /// <summary>Grumpy lines when a focus round is stopped before it's done.</summary>
    private static readonly string[] StopLines = ["Hmph! We were doing so well.", "Grr… giving up already?", "Not cool! We were on a roll."];

    private readonly FocusTimer _timer;
    private readonly IFocusStore _store;
    private readonly FaceAnimationCoordinator _face;
    private readonly ILogger<FocusViewModel> _logger;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _whisperTimer;
    private readonly DispatcherTimer _calmTimer;
    private string? _whisper;
    private int _cheerIndex;
    private int _stopIndex;
    private bool _halfwayDone;
    private bool _windowVisible = true;
    private bool _textVisible = true;
    private FaceMood _appliedMood = FaceMood.Idle;

    public FocusViewModel(TimeProvider time, IFocusStore store, FaceAnimationCoordinator face, ILogger<FocusViewModel> logger)
    {
        _store = store;
        _face = face;
        _logger = logger;
        _timer = new FocusTimer(time, store.Load());

        // Start from the last choices.
        var minutes = (int)Math.Round(_timer.FocusLength.TotalMinutes);
        _isCustom = !FocusPlan.FocusChoices.Contains(minutes);
        _selectedMinutes = _isCustom ? FocusPlan.DefaultFocusMinutes : minutes;
        _customMinutesText = _isCustom ? minutes.ToString() : string.Empty;
        _plannedRounds = _timer.Rounds;
        _breakMinutes = NearestBreakChoice((int)Math.Round(_timer.BreakLength.TotalMinutes));

        Choices = FocusPlan.FocusChoices.Select(m => new FocusChoice(m, $"{m} min", Choose)).ToList();
        CustomChoice = new FocusChoice(0, "Custom", Choose);
        UpdateChoices();

        _tick = new DispatcherTimer(DispatcherPriority.Normal);
        _tick.Tick += (_, _) => OnTick();
        _whisperTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = WhisperDuration };
        _whisperTimer.Tick += (_, _) =>
        {
            _whisperTimer.Stop();
            _whisper = null;
            OnPropertyChanged(nameof(CompactSubtitle));
        };

        // After the stop-anger passes, a calmer line (unless a new session has already started).
        _calmTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ClickReactionMachine.AngryDuration };
        _calmTimer.Tick += (_, _) =>
        {
            _calmTimer.Stop();
            if (!IsCounting)
            {
                Say("Okay… we'll try again later.");
            }
        };

        // A round restored past its middle shouldn't nudge again.
        _halfwayDone = _timer.Progress >= 0.5;
        _timer.Changed += OnTimerChanged;
        _timer.Finished += OnTimerFinished;

        if (_timer.FinishedWhileAway is { } away)
        {
            StartupMessage = away.Kind == FocusKind.Break
                ? "Your break ended while I was away."
                : "Your focus round finished while I was away.";
            _store.Save(_timer.Data);
        }

        ApplyMood();
        Schedule();
    }

    /// <summary>Raised when a focus round or break runs out by itself (for the peek and chime).</summary>
    public event Action<FocusSession>? SessionFinished;

    /// <summary>A short line for the status text whenever something happens.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Raised after every state change (start, pause, finish, …).</summary>
    public event Action? StateChanged;

    /// <summary>Message to show at launch (e.g. a round finished while Mithu was closed), or null.</summary>
    public string? StartupMessage { get; }

    // ===== Setup ============================================================

    /// <summary>Preset length chips.</summary>
    public IReadOnlyList<FocusChoice> Choices { get; }

    /// <summary>The "Custom" chip; when chosen, a minutes box replaces it.</summary>
    public FocusChoice CustomChoice { get; }

    [ObservableProperty]
    private string _intentionDraft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _selectedMinutes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomInvalid), nameof(StartText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isCustom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomInvalid), nameof(StartText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _customMinutesText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseBreak), nameof(StartText))]
    [NotifyCanExecuteChangedFor(nameof(MoreRoundsCommand), nameof(FewerRoundsCommand))]
    private int _plannedRounds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BreakText))]
    [NotifyCanExecuteChangedFor(nameof(LongerBreakCommand), nameof(ShorterBreakCommand))]
    private int _breakMinutes;

    /// <summary>Start button: "Start · 25 min", "Start · 37 min × 3".</summary>
    public string StartText => EffectiveMinutes is not { } minutes ? "Enter 1–180 minutes"
        : PlannedRounds > 1 ? $"Start · {minutes} min × {PlannedRounds}"
        : $"Start · {minutes} min";

    /// <summary>Focus length to start with, or null when the custom minutes aren't a valid number.</summary>
    public int? EffectiveMinutes
    {
        get
        {
            if (!IsCustom)
            {
                return SelectedMinutes;
            }

            return int.TryParse(CustomMinutesText.Trim(), out var minutes) && minutes is >= 1 and <= FocusPlan.MaxFocusMinutes
                ? minutes
                : null;
        }
    }

    /// <summary>Custom box shows a warning rim (something typed, but not 1–180).</summary>
    public bool IsCustomInvalid => IsCustom && CustomMinutesText.Trim().Length > 0 && EffectiveMinutes is null;

    /// <summary>A break only makes sense between rounds.</summary>
    public bool CanChooseBreak => PlannedRounds > 1;

    public string BreakText => BreakMinutes == 0 ? "None" : $"{BreakMinutes} min";

    // ===== Display ==========================================================

    public FocusState State => _timer.State;

    public FocusCard Card => _timer.State switch
    {
        FocusState.Idle => FocusCard.Setup,
        FocusState.Finished => FocusCard.Finished,
        _ => FocusCard.Countdown,
    };

    /// <summary>Anything to show in the header and notch (counting, paused or waiting after a finish).</summary>
    public bool IsActive => State != FocusState.Idle;

    /// <summary>A focus round (not a break) is running or paused: Mithu stays quiet.</summary>
    public bool IsFocusing => State is FocusState.Running or FocusState.Paused && !_timer.IsBreak;

    public bool IsBreak => IsActive && _timer.IsBreak;

    /// <summary>A focus round or break is counting down or paused (the header shows time + pause).</summary>
    public bool IsCounting => State is FocusState.Running or FocusState.Paused;

    /// <summary>
    /// Second header line while counting (kept short): "Write report · 1/3", "Paused · Write report",
    /// or for a few seconds a message from Mithu ("You've got this!").
    /// </summary>
    public string CompactSubtitle =>
        _whisper is { } whisper ? whisper
        : IsPaused ? $"Paused · {Label}"
        : _timer.IsBreak ? $"Break · round {_timer.Round + 1} next"
        : _timer.Rounds > 1 ? $"{Label} · {_timer.Round}/{_timer.Rounds}"
        : Label;

    public bool IsPaused => State == FocusState.Paused;

    public double Progress => _timer.Progress;

    public string RemainingText => FormatClock(_timer.Remaining);

    /// <summary>Header line while active: "18:42 · Write report", "Round 1 of 3 done".</summary>
    public string HeaderText => State switch
    {
        FocusState.Running => $"{RemainingText} · {Label}",
        FocusState.Paused => $"{RemainingText} · Paused",
        FocusState.Finished => FinishedTitle(header: true),
        _ => string.Empty,
    };

    public string Title => State == FocusState.Finished ? FinishedTitle(header: false) : Label;

    public string Detail => State switch
    {
        FocusState.Running when _timer.IsBreak => $"Stretch, breathe · round {_timer.Round + 1} of {_timer.Rounds} next",
        FocusState.Paused when _timer.IsBreak => "Break paused",
        FocusState.Running => RoundText(),
        FocusState.Paused => $"Paused · {RoundText().ToLowerInvariant()}",
        FocusState.Finished => FinishedDetail(),
        _ => string.Empty,
    };

    /// <summary>Small line inside the progress ring.</summary>
    public string RingCaption => IsPaused ? "paused" : "remaining";

    /// <summary>Small badge on the Focus tile: the live countdown, or "Done"; nothing when no round is on.</summary>
    public string TileBadge => State switch
    {
        FocusState.Running or FocusState.Paused => RemainingText,
        FocusState.Finished => "Done",
        _ => string.Empty,
    };

    public bool HasTileBadge => TileBadge.Length > 0;

    /// <summary>Main answer when a countdown finished: "Take 5", "Start round 2", "Done".</summary>
    public string PrimaryText => _timer.IsSessionComplete ? "Done"
        : OffersBreak ? $"Take {(int)_timer.BreakLength.TotalMinutes}"
        : $"Start round {_timer.Round + 1}";

    /// <summary>Second answer: "Skip break" or "One more"; null when there is none.</summary>
    public string? SecondaryText => _timer.IsSessionComplete ? "One more"
        : OffersBreak ? "Skip break"
        : null;

    public bool HasSecondary => SecondaryText is not null;

    /// <summary>✕ "End session" between rounds (after the last round, "Done" does that).</summary>
    public bool CanEndSession => _timer.IsBetweenRounds;

    public string PauseGlyph => IsPaused ? "" : "";

    public string PauseLabel => IsPaused ? "Resume" : "Pause";

    /// <summary>A focus round just finished, more rounds are left, and a break is planned.</summary>
    private bool OffersBreak => _timer.IsBetweenRounds && !_timer.IsBreak && _timer.HasBreak;

    private string Label => _timer.IsBreak ? "Break" : _timer.Intention ?? "Focus";

    // ===== Commands: setup ==================================================

    [RelayCommand(CanExecute = nameof(CanMoreRounds))]
    private void MoreRounds() => PlannedRounds++;

    private bool CanMoreRounds() => PlannedRounds < FocusPlan.MaxRounds;

    [RelayCommand(CanExecute = nameof(CanFewerRounds))]
    private void FewerRounds() => PlannedRounds--;

    private bool CanFewerRounds() => PlannedRounds > 1;

    [RelayCommand(CanExecute = nameof(CanLongerBreak))]
    private void LongerBreak() => BreakMinutes = FocusPlan.BreakChoices.First(m => m > BreakMinutes);

    private bool CanLongerBreak() => BreakMinutes < FocusPlan.BreakChoices[^1];

    [RelayCommand(CanExecute = nameof(CanShorterBreak))]
    private void ShorterBreak() => BreakMinutes = FocusPlan.BreakChoices.Last(m => m < BreakMinutes);

    private bool CanShorterBreak() => BreakMinutes > FocusPlan.BreakChoices[0];

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        if (EffectiveMinutes is not { } minutes)
        {
            return;
        }

        _timer.StartSession(TimeSpan.FromMinutes(minutes), PlannedRounds, TimeSpan.FromMinutes(BreakMinutes), IntentionDraft);
        OnRoundStarted();

        var on = _timer.Intention is { } intention ? $"Focusing on {intention}." : "Focus time.";
        Say(_timer.Rounds > 1 ? $"{on} Round 1 of {_timer.Rounds}. I'll keep quiet." : $"{on} I'll keep quiet until it's done.");
        _logger.LogInformation("Focus session started ({Minutes} min × {Rounds}, break {Break} min)", minutes, _timer.Rounds, BreakMinutes);
    }

    private bool CanStart() => EffectiveMinutes is not null;

    // ===== For the assistant (same steps as the buttons) ======================

    /// <summary>Starts a session asked for in the chat. False if one is already running.</summary>
    public bool StartFromAssistant(int minutes, int rounds, int breakMinutes, string? intention)
    {
        if (IsActive)
        {
            return false;
        }

        minutes = Math.Clamp(minutes, 1, FocusPlan.MaxFocusMinutes);
        rounds = Math.Clamp(rounds, 1, FocusPlan.MaxRounds);
        breakMinutes = Math.Clamp(breakMinutes, 0, FocusPlan.MaxBreakMinutes);
        _timer.StartSession(TimeSpan.FromMinutes(minutes), rounds, TimeSpan.FromMinutes(breakMinutes), intention);
        OnRoundStarted();
        _logger.LogInformation("Focus session started from chat ({Minutes} min × {Rounds})", minutes, rounds);
        return true;
    }

    /// <summary>Pauses (true) or resumes (false) from the chat. False if there was nothing to pause/resume.</summary>
    public bool PauseFromAssistant(bool pause)
    {
        if (pause ? State != FocusState.Running : State != FocusState.Paused)
        {
            return false;
        }

        PauseResume();
        return true;
    }

    /// <summary>"Running · 18:42 left · Write report · round 1 of 3", "Paused · …", "Idle".</summary>
    public string StatusForAssistant() => State switch
    {
        FocusState.Running or FocusState.Paused =>
            $"{(IsPaused ? "Paused" : "Running")} · {(IsBreak ? "break" : "focus")} · {RemainingText} left · {Label}" +
            (_timer.Rounds > 1 ? $" · round {_timer.Round} of {_timer.Rounds}" : string.Empty),
        FocusState.Finished => $"Finished: {FinishedTitle(header: false)} (waiting for the user to continue or end)",
        _ => "No focus session running.",
    };

    // ===== Commands: countdown ==============================================

    [RelayCommand]
    private void PauseResume()
    {
        if (State == FocusState.Paused)
        {
            _timer.Resume();
            Say(_timer.IsBreak ? "Back to your break." : "Back to it.");
        }
        else if (State == FocusState.Running)
        {
            _timer.Pause();
            Say("Paused. Take your time.");
        }
    }

    /// <summary>Ends the whole session early.</summary>
    [RelayCommand]
    private void Stop()
    {
        var wasBreak = _timer.IsBreak;
        _timer.Stop();

        if (wasBreak)
        {
            Say("Session ended. Ready when you are.");
        }
        else
        {
            // Quitting a focus round halfway: Mithu is annoyed for a moment (same face as the click anger), then calms down.
            _face.React(FaceMood.Angry, ClickReactionMachine.AngryDuration);
            Say(StopLines[_stopIndex++ % StopLines.Length]);
            _calmTimer.Stop();
            _calmTimer.Start();
        }

        _logger.LogInformation("Focus session stopped early");
    }

    // ===== Commands: finished ===============================================

    /// <summary>"Take 5" (break), "Start round N", or "Done".</summary>
    [RelayCommand]
    private void Primary()
    {
        if (State != FocusState.Finished)
        {
            return;
        }

        if (_timer.IsSessionComplete)
        {
            _timer.Dismiss();
            Say("Session complete. Ready when you are.");
        }
        else if (OffersBreak)
        {
            _timer.StartBreak();
            Say("Break time. Stretch, breathe, sip some water.");
            _logger.LogInformation("Break started");
        }
        else
        {
            StartNextRound();
        }
    }

    /// <summary>"Skip break" (next round now) or "One more" (adds a round after the last one).</summary>
    [RelayCommand]
    private void Secondary()
    {
        if (State == FocusState.Finished && HasSecondary)
        {
            StartNextRound();
        }
    }

    /// <summary>✕ between rounds: end the session here.</summary>
    [RelayCommand]
    private void EndSession()
    {
        _timer.Dismiss();
        Say("Session ended. Ready when you are.");
    }

    /// <summary>Face click during a focus round: a quick smile and a soft line instead of the playful reactions.</summary>
    public void Encourage()
    {
        _face.React(FaceMood.Happy, TimeSpan.FromMilliseconds(1200));
        Say(Cheers[_cheerIndex++ % Cheers.Length]);
    }

    /// <summary>Shows a short message under the timer for a few seconds, then the title comes back.</summary>
    public void ShowWhisper(string text)
    {
        _whisper = text;
        OnPropertyChanged(nameof(CompactSubtitle));
        _whisperTimer.Stop();
        _whisperTimer.Start();
    }

    /// <summary>A focus round just began: a little cheer (then the calm focus face) and a fresh halfway nudge.</summary>
    private void OnRoundStarted()
    {
        _halfwayDone = false;
        _face.React(FaceMood.Celebrating, TimeSpan.FromMilliseconds(1200));
    }

    private void StartNextRound()
    {
        _timer.NextRound();
        OnRoundStarted();
        Say($"Round {_timer.Round} of {_timer.Rounds}. Let's go.");
        _logger.LogInformation("Focus round {Round} of {Rounds} started", _timer.Round, _timer.Rounds);
    }

    // ===== Choices ==========================================================

    private void Choose(FocusChoice choice)
    {
        if (choice == CustomChoice)
        {
            if (string.IsNullOrWhiteSpace(CustomMinutesText))
            {
                CustomMinutesText = SelectedMinutes.ToString();
            }

            IsCustom = true;
        }
        else
        {
            SelectedMinutes = choice.Minutes;
            IsCustom = false;
        }

        UpdateChoices();
    }

    private void UpdateChoices()
    {
        foreach (var choice in Choices)
        {
            choice.IsSelected = !IsCustom && choice.Minutes == SelectedMinutes;
        }

        CustomChoice.IsSelected = IsCustom;
    }

    private static int NearestBreakChoice(int minutes) =>
        FocusPlan.BreakChoices.MinBy(choice => Math.Abs(choice - minutes));

    // ===== Ticking ==========================================================

    /// <summary>
    /// Tells the timer what is on screen, so it only ticks as often as something visible changes.
    /// </summary>
    /// <param name="windowVisible">Mithu is on screen (the notch line or the countdown can be seen).</param>
    /// <param name="textVisible">The countdown text is on screen (bar, panel or peek).</param>
    public void SetVisibility(bool windowVisible, bool textVisible)
    {
        if (windowVisible == _windowVisible && textVisible == _textVisible)
        {
            return;
        }

        _windowVisible = windowVisible;
        _textVisible = textVisible;
        Refresh();
    }

    /// <summary>Re-reads the clock (after sleep, or when Mithu becomes visible) and reschedules.</summary>
    public void Refresh()
    {
        _timer.Tick();
        OnPropertyChanged(string.Empty);
        Schedule();
    }

    private void OnTick()
    {
        _tick.Stop();
        _timer.Tick();
        if (_timer.State == FocusState.Running)
        {
            NudgeAtHalfway();
            PublishTime();
            Schedule();
        }
    }

    /// <summary>
    /// Once per focus round, past the middle: a small silent smile — only if the countdown is on screen
    /// (nothing ever pops up for it).
    /// </summary>
    private void NudgeAtHalfway()
    {
        if (_halfwayDone || _timer.IsBreak || _timer.Progress < 0.5)
        {
            return;
        }

        _halfwayDone = true;
        if (_textVisible)
        {
            _face.React(FaceMood.Happy, TimeSpan.FromMilliseconds(1500));
            Say("Halfway there. Nice work!");
            _logger.LogInformation("Halfway nudge");
        }
    }

    private void Schedule()
    {
        _tick.Stop();
        if (_timer.State != FocusState.Running)
        {
            return;
        }

        var remaining = _timer.Remaining;
        var next = remaining;
        if (_textVisible)
        {
            // Until the shown second changes (text is rounded up: 18:42.3 shows "18:43").
            var fraction = remaining - TimeSpan.FromSeconds(Math.Floor(remaining.TotalSeconds));
            next = Min(next, (fraction > TimeSpan.Zero ? fraction : TimeSpan.FromSeconds(1)) + SecondMargin);
        }
        else if (_windowVisible)
        {
            next = Min(next, NotchStep(_timer.Duration));
        }

        _tick.Interval = next > MinInterval ? next : MinInterval;
        _tick.Start();
    }

    /// <summary>The notch line is about 50 px long: roughly one step per pixel is plenty.</summary>
    private static TimeSpan NotchStep(TimeSpan duration)
    {
        var step = duration / 240;
        return step < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1)
            : step > TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15)
            : step;
    }

    private void PublishTime()
    {
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(TileBadge));
        OnPropertyChanged(nameof(HasTileBadge));
    }

    // ===== Timer events =====================================================

    private void OnTimerChanged()
    {
        _store.Save(_timer.Data);
        ApplyMood();
        OnPropertyChanged(string.Empty);
        Schedule();
        StateChanged?.Invoke();
    }

    private void OnTimerFinished(FocusSession session)
    {
        if (session.Kind == FocusKind.Break)
        {
            _face.React(FaceMood.Happy, TimeSpan.FromSeconds(2));
            Say($"Break's over. Ready for round {_timer.Round + 1}?");
            _logger.LogInformation("Break finished");
        }
        else
        {
            _face.React(FaceMood.Celebrating, TimeSpan.FromSeconds(3));
            Say(_timer.IsSessionComplete
                ? _timer.Rounds > 1 ? $"All {_timer.Rounds} rounds done. Great work!" : "Nice work! Session complete."
                : OffersBreak ? $"Round {_timer.Round} done! Take a {(int)_timer.BreakLength.TotalMinutes}-minute break?"
                : $"Round {_timer.Round} done! Ready for round {_timer.Round + 1}?");
            _logger.LogInformation("Focus round {Round} of {Rounds} finished", _timer.Round, _timer.Rounds);
        }

        SessionFinished?.Invoke(session);
    }

    /// <summary>
    /// Calm "focused" face while a focus round runs, a relaxed one during a break, normal otherwise.
    /// Only touches the base mood when it changes.
    /// </summary>
    private void ApplyMood()
    {
        var mood = State != FocusState.Running ? FaceMood.Idle
            : _timer.IsBreak ? FaceMood.Relaxed
            : FaceMood.Focused;
        if (mood != _appliedMood)
        {
            _appliedMood = mood;
            _face.SetBaseMood(mood);
        }
    }

    /// <summary>Status line; while counting it also shows under the timer for a few seconds.</summary>
    private void Say(string text)
    {
        StatusChanged?.Invoke(text);
        if (IsCounting)
        {
            ShowWhisper(text);
        }
    }

    // ===== Text =============================================================

    private string RoundText() => _timer.Rounds > 1
        ? $"Round {_timer.Round} of {_timer.Rounds}"
        : $"{FormatMinutes(_timer.Duration)} focus";

    private string FinishedTitle(bool header)
    {
        if (_timer.IsBreak)
        {
            return "Break's over";
        }

        if (_timer.IsSessionComplete)
        {
            return _timer.Rounds > 1 ? "All done!"
                : header ? $"Focus done · {FormatMinutes(_timer.Duration)}"
                : "Nice work!";
        }

        return $"Round {_timer.Round} of {_timer.Rounds} done";
    }

    private string FinishedDetail()
    {
        if (_timer.IsBreak)
        {
            return $"Ready for round {_timer.Round + 1} of {_timer.Rounds}?";
        }

        var on = _timer.Intention is { } intention ? $" on {intention}" : string.Empty;
        if (_timer.IsSessionComplete && _timer.Rounds > 1)
        {
            return $"{Plural(_timer.Rounds, "round")} · {FormatMinutes(_timer.FocusLength * _timer.Rounds)}{on}";
        }

        if (_timer.IsSessionComplete)
        {
            return $"{FormatMinutes(_timer.Duration)}{(on.Length > 0 ? on : " of focus")}";
        }

        var left = _timer.Rounds - _timer.Round;
        return $"{FormatMinutes(_timer.Duration)}{on} · {left} to go";
    }

    /// <summary>Countdown text, rounded up so it reads 25:00 at the start and 0:01 in the last second.</summary>
    internal static string FormatClock(TimeSpan remaining)
    {
        var total = (long)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        var (h, m, s) = (total / 3600, total / 60 % 60, total % 60);
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    internal static string FormatMinutes(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        return minutes < 60 ? $"{minutes} min"
            : minutes % 60 == 0 ? $"{minutes / 60} h"
            : $"{minutes / 60} h {minutes % 60} min";
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public void Dispose()
    {
        _tick.Stop();
        _whisperTimer.Stop();
        _calmTimer.Stop();
        _timer.Changed -= OnTimerChanged;
        _timer.Finished -= OnTimerFinished;
    }
}
