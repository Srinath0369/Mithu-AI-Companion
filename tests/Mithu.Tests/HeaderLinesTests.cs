using Mithu.Core.Models;

namespace Mithu.Tests;

public class HeaderLinesTests
{
    // Tuesday 13 Oct 2026, local time (+05:30).
    private static DateTimeOffset At(int hour, int minute = 0, int day = 13) => new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void Greets_once_per_part_of_the_day()
    {
        var lines = new HeaderLines(new Random(1));
        Assert.Equal("Good evening!", lines.Next(At(18), new HeaderContext()));
        Assert.NotEqual("Good evening!", lines.Next(At(18, 5), new HeaderContext()));
        Assert.NotEqual("Good evening!", lines.Next(At(20, 30), new HeaderContext()));
        Assert.Equal("Winding down?", lines.Next(At(21, 10), new HeaderContext())); // a new part of the day
        Assert.Equal("Good morning!", lines.Next(At(8, 0, day: 14), new HeaderContext())); // and a new day
    }

    [Fact]
    public void After_the_greeting_the_most_useful_thing_shows()
    {
        var lines = new HeaderLines(new Random(1));
        lines.Next(At(10), new HeaderContext());

        Assert.Equal("Overdue: Call Mom", lines.Next(At(10, 1), new HeaderContext("Call Mom", ReminderOverdue: true, OpenTodos: 4)));
        Assert.Equal("Call Mom · in 20 min",
            lines.Next(At(10, 2), new HeaderContext("Call Mom", "in 20 min", ReminderAhead: TimeSpan.FromMinutes(20), OpenTodos: 4)));

        // A reminder far away doesn't take the header; open to-dos do.
        Assert.Equal("4 to-dos left",
            lines.Next(At(10, 3), new HeaderContext("Dentist", "tomorrow", ReminderAhead: TimeSpan.FromHours(20), OpenTodos: 4)));
        Assert.Equal("1 to-do left", lines.Next(At(10, 4), new HeaderContext(OpenTodos: 1)));
    }

    [Fact]
    public void Friendly_lines_never_repeat_twice_in_a_row()
    {
        var lines = new HeaderLines(new Random(7));
        lines.Next(At(14), new HeaderContext());
        string? previous = null;
        for (var i = 0; i < 40; i++)
        {
            var line = lines.Next(At(14, i % 50), new HeaderContext());
            Assert.NotEqual(previous, line);
            previous = line;
        }
    }

    [Fact]
    public void Friendly_lines_fit_the_moment()
    {
        var lines = new HeaderLines(new Random(3));
        lines.Next(At(15), new HeaderContext());
        Assert.Equal("Welcome back!", lines.Next(At(15, 30), new HeaderContext(SinceLastOpen: TimeSpan.FromHours(3))));

        var friday = new HeaderLines(new Random(3));
        var fridayNoon = new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.FromHours(5.5));
        friday.Next(fridayNoon, new HeaderContext());
        Assert.Equal("Happy Friday!", friday.Next(fridayNoon.AddMinutes(1), new HeaderContext()));

        var night = new HeaderLines(new Random(3));
        night.Next(At(23, 30), new HeaderContext());
        Assert.Equal("Late night, huh?", night.Next(At(23, 40), new HeaderContext()));
    }
}
