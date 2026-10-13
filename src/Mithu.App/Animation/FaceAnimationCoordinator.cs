using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Mithu.App.Controls;
using Mithu.Core.Models;
using Mithu.Core.Mood;

namespace Mithu.App.Animation;

/// <summary>
/// Decides <i>when</i> the face changes: mood priority, transient reactions, and sparse idle blinking.
/// Idle cost: a single one-shot timer firing every 3–7 s. No frame loop.
/// </summary>
public sealed class FaceAnimationCoordinator : IDisposable
{
    private static readonly TimeSpan DefaultTransientDuration = TimeSpan.FromMilliseconds(1600);

    private readonly MoodArbiter _arbiter = new();
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _transientTimer;
    private readonly ILogger<FaceAnimationCoordinator> _logger;
    private CompanionFaceControl? _face;
    private bool _paused = true;

    public FaceAnimationCoordinator(ILogger<FaceAnimationCoordinator> logger)
    {
        _logger = logger;
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background);
        _blinkTimer.Tick += OnBlinkTick;
        _transientTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _transientTimer.Tick += (_, _) =>
        {
            _transientTimer.Stop();
            _arbiter.ClearTransient();
        };
        _arbiter.Changed += OnMoodChanged;
    }

    public FaceMood CurrentMood => _arbiter.Current;

    /// <summary>The long-lived mood (Idle, Focused, Thinking…) that reactions return to.</summary>
    public FaceMood BaseMood => _arbiter.Base;

    public void Attach(CompanionFaceControl face)
    {
        _face = face;
        _face.Mood = _arbiter.Current;
    }

    /// <summary>Starts or stops decorative animation (stop when hidden / locked / quiet).</summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        if (paused)
        {
            _blinkTimer.Stop();
            _face?.StopAnimations();
            _logger.LogDebug("Face animation paused");
        }
        else
        {
            ScheduleNextBlink();
            _face?.ResumeAnimations();
            _logger.LogDebug("Face animation resumed");
        }
    }

    public void SetHover(bool hovering) => _arbiter.SetHover(hovering);

    /// <summary>Something waits for the user's answer: Mithu listens (see <see cref="MoodArbiter.SetWaiting"/>).</summary>
    public void SetWaiting(string reason, bool waiting) => _arbiter.SetWaiting(reason, waiting);

    public bool IsSleepy => _arbiter.IsSleepy;

    /// <summary>The user is away: an idle Mithu dozes; waking up comes with a blink.</summary>
    public void SetSleepy(bool sleepy)
    {
        if (sleepy == _arbiter.IsSleepy)
        {
            return;
        }

        _arbiter.SetSleepy(sleepy);
        if (!sleepy && !_paused)
        {
            _face?.Blink();
        }
    }

    public void SetBaseMood(FaceMood mood) => _arbiter.SetBase(mood);

    /// <summary>Brief reaction (Happy, Celebrating, Concerned) that reverts automatically.</summary>
    public void React(FaceMood mood, TimeSpan? duration = null)
    {
        _arbiter.PushTransient(mood);
        _transientTimer.Stop();
        _transientTimer.Interval = duration ?? DefaultTransientDuration;
        _transientTimer.Start();
    }

    /// <summary>
    /// Holds an expression (top priority) until <see cref="Release"/> is called. Used by the click-reaction
    /// sequence, whose timing is owned by <see cref="Mithu.Core.Mood.ClickReactionMachine"/>.
    /// </summary>
    public void Hold(FaceMood mood)
    {
        _transientTimer.Stop();
        _arbiter.PushTransient(mood);
    }

    /// <summary>Ends a <see cref="Hold"/> and returns to the base mood.</summary>
    public void Release()
    {
        _transientTimer.Stop();
        _arbiter.ClearTransient();
    }

    /// <summary>A playful wink (the AI's [face:wink]).</summary>
    public void Wink() => _face?.Wink();

    /// <summary>Plays the click "punch" squash on the face.</summary>
    public void Punch() => _face?.Punch();


    /// <summary>Plays a happy hop, even if the face already looks happy (e.g. replayed once a peek is visible).</summary>
    public void Bounce() => _face?.Bounce();

    private void OnMoodChanged(FaceMood mood)
    {
        if (_face is not null)
        {
            _face.Mood = mood;
        }
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        _blinkTimer.Stop();
        if (_paused)
        {
            return;
        }

        // Thinking for a while: the eyes drift slowly between two spots, not just once.
        if (_arbiter.Current == FaceMood.Thinking && Random.Shared.NextDouble() < 0.6)
        {
            _face?.Ponder();
            ScheduleNextBlink();
            return;
        }

        _face?.Blink();

        // Occasionally a quick double blink feels more alive.
        if (Random.Shared.NextDouble() < 0.15)
        {
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(260);
            _blinkTimer.Start();
            return;
        }

        ScheduleNextBlink();
    }

    private void ScheduleNextBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(Random.Shared.Next(3000, 7000));
        _blinkTimer.Start();
    }

    public void Dispose()
    {
        _blinkTimer.Stop();
        _transientTimer.Stop();
        _arbiter.Changed -= OnMoodChanged;
    }
}
