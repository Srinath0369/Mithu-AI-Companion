namespace Mithu.Core.Focus;

/// <summary>
/// The focus timer's rules (pure, deterministic, unit-tested). The user plans a session: focus length,
/// how many rounds, and the break between them.
/// <list type="bullet">
/// <item>Time left is always <c>deadline − now</c>, never counted ticks, so it stays right across sleep,
///   clock changes and relaunches.</item>
/// <item>When a countdown runs out it waits in <see cref="FocusState.Finished"/>: with rounds left, the break
///   (if any) or the next round is offered; after the last round, the session is complete. Nothing starts by itself.</item>
/// </list>
/// Owns no timers: the caller calls <see cref="Tick"/> at <see cref="Deadline"/> (calling it any time is safe).
/// </summary>
public sealed class FocusTimer
{
    public const int MaxIntentionLength = 80;

    private readonly TimeProvider _time;
    private FocusData _data;
    private FocusSession? _session;

    /// <param name="saved">Data from the last run. A running countdown continues; one that ran out while
    /// Mithu was closed is reported in <see cref="FinishedWhileAway"/>.</param>
    public FocusTimer(TimeProvider time, FocusData? saved = null)
    {
        _time = time;
        _data = (saved ?? new FocusData()) with { Active = null };
        Restore(saved?.Active);
    }

    public FocusState State { get; private set; }

    public FocusKind Kind => _session?.Kind ?? FocusKind.Focus;

    public bool IsBreak => Kind == FocusKind.Break;

    public string? Intention => _session?.Intention;

    public TimeSpan Duration => _session?.Duration ?? TimeSpan.Zero;

    public TimeSpan Remaining => State switch
    {
        // Clamped: a clock moved backwards never shows more than the full length.
        FocusState.Running => Clamp(_session!.DeadlineUtc!.Value - _time.GetUtcNow(), _session.Duration),
        FocusState.Paused => _session!.Remaining,
        _ => TimeSpan.Zero,
    };

    /// <summary>0 at the start, 1 when the countdown has run out.</summary>
    public double Progress => State switch
    {
        FocusState.Idle => 0,
        FocusState.Finished => 1,
        _ => Duration <= TimeSpan.Zero ? 0 : 1 - (Remaining / Duration),
    };

    /// <summary>When <see cref="Tick"/> should run next, or null when nothing is counting down.</summary>
    public DateTimeOffset? Deadline => State == FocusState.Running ? _session!.DeadlineUtc : null;

    /// <summary>Round in progress (1-based; during a break, the round just finished). 0 when idle.</summary>
    public int Round => State == FocusState.Idle ? 0 : _data.Round;

    /// <summary>Rounds planned for this session (or the last one chosen, when idle).</summary>
    public int Rounds => _data.Rounds;

    public TimeSpan FocusLength => _data.FocusLength;

    public TimeSpan BreakLength => _data.BreakLength;

    public bool HasBreak => _data.BreakLength > TimeSpan.Zero;

    /// <summary>The last planned round has just finished.</summary>
    public bool IsSessionComplete => State == FocusState.Finished && !IsBreak && _data.Round >= _data.Rounds;

    /// <summary>A round finished and there are more to go.</summary>
    public bool IsBetweenRounds => State == FocusState.Finished && !IsSessionComplete;

    /// <summary>A countdown that ran out while Mithu was closed, or null.</summary>
    public FocusSession? FinishedWhileAway { get; private set; }

    /// <summary>Snapshot to persist.</summary>
    public FocusData Data => _data with { Active = State is FocusState.Running or FocusState.Paused ? _session : null };

    /// <summary>Raised after every state change (not every second).</summary>
    public event Action? Changed;

    /// <summary>Raised when a countdown runs out by itself (after <see cref="Changed"/>).</summary>
    public event Action<FocusSession>? Finished;

