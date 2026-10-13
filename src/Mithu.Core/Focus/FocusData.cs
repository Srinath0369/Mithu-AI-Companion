namespace Mithu.Core.Focus;

/// <summary>Everything the focus timer persists between launches.</summary>
public sealed record FocusData
{
    /// <summary>Countdown that is running or paused (finished ones are not restored).</summary>
    public FocusSession? Active { get; init; }

    /// <summary>Length of each focus round, as last chosen.</summary>
    public TimeSpan FocusLength { get; init; } = TimeSpan.FromMinutes(FocusPlan.DefaultFocusMinutes);

    /// <summary>How many rounds the session has, as last chosen.</summary>
    public int Rounds { get; init; } = FocusPlan.DefaultRounds;

    /// <summary>Break between rounds, as last chosen (zero = no break).</summary>
    public TimeSpan BreakLength { get; init; } = TimeSpan.FromMinutes(FocusPlan.DefaultBreakMinutes);

    /// <summary>Round of the session in progress (1-based); 0 when no session is going.</summary>
    public int Round { get; init; }
}
