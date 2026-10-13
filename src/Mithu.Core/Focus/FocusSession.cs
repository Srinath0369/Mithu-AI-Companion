namespace Mithu.Core.Focus;

/// <summary>What a countdown is for.</summary>
public enum FocusKind
{
    Focus,
    Break,
}

/// <summary>Where the focus timer is in its flow.</summary>
public enum FocusState
{
    /// <summary>Nothing running.</summary>
    Idle,

    Running,

    Paused,

    /// <summary>
    /// A countdown ran out and waits for the user: a break or the next round when rounds are left,
    /// "done" after the last one. Nothing starts by itself.
    /// </summary>
    Finished,
}

/// <summary>
/// One countdown. Running while <see cref="DeadlineUtc"/> is set (time left = deadline − now, never counted ticks);
/// paused otherwise, with <see cref="Remaining"/> left.
/// </summary>
/// <param name="Intention">Optional "what are you focusing on?" text. User content: never log it.</param>
public sealed record FocusSession(
    FocusKind Kind,
    TimeSpan Duration,
    TimeSpan Remaining,
    DateTimeOffset? DeadlineUtc,
    string? Intention);

/// <summary>Choices and limits for a focus session (the user picks length, rounds and break).</summary>
public static class FocusPlan
{
    public const int DefaultFocusMinutes = 25;
    public const int MaxFocusMinutes = 180;

    public const int DefaultRounds = 1;
    public const int MaxRounds = 8;

    public const int DefaultBreakMinutes = 5;
    public const int MaxBreakMinutes = 30;

    /// <summary>Quick focus lengths offered in the panel (minutes); anything else is "Custom".</summary>
    public static IReadOnlyList<int> FocusChoices { get; } = [15, 25, 45, 60];

    /// <summary>Break lengths the stepper walks through (minutes); 0 = no break.</summary>
    public static IReadOnlyList<int> BreakChoices { get; } = [0, 1, 2, 3, 5, 10, 15, 20, 30];
}
