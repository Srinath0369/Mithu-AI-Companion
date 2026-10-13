using Mithu.Core.Models;
using Mithu.Core.Motion;

namespace Mithu.Core.Mood;

/// <summary>What the face should do in response to a click.</summary>
public enum ClickReaction
{
    /// <summary>A single, friendly click: smile.</summary>
    Smile,

    /// <summary>Clicked again quickly: squash/wobble with eyes squeezed shut.</summary>
    Punch,
}

/// <summary>Phase of the click-reaction sequence.</summary>
public enum ReactionPhase
{
    Calm,
    Smiling,
    BeingPunched,
    Angry,
}

/// <summary>
/// Turns face clicks into a little personality sequence (pure, deterministic, unit-tested):
/// <list type="number">
/// <item><b>Single click</b> → <see cref="ClickReaction.Smile"/>.</item>
/// <item><b>Clicks within <see cref="ComboWindow"/> of each other</b> → from the 2nd on, <see cref="ClickReaction.Punch"/>.</item>
/// <item><b>Clicking stops</b> → once the last punch animation has finished, <see cref="ReactionPhase.Angry"/>
///   for <see cref="AngryDuration"/>.</item>
/// <item>Clicking while angry counts as more punching: the anger restarts after the new punches.</item>
/// </list>
/// The machine never runs timers itself; callers pass the current time to <see cref="Click"/> and
/// <see cref="Tick"/>, and ask <see cref="NextWakeUp"/> when to call <see cref="Tick"/> again.
/// </summary>
public sealed class ClickReactionMachine
{
    /// <summary>Max gap between clicks that still counts as "continuous clicking".</summary>
    public static readonly TimeSpan ComboWindow = TimeSpan.FromMilliseconds(600);

    /// <summary>How long Mithu stays angry after the punching stops.</summary>
    public static readonly TimeSpan AngryDuration = TimeSpan.FromSeconds(2);

    /// <summary>How long the friendly smile lasts after a single click.</summary>
    public static readonly TimeSpan SmileDuration = TimeSpan.FromMilliseconds(1400);

    /// <summary>Length of one punch animation; anger starts only after the last one has finished.</summary>
    public static readonly TimeSpan PunchDuration = TimeSpan.FromSeconds(PunchCurve.Duration);

    private DateTimeOffset _lastClick = DateTimeOffset.MinValue;
    private DateTimeOffset _phaseEnds = DateTimeOffset.MinValue;

    public ReactionPhase Phase { get; private set; } = ReactionPhase.Calm;

    /// <summary>Number of punches in the current streak (0 when calm or smiling).</summary>
    public int PunchCount { get; private set; }

    /// <summary>Expression for the current phase (Idle when calm).</summary>
    public FaceMood Mood => Phase switch
    {
        ReactionPhase.Smiling => FaceMood.Happy,
        ReactionPhase.BeingPunched => FaceMood.Hurt,
        ReactionPhase.Angry => FaceMood.Angry,
        _ => FaceMood.Idle,
    };

    /// <summary>Raised whenever <see cref="Phase"/> changes.</summary>
    public event Action<ReactionPhase>? PhaseChanged;

    /// <summary>Registers a click and returns the reaction to play.</summary>
    public ClickReaction Click(DateTimeOffset now)
    {
        var continuing = Phase is ReactionPhase.BeingPunched or ReactionPhase.Angry ||
                         (Phase == ReactionPhase.Smiling && now - _lastClick <= ComboWindow);
        _lastClick = now;

        if (!continuing)
        {
            PunchCount = 0;
            SetPhase(ReactionPhase.Smiling, now + SmileDuration);
            return ClickReaction.Smile;
        }

        PunchCount++;

        // The anger timer starts only after the last punch has finished AND the combo window has passed,
        // so a steady stream of clicks keeps punching.
        var punchEnds = now + Max(PunchDuration, ComboWindow);
        SetPhase(ReactionPhase.BeingPunched, punchEnds);
        return ClickReaction.Punch;
    }

    /// <summary>Advances time-based transitions. Safe to call any time.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (Phase == ReactionPhase.Calm || now < _phaseEnds)
        {
            return;
        }

        switch (Phase)
        {
            case ReactionPhase.BeingPunched:
                SetPhase(ReactionPhase.Angry, _phaseEnds + AngryDuration);

                // If Tick was called late, the anger may already be over too.
                Tick(now);
                break;

            default:
                PunchCount = 0;
                SetPhase(ReactionPhase.Calm, DateTimeOffset.MaxValue);
                break;
        }
    }

    /// <summary>When <see cref="Tick"/> next needs to run, or <c>null</c> when calm.</summary>
    public DateTimeOffset? NextWakeUp => Phase == ReactionPhase.Calm ? null : _phaseEnds;

    /// <summary>Forgets everything (e.g. Mithu was hidden).</summary>
    public void Reset()
    {
        PunchCount = 0;
        _lastClick = DateTimeOffset.MinValue;
        SetPhase(ReactionPhase.Calm, DateTimeOffset.MaxValue);
    }

    private void SetPhase(ReactionPhase phase, DateTimeOffset endsAt)
    {
        _phaseEnds = endsAt;
        if (Phase == phase)
        {
            return;
        }

        Phase = phase;
        PhaseChanged?.Invoke(phase);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
