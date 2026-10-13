using Mithu.Core.Models;

namespace Mithu.Core.Mood;

/// <summary>
/// Decides which expression wins when several are requested at once (blueprint §4).
/// Priority: transient (confirmation / error) &gt; waiting on the user (Listening) &gt; Listening / Thinking base &gt;
/// Hover &gt; base, where an Idle base turns Sleepy while the user is away.
/// </summary>
public sealed class MoodArbiter
{
    private readonly HashSet<string> _waiting = [];
    private FaceMood _baseMood = FaceMood.Idle;
    private bool _hover;
    private bool _sleepy;
    private FaceMood? _transient;

    /// <summary>The expression that should currently be shown.</summary>
    public FaceMood Current { get; private set; } = FaceMood.Idle;

    /// <summary>The long-lived mood set with <see cref="SetBase"/>.</summary>
    public FaceMood Base => _baseMood;

    /// <summary>Raised only when <see cref="Current"/> actually changes.</summary>
    public event Action<FaceMood>? Changed;

    /// <summary>Long-lived state, e.g. Idle, Thinking while an AI call is pending, Listening while the mic is on.</summary>
    public void SetBase(FaceMood mood)
    {
        if (mood == FaceMood.Hover)
        {
            throw new ArgumentException("Hover is pointer state; use SetHover.", nameof(mood));
        }

        _baseMood = mood;
        Recompute();
    }

    public void SetHover(bool isHovering)
    {
        _hover = isHovering;
        Recompute();
    }

    /// <summary>
    /// Something is waiting for the user's answer (an approval in chat, a reminder alert, Claude Code's
    /// permission prompt): Mithu listens attentively until every one of them is answered.
    /// </summary>
    public void SetWaiting(string reason, bool waiting)
    {
        if (waiting ? _waiting.Add(reason) : _waiting.Remove(reason))
        {
            Recompute();
        }
    }

    public bool IsWaiting => _waiting.Count > 0;

    /// <summary>The user is away (or it's late): an idle Mithu dozes. Anything else it's doing comes first.</summary>
    public void SetSleepy(bool sleepy)
    {
        _sleepy = sleepy;
        Recompute();
    }

    public bool IsSleepy => _sleepy;

    /// <summary>Short-lived reaction (Happy, Celebrating, Concerned). Latest request wins.</summary>
    public void PushTransient(FaceMood mood)
    {
        _transient = mood;
        Recompute();
    }

    public void ClearTransient()
    {
        _transient = null;
        Recompute();
    }

    public static FaceMood Resolve(FaceMood baseMood, bool hover, FaceMood? transient, bool waiting = false, bool sleepy = false)
    {
        if (transient is { } t)
        {
            return t;
        }

        if (waiting)
        {
            return FaceMood.Listening;
        }

        if (baseMood is FaceMood.Listening or FaceMood.Thinking)
        {
            return baseMood;
        }

        if (hover)
        {
            return FaceMood.Hover;
        }

        return baseMood == FaceMood.Idle && sleepy ? FaceMood.Sleepy : baseMood;
    }

    private void Recompute()
    {
        var next = Resolve(_baseMood, _hover, _transient, _waiting.Count > 0, _sleepy);
        if (next == Current)
        {
            return;
        }

        Current = next;
        Changed?.Invoke(next);
    }
}
