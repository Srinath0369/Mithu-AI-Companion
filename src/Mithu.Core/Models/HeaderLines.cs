namespace Mithu.Core.Models;

/// <summary>What's worth knowing right now, for the header line (all optional).</summary>
/// <param name="ReminderTitle">The next pending reminder.</param>
/// <param name="ReminderIn">How far away it is ("in 20 min"), when it isn't overdue.</param>
/// <param name="ReminderOverdue">It is already past due.</param>
/// <param name="ReminderAhead">How long until it fires (negative when overdue).</param>
/// <param name="OpenTodos">Tasks not ticked off.</param>
/// <param name="SinceLastOpen">How long Mithu was tucked away (null on the first open).</param>
public sealed record HeaderContext(
    string? ReminderTitle = null,
    string? ReminderIn = null,
    bool ReminderOverdue = false,
    TimeSpan? ReminderAhead = null,
    int OpenTodos = 0,
    TimeSpan? SinceLastOpen = null);

/// <summary>
/// The header's title each time the companion opens (owner, 2026-10-13: "it says good evening every time"). A greeting
/// once per part of the day; after that the most useful thing (an overdue or soon reminder, open to-dos); with
/// nothing useful, a friendly line that fits the moment, never the same one twice in a row. Pure; unit-tested.
/// </summary>
public sealed class HeaderLines(Random? random = null)
{
    /// <summary>A reminder this close counts as "coming up".</summary>
    public static readonly TimeSpan SoonWindow = TimeSpan.FromHours(3);

    /// <summary>Away at least this long: "Welcome back!".</summary>
    public static readonly TimeSpan LongAway = TimeSpan.FromHours(2);

    private static readonly string[] Friendly =
    [
        "Back again? 👋", "What's next?", "I'm all ears", "Nice to see you", "Let's get something done",
        "Ready when you are", "Hey there!", "What are we up to?",
    ];

    private readonly Random _random = random ?? Random.Shared;
    private (DateOnly Day, int Part)? _greeted;
    private string? _lastFriendly;

    /// <summary>The time-of-day greeting ("Good morning!").</summary>
    public static string Greeting(DateTimeOffset now) => PartOfDay(now) switch
    {
        0 => "Up late? I'm here.",
        1 => "Good morning!",
        2 => "Good afternoon!",
        3 => "Good evening!",
        _ => "Winding down?",
    };

    /// <summary>0 night (before 5), 1 morning, 2 afternoon (12–17), 3 evening (17–21), 4 late evening.</summary>
    public static int PartOfDay(DateTimeOffset now) => now.Hour switch
    {
        < 5 => 0,
        < 12 => 1,
        < 17 => 2,
        < 21 => 3,
        _ => 4,
    };

    /// <summary>The line for this opening.</summary>
    public string Next(DateTimeOffset now, HeaderContext context)
    {
        var part = (DateOnly.FromDateTime(now.DateTime), PartOfDay(now));
        if (_greeted != part)
        {
            _greeted = part;
            return Greeting(now);
        }

        if (context.ReminderTitle is { Length: > 0 } title)
        {
            if (context.ReminderOverdue)
            {
                return $"Overdue: {title}";
            }

            if (context.ReminderAhead is { } ahead && ahead <= SoonWindow && context.ReminderIn is { Length: > 0 } when)
            {
                return $"{title} · {when}";
            }
        }

        if (context.OpenTodos > 0)
        {
            return context.OpenTodos == 1 ? "1 to-do left" : $"{context.OpenTodos} to-dos left";
        }

        return FriendlyLine(now, context);
    }

    private string FriendlyLine(DateTimeOffset now, HeaderContext context)
    {
        // Lines that fit this moment come first; one of them is used when it isn't the line shown last time.
        var moment = new List<string>();
        if (context.SinceLastOpen is { } away && away >= LongAway)
        {
            moment.Add("Welcome back!");
        }

        if (now.Hour >= 23 || now.Hour < 5)
        {
            moment.Add("Late night, huh?");
        }

        if (now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 12)
        {
            moment.Add("Happy Friday!");
        }

        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            moment.Add("Weekend mode 😎");
        }

        if (now.DayOfWeek == DayOfWeek.Monday && now.Hour < 12)
        {
            moment.Add("New week, fresh start");
        }

        var fitting = moment.Where(l => l != _lastFriendly).ToList();
        var pool = fitting.Count > 0 ? fitting : Friendly.Where(l => l != _lastFriendly).ToList();
        var line = pool[_random.Next(pool.Count)];
        _lastFriendly = line;
        return line;
    }
}
