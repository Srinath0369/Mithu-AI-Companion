using Mithu.Core.Reminders;

namespace Mithu.Tests;

public class RepeatRuleTests
{
    // 2026: clocks go forward on Mar 8 (2:00 → 3:00) and back on Nov 1 (2:00 → 1:00) in New York.
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_keeps_the_wall_clock_across_spring_forward()
    {
        var start = new DateTime(2026, 3, 7, 9, 0, 0);
        Assert.Equal(Utc(2026, 3, 7, 14), RepeatRule.Occurrence(start, NewYork, Repeat.Daily, 0)); // EST −5
        Assert.Equal(Utc(2026, 3, 8, 13), RepeatRule.Next(start, NewYork, Repeat.Daily, Utc(2026, 3, 7, 14))); // EDT −4
    }

    [Fact]
    public void Daily_keeps_the_wall_clock_across_fall_back()
    {
        var start = new DateTime(2026, 10, 31, 9, 0, 0);
        Assert.Equal(Utc(2026, 11, 1, 14), RepeatRule.Occurrence(start, NewYork, Repeat.Daily, 1));
    }

    [Fact]
    public void A_time_skipped_by_spring_forward_moves_forward_by_the_jump()
    {
        // 2:30 doesn't exist on Mar 8 → 3:30 EDT.
        Assert.Equal(Utc(2026, 3, 8, 7, 30), RepeatRule.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), NewYork));
    }

    [Fact]
    public void A_time_that_happens_twice_uses_the_first()
    {
        // 1:30 happens twice on Nov 1: first in EDT (−4).
        Assert.Equal(Utc(2026, 11, 1, 5, 30), RepeatRule.ToUtc(new DateTime(2026, 11, 1, 1, 30, 0), NewYork));
    }

    [Fact]
    public void Monthly_on_the_31st_lands_on_the_last_day_and_comes_back()
    {
        var start = new DateTime(2026, 1, 31, 9, 0, 0);
        var dates = Enumerable.Range(0, 4)
            .Select(k => TimeZoneInfo.ConvertTime(RepeatRule.Occurrence(start, NewYork, Repeat.Monthly, k), NewYork).Date)
            .ToArray();
        Assert.Equal([new DateTime(2026, 1, 31), new DateTime(2026, 2, 28), new DateTime(2026, 3, 31), new DateTime(2026, 4, 30)], dates);
    }

    [Fact]
    public void Weekdays_skip_the_weekend()
    {
        var friday = new DateTime(2026, 10, 9, 9, 0, 0);
        var days = Enumerable.Range(0, 4)
            .Select(k => TimeZoneInfo.ConvertTime(RepeatRule.Occurrence(friday, NewYork, Repeat.Weekdays, k), NewYork).DayOfWeek)
            .ToArray();
        Assert.Equal([DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday], days);
    }

    [Fact]
    public void Weekdays_starting_on_a_saturday_begin_on_monday()
    {
        var saturday = new DateTime(2026, 10, 10, 9, 0, 0);
        var first = TimeZoneInfo.ConvertTime(RepeatRule.Occurrence(saturday, NewYork, Repeat.Weekdays, 0), NewYork);
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0), first.DateTime);
    }

    [Fact]
    public void Hours_step_real_time_across_dst()
    {
        var start = new DateTime(2026, 3, 8, 0, 0, 0); // 05:00Z
        Assert.Equal(Utc(2026, 3, 8, 9), RepeatRule.Occurrence(start, NewYork, Repeat.Custom(2, RepeatUnit.Hours), 2));
    }

    [Fact]
    public void Next_after_a_long_gap_is_exact()
    {
        var start = new DateTime(2020, 1, 1, 9, 0, 0);
        var next = RepeatRule.Next(start, NewYork, Repeat.Daily, Utc(2026, 10, 12, 15));
        Assert.Equal(Utc(2026, 10, 13, 13), next); // 9:00 EDT next day
        Assert.Equal(Utc(2026, 10, 12, 13), RepeatRule.Latest(start, NewYork, Repeat.Daily, Utc(2026, 10, 12, 15)));
    }

    [Theory]
    [InlineData(RepeatKind.Weekly, 1)]
    [InlineData(RepeatKind.Monthly, 1)]
    [InlineData(RepeatKind.Weekdays, 1)]
    public void Next_is_strictly_after_and_latest_is_not_after(RepeatKind kind, int every)
    {
        var repeat = new Repeat(kind, every);
        var start = new DateTime(2024, 5, 31, 8, 15, 0);
        for (var day = 0; day < 400; day += 13)
        {
            var at = Utc(2026, 1, 1, 12).AddDays(day);
            var next = RepeatRule.Next(start, NewYork, repeat, at);
            var latest = RepeatRule.Latest(start, NewYork, repeat, at);
            Assert.True(next > at);
            Assert.True(latest <= at);
            Assert.Equal(next, RepeatRule.Next(start, NewYork, repeat, latest));
        }
    }

    [Fact]
    public void Describe_reads_naturally()
    {
        Assert.Equal("Daily", Repeat.Daily.Describe());
        Assert.Equal("Every 3 days", Repeat.Custom(3, RepeatUnit.Days).Describe());
        Assert.Equal("Every hour", Repeat.Custom(1, RepeatUnit.Hours).Describe());
        Assert.Equal(Repeat.MinMinutes, Repeat.Custom(1, RepeatUnit.Minutes).Every);
        Assert.Equal(string.Empty, Repeat.None.Describe());
    }
}

