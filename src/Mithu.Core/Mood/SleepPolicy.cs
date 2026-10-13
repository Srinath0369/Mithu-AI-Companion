namespace Mithu.Core.Mood;

/// <summary>
/// When Mithu dozes off: after 10 minutes without mouse or keyboard input, or after 2 minutes late at night
/// (11 pm – 5 am). Any input wakes it. Pure; unit-tested.
/// </summary>
public static class SleepPolicy
{
    public static readonly TimeSpan DayIdle = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan NightIdle = TimeSpan.FromMinutes(2);

    public static bool IsNight(TimeSpan timeOfDay) => timeOfDay >= TimeSpan.FromHours(23) || timeOfDay < TimeSpan.FromHours(5);

    public static bool ShouldSleep(TimeSpan idle, TimeSpan localTimeOfDay) =>
        idle >= (IsNight(localTimeOfDay) ? NightIdle : DayIdle);
}