    /// <summary>
    /// Starts a session: <paramref name="rounds"/> focus rounds of <paramref name="focus"/>, with
    /// <paramref name="breakLength"/> between them (zero = no break). Anything in progress is ended first.
    /// The choices are remembered for next time.
    /// </summary>
    public void StartSession(TimeSpan focus, int rounds, TimeSpan breakLength, string? intention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(focus, TimeSpan.Zero);
        var now = _time.GetUtcNow();
        Clear();

        _data = _data with
        {
            FocusLength = focus,
            Rounds = Math.Clamp(rounds, 1, FocusPlan.MaxRounds),
            BreakLength = breakLength < TimeSpan.Zero ? TimeSpan.Zero : breakLength,
            Round = 1,
        };
        Begin(FocusKind.Focus, focus, CleanIntention(intention), now);
    }

    /// <summary>Starts the break after a finished round (when rounds are left and a break is planned).</summary>
    public void StartBreak()
    {
        if (!IsBetweenRounds || IsBreak || !HasBreak)
        {
            return;
        }

        Begin(FocusKind.Break, _data.BreakLength, Intention, _time.GetUtcNow());
    }

    /// <summary>
    /// Starts the next focus round: after a break, instead of a break ("Skip break"), or as "One more" after
    /// the last round (which adds a round to the session).
    /// </summary>
    public void NextRound()
    {
        if (State != FocusState.Finished)
        {
            return;
        }

        var next = _data.Round + 1;
        _data = _data with { Round = next, Rounds = Math.Max(_data.Rounds, next) };
        Begin(FocusKind.Focus, _data.FocusLength, Intention, _time.GetUtcNow());
    }

    public void Pause()
    {
        if (State != FocusState.Running)
        {
            return;
        }

        _session = _session! with { Remaining = Remaining, DeadlineUtc = null };
        State = FocusState.Paused;
        Changed?.Invoke();
    }

    public void Resume()
    {
        if (State != FocusState.Paused)
        {
            return;
        }

        _session = _session! with { DeadlineUtc = _time.GetUtcNow() + _session.Remaining };
        State = FocusState.Running;
        Changed?.Invoke();
    }

    /// <summary>Ends the whole session early.</summary>
    public void Stop()
    {
        if (State is not (FocusState.Running or FocusState.Paused))
        {
            return;
        }

        Clear();
        _data = _data with { Round = 0 };
        Changed?.Invoke();
    }

    /// <summary>Closes a finished countdown and ends the session ("Done", or ✕ between rounds).</summary>
    public void Dismiss()
    {
        if (State != FocusState.Finished)
        {
            return;
        }

        _session = null;
        _data = _data with { Round = 0 };
        State = FocusState.Idle;
        Changed?.Invoke();
    }

    /// <summary>Finishes the countdown once its deadline has passed. Safe to call any time.</summary>
    public void Tick()
    {
        if (State != FocusState.Running || Remaining > TimeSpan.Zero)
        {
            return;
        }

        _session = _session! with { Remaining = TimeSpan.Zero, DeadlineUtc = null };
        State = FocusState.Finished;
        Changed?.Invoke();
        Finished?.Invoke(_session);
    }

    public static string? CleanIntention(string? text)
    {
        var trimmed = text?.ReplaceLineEndings(" ").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxIntentionLength ? trimmed : trimmed[..MaxIntentionLength].TrimEnd();
    }

    private void Begin(FocusKind kind, TimeSpan duration, string? intention, DateTimeOffset now)
    {
        _session = new FocusSession(kind, duration, duration, now + duration, intention);
        State = FocusState.Running;
        Changed?.Invoke();
    }

    /// <summary>Clears the current countdown without an event.</summary>
    private void Clear()
    {
        _session = null;
        State = FocusState.Idle;
    }

    private void Restore(FocusSession? saved)
    {
        if (saved is null || saved.Duration <= TimeSpan.Zero)
        {
            _data = _data with { Round = 0 };
            return;
        }

        if (saved.DeadlineUtc is { } deadline)
        {
            if (deadline > _time.GetUtcNow())
            {
                _session = saved;
                State = FocusState.Running;
                return;
            }

            // Ran out while Mithu was closed: mention it, but don't greet the user with a stale "take a break?".
            FinishedWhileAway = saved;
            _data = _data with { Round = 0 };
            return;
        }

        _session = saved with { Remaining = Clamp(saved.Remaining, saved.Duration) };
        State = FocusState.Paused;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan max) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value > max ? max : value;
}