public class ReminderModelTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 19, 0, 0, TimeSpan.Zero); // Mon 15:00 EDT

    private static Reminder Make(DateTime startLocal, Repeat? repeat = null) =>
        Reminder.Create("Call Mom", "", startLocal, Zone, repeat ?? Repeat.None, ReminderPriority.Normal, Now);

    [Fact]
    public void A_repeating_reminder_whose_start_passed_begins_at_its_next_occurrence()
    {
        var r = Make(new DateTime(2026, 10, 12, 9, 0, 0), Repeat.Daily);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 13, 0, 0, TimeSpan.Zero), r.DueUtc);
    }

    [Fact]
    public void Weekdays_set_on_a_sunday_start_on_monday()
    {
        var sundayEvening = new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero); // Sun 11 Oct 20:00 EDT
        var r = Reminder.Create("Standup", "", new DateTime(2026, 10, 11, 21, 30, 0), Zone, Repeat.Weekdays, ReminderPriority.Normal, sundayEvening);
        Assert.Equal(new DateTime(2026, 10, 12, 21, 30, 0), TimeZoneInfo.ConvertTime(r.DueUtc, Zone).DateTime);
    }

    [Fact]
    public void Completing_a_one_off_finishes_it()
    {
        var (updated, history) = Make(new DateTime(2026, 10, 12, 18, 0, 0)).Finish(ReminderStatus.Completed, Zone, Now);
        Assert.Equal(ReminderStatus.Completed, updated.Status);
        Assert.Equal(Now, updated.DoneUtc);
        Assert.Null(history);
    }

    [Fact]
    public void Completing_a_daily_reminder_moves_it_to_tomorrow_and_keeps_history()
    {
        var r = Make(new DateTime(2026, 10, 12, 14, 0, 0), Repeat.Daily) with { DueUtc = new DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero) };
        var (updated, history) = r.Finish(ReminderStatus.Completed, Zone, Now);

        Assert.True(updated.IsPending);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.Zero), updated.DueUtc);
        Assert.NotNull(history);
        Assert.Equal(ReminderStatus.Completed, history.Status);
        Assert.False(history.Repeat.IsRepeating);
        Assert.Equal(r.DueUtc, history.DueUtc);
        Assert.NotEqual(r.Id, history.Id);
    }

    [Fact]
    public void Completing_early_skips_to_the_occurrence_after_the_current_one()
    {
        var r = Make(new DateTime(2026, 10, 12, 18, 0, 0), Repeat.Daily); // due today 18:00, now 15:00
        var (updated, _) = r.Finish(ReminderStatus.Skipped, Zone, Now);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 22, 0, 0, TimeSpan.Zero), updated.DueUtc);
    }

    [Fact]
    public void Snooze_moves_the_alert_and_alerts_again_once()
    {
        var r = Make(new DateTime(2026, 10, 12, 14, 0, 0)); // due an hour ago
        Assert.True(r.NeedsAlert(Now));

        var notified = r with { NotifiedUtc = Now };
        Assert.False(notified.NeedsAlert(Now));
        Assert.True(notified.IsOverdue(Now));

        var snoozed = notified.Snooze(TimeSpan.FromMinutes(10), Now);
        Assert.False(snoozed.IsOverdue(Now));
        Assert.True(snoozed.NeedsAlert(Now.AddMinutes(10)));
        Assert.Equal(r.DueUtc, snoozed.DueUtc);
    }

    [Fact]
    public void Catch_up_jumps_to_the_latest_missed_occurrence()
    {
        var r = Make(new DateTime(2026, 10, 9, 9, 0, 0), Repeat.Daily) with
        {
            DueUtc = new DateTimeOffset(2026, 10, 9, 13, 0, 0, TimeSpan.Zero),
            NotifiedUtc = new DateTimeOffset(2026, 10, 9, 13, 0, 0, TimeSpan.Zero),
        };

        var caught = r.CatchUp(Zone, Now);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 13, 0, 0, TimeSpan.Zero), caught.DueUtc);
        Assert.True(caught.NeedsAlert(Now));
        Assert.Same(caught, caught.CatchUp(Zone, Now));
    }

    [Fact]
    public void Moving_time_zone_keeps_the_wall_clock_time()
    {
        var r = Make(new DateTime(2026, 10, 13, 9, 0, 0));
        var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
        var moved = r.Rezone(london, Now);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), moved.DueUtc); // 9:00 BST
        Assert.Equal(london.Id, moved.TimeZoneId);
    }

    [Fact]
    public void Duplicate_is_a_fresh_pending_copy()
    {
        var r = Make(new DateTime(2026, 10, 13, 9, 0, 0)) with { Status = ReminderStatus.Completed };
        var copy = r.Duplicate(Zone, Now);
        Assert.NotEqual(r.Id, copy.Id);
        Assert.Equal("Call Mom (copy)", copy.Title);
        Assert.True(copy.IsPending);
        Assert.Equal(r.DueUtc, copy.DueUtc);
    }
}

public class ReminderParserTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 15, 0, 0); // Monday 3 PM

    private static ParsedReminder P(string text) => ReminderParser.Parse(text, Now);

    [Fact]
    public void The_classic()
    {
        var p = P("Remind me to call Mom tomorrow at 6 PM");
        Assert.Equal("Call Mom", p.Title);
        Assert.Equal(new DateTime(2026, 10, 13, 18, 0, 0), p.WhenLocal);
        Assert.False(p.Repeat.IsRepeating);
        Assert.Equal(ReminderPriority.Normal, p.Priority);
    }

    [Theory]
    [InlineData("remind me in 20 minutes to stretch", "Stretch", "2026-10-12 15:20")]
    [InlineData("check the oven in half an hour", "Check the oven", "2026-10-12 15:30")]
    [InlineData("call Bob in an hour and 15 min", "Call Bob", "2026-10-12 16:15")]
    [InlineData("call dentist at 8", "Call dentist", "2026-10-12 20:00")] // 8 AM has passed → 8 PM
    [InlineData("call dentist at 4", "Call dentist", "2026-10-12 16:00")]
    [InlineData("tomorrow at 8 standup", "Standup", "2026-10-13 08:00")] // another day: 7–11 → morning
    [InlineData("tomorrow at 3 pick up kids", "Pick up kids", "2026-10-13 15:00")] // 1–6 → afternoon
    [InlineData("call Bob at 18:30", "Call Bob", "2026-10-12 18:30")]
    [InlineData("call Bob at 9:15 am", "Call Bob", "2026-10-13 09:15")] // passed today → tomorrow
    [InlineData("Reminder: dinner tonight", "Dinner", "2026-10-12 20:00")]
    [InlineData("dinner at 7 tonight", "Dinner", "2026-10-12 19:00")]
    [InlineData("water plants this evening", "Water plants", "2026-10-12 18:00")]
    [InlineData("lunch with Ana at noon tomorrow", "Lunch with Ana", "2026-10-13 12:00")]
    [InlineData("remind me to email Sam next monday", "Email Sam", "2026-10-19 09:00")]
    [InlineData("meeting on friday at 3pm", "Meeting", "2026-10-16 15:00")]
    [InlineData("buy gifts on 25 dec", "Buy gifts", "2026-12-25 09:00")]
    [InlineData("pay bill March 3", "Pay bill", "2027-03-03 09:00")]
    [InlineData("don't forget to renew passport the day after tomorrow", "Renew passport", "2026-10-14 09:00")]
    public void Understands_times(string text, string title, string when)
    {
        var p = P(text);
        Assert.Equal(title, p.Title);
        Assert.Equal(DateTime.Parse(when, System.Globalization.CultureInfo.InvariantCulture), p.WhenLocal);
    }

    [Fact]
    public void In_n_minutes_is_exact_to_the_second()
    {
        var now = new DateTime(2026, 10, 12, 15, 0, 37);
        Assert.Equal(now.AddMinutes(1), ReminderParser.Parse("stretch in 1 minute", now).WhenLocal);
        Assert.Equal(new DateTime(2026, 10, 12, 18, 0, 0), ReminderParser.Parse("call at 6pm", now).WhenLocal);
    }

    [Fact]
    public void Repeats()
    {
        var rent = P("pay rent on the 1st every month");
        Assert.Equal("Pay rent", rent.Title);
        Assert.Equal(RepeatKind.Monthly, rent.Repeat.Kind);
        Assert.Equal(new DateTime(2026, 11, 1, 9, 0, 0), rent.WhenLocal);

        var water = P("drink water every 2 hours");
        Assert.Equal("Drink water", water.Title);
        Assert.Equal(Repeat.Custom(2, RepeatUnit.Hours), water.Repeat);
        Assert.Equal(new DateTime(2026, 10, 12, 17, 0, 0), water.WhenLocal);

        var standup = P("standup weekdays at 9:30");
        Assert.Equal(RepeatKind.Weekdays, standup.Repeat.Kind);
        Assert.Equal("Standup", standup.Title);
        Assert.Equal(new TimeSpan(9, 30, 0), standup.WhenLocal!.Value.TimeOfDay); // a habit: morning, though 9:30 AM has passed

        Assert.Equal(new TimeSpan(15, 0, 0), P("walk the dog every day at 3").WhenLocal!.Value.TimeOfDay);

        var gym = P("gym every monday at 7am");
        Assert.Equal(RepeatKind.Weekly, gym.Repeat.Kind);
        Assert.Equal(new DateTime(2026, 10, 19, 7, 0, 0), gym.WhenLocal);

        var vitamins = P("take vitamins every morning");
        Assert.Equal(RepeatKind.Daily, vitamins.Repeat.Kind);
        Assert.Equal("Take vitamins", vitamins.Title);
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0), vitamins.WhenLocal); // passed: the reminder moves to the next one

        Assert.Equal(Repeat.Custom(2, RepeatUnit.Days), P("water cactus every other day").Repeat);
        Assert.Equal(RepeatKind.Daily, P("journal daily at 9pm").Repeat.Kind);
    }

    [Fact]
    public void Priorities()
    {
        var urgent = P("urgent: send invoice in 1 hour");
        Assert.Equal(ReminderPriority.Urgent, urgent.Priority);
        Assert.Equal("Send invoice", urgent.Title);
        Assert.Equal(new DateTime(2026, 10, 12, 16, 0, 0), urgent.WhenLocal);

        Assert.Equal(ReminderPriority.Important, P("important: call the bank tomorrow").Priority);
    }

    [Fact]
    public void Without_a_time_only_the_title_is_set()
    {
        var p = P("remind me to buy milk");
        Assert.Equal("Buy milk", p.Title);
        Assert.Null(p.WhenLocal);
    }

    [Fact]
    public void Ordinary_words_are_not_read_as_days()
    {
        var p = P("enjoy the sun");
        Assert.Equal("Enjoy the sun", p.Title);
        Assert.Null(p.WhenLocal);
    }

    [Theory]
    [InlineData("6pm", 18, 0)]
    [InlineData("6:30 PM", 18, 30)]
    [InlineData("12 am", 0, 0)]
    [InlineData("12pm", 12, 0)]
    [InlineData("18:30", 18, 30)]
    [InlineData("7.15", 7, 15)]
    [InlineData(" 9 ", 9, 0)]
    [InlineData("9a", 9, 0)]
    public void Reads_typed_times(string text, int hour, int minute)
    {
        Assert.True(ReminderParser.TryParseClock(text, out var time));
        Assert.Equal(new TimeSpan(hour, minute, 0), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("25:00")]
    [InlineData("13pm")]
    [InlineData("6:75")]
    [InlineData("soon")]
    public void Rejects_bad_times(string text) => Assert.False(ReminderParser.TryParseClock(text, out _));

    [Theory]
    [InlineData("20", 20)]
    [InlineData("45 min", 45)]
    [InlineData("2h", 120)]
    [InlineData("1h 30m", 90)]
    [InlineData(" 90 minutes ", 90)]
    [InlineData("1 hour", 60)]
    public void Reads_snooze_lengths(string text, int minutes)
    {
        Assert.True(ReminderParser.TryParseDuration(text, out var d));
        Assert.Equal(TimeSpan.FromMinutes(minutes), d);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("25h")]
    [InlineData("soon")]
    [InlineData("20m 10m")]
    public void Rejects_bad_snooze_lengths(string text) => Assert.False(ReminderParser.TryParseDuration(text, out _));

    [Fact]
    public void Knows_when_text_is_a_reminder()
    {
        Assert.True(ReminderParser.LooksLikeReminder("Remind me to call Mom"));
        Assert.True(ReminderParser.LooksLikeReminder("reminder: pay rent"));
        Assert.True(ReminderParser.LooksLikeReminder("Don't forget to water plants"));
        Assert.False(ReminderParser.LooksLikeReminder("Reminders are great"));
        Assert.False(ReminderParser.LooksLikeReminder("Shopping list: milk"));
    }
}

public class ReminderListTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 19, 0, 0, TimeSpan.Zero); // Mon 15:00 EDT

    private static Reminder At(string title, int hoursFromNow, ReminderPriority priority = ReminderPriority.Normal, string notes = "")
    {
        var local = TimeZoneInfo.ConvertTime(Now.AddHours(hoursFromNow), Zone).DateTime;
        return Reminder.Create(title, notes, local, Zone, Repeat.None, priority, Now);
    }

    private static readonly Reminder[] All =
    [
        At("Soon", 1),
        At("Later", 30, ReminderPriority.Important),
        At("Late normal", -3),
        At("Late urgent", -1, ReminderPriority.Urgent),
        At("Finished", -5, notes: "groceries") with { Status = ReminderStatus.Completed, DoneUtc = Now.AddHours(-4) },
    ];

    private static string[] Titles(ReminderFilter filter) =>
        ReminderList.Apply(All, filter, Now, Zone).Select(r => r.Title).ToArray();

    [Fact]
    public void Views_and_order()
    {
        Assert.Equal(["Soon", "Later"], Titles(new(ReminderView.Upcoming)));
        Assert.Equal(["Late urgent", "Late normal"], Titles(new(ReminderView.Overdue)));
        Assert.Equal(["Finished"], Titles(new(ReminderView.Done)));
    }

    [Fact]
    public void Filters()
    {
        Assert.Equal(["Soon"], Titles(new(ReminderView.Upcoming, Date: ReminderDateFilter.Today)));
        Assert.Equal(["Later"], Titles(new(ReminderView.Upcoming, Date: ReminderDateFilter.Tomorrow)));
        Assert.Equal(["Later"], Titles(new(ReminderView.Upcoming, Priority: ReminderPriority.Important)));
        Assert.Equal(["Finished"], Titles(new(ReminderView.Done, Search: "grocer")));
        Assert.Empty(Titles(new(ReminderView.Overdue, Search: "soon")));
    }

    [Fact]
    public void Next_up_prefers_overdue()
    {
        Assert.Equal("Late urgent", ReminderList.NextUp(All, Now)!.Title);
        Assert.Equal(Now.AddHours(1), ReminderList.NextAlert(All, Now));
        Assert.Equal(2, ReminderList.Count(All, ReminderView.Overdue, Now));
    }

    [Fact]
    public void Friendly_text()
    {
        var us = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("Today 4:00 PM", ReminderText.When(Now.AddHours(1), Now, Zone, us));
        Assert.Equal("Tomorrow 9:00 PM", ReminderText.When(Now.AddHours(30), Now, Zone, us));
        Assert.Equal("Fri 3:00 PM", ReminderText.When(Now.AddDays(4), Now, Zone, us));
        Assert.Equal("in 1 h", ReminderText.Relative(Now.AddHours(1), Now));
        Assert.Equal("5 min ago", ReminderText.Relative(Now.AddMinutes(-5), Now));
        Assert.Equal("now", ReminderText.Relative(Now.AddSeconds(20), Now));
    }
}
