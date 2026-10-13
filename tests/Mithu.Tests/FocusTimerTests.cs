using Microsoft.Extensions.Time.Testing;
using Mithu.Core.Focus;

namespace Mithu.Tests;

public sealed class FocusTimerTests
{
    private static readonly TimeSpan Min25 = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan Min5 = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));

    private FocusTimer NewTimer(FocusData? saved = null) => new(_time, saved);

    private static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);

    /// <summary>Lets the current countdown run out.</summary>
    private void RunOut(FocusTimer t)
    {
        _time.Advance(t.Remaining);
        t.Tick();
    }

    [Fact]
    public void Starts_idle()
    {
        var t = NewTimer();

        Assert.Equal(FocusState.Idle, t.State);
        Assert.Equal(TimeSpan.Zero, t.Remaining);
        Assert.Null(t.Deadline);
        Assert.Equal(0, t.Round);
    }

    [Fact]
    public void Counts_down_from_the_deadline()
    {
        var t = NewTimer();
        t.StartSession(Min25, 1, Min5, "Write report");

        _time.Advance(Min(10));

        Assert.Equal(FocusState.Running, t.State);
        Assert.Equal(Min(15), t.Remaining);
        Assert.Equal(0.4, t.Progress, 6);
        Assert.Equal(_time.GetUtcNow() + Min(15), t.Deadline);
        Assert.Equal("Write report", t.Intention);
        Assert.Equal(1, t.Round);
    }

    [Fact]
    public void Any_custom_length_works()
    {
        var t = NewTimer();
        t.StartSession(Min(37), 1, TimeSpan.Zero, null);

        Assert.Equal(Min(37), t.Remaining);
        Assert.Equal(Min(37), t.FocusLength);
    }

    [Fact]
    public void Pause_freezes_time_and_resume_sets_a_new_deadline()
    {
        var t = NewTimer();
        t.StartSession(Min25, 1, Min5, null);
        _time.Advance(Min(5));

        t.Pause();
        _time.Advance(Min(30)); // a long pause
        Assert.Equal(FocusState.Paused, t.State);
        Assert.Equal(Min(20), t.Remaining);
        Assert.Null(t.Deadline);

        t.Resume();
        Assert.Equal(_time.GetUtcNow() + Min(20), t.Deadline);
        _time.Advance(Min(20));
        t.Tick();
        Assert.Equal(FocusState.Finished, t.State);
    }

    [Fact]
    public void Finishes_exactly_at_the_deadline_and_reports_once()
    {
        var t = NewTimer();
        var finished = new List<FocusSession>();
        t.Finished += finished.Add;
        t.StartSession(Min25, 1, Min5, "Write report");

        _time.Advance(Min25 - TimeSpan.FromMilliseconds(1));
        t.Tick();
        Assert.Equal(FocusState.Running, t.State);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        t.Tick();
        t.Tick();

        Assert.Equal(FocusState.Finished, t.State);
        Assert.Equal(1, t.Progress);
        var session = Assert.Single(finished);
        Assert.Equal(FocusKind.Focus, session.Kind);
        Assert.Equal("Write report", session.Intention);
    }

    [Fact]
    public void Single_round_session_is_complete_when_it_finishes()
    {
        var t = NewTimer();
        t.StartSession(Min25, 1, Min5, null);
        RunOut(t);

        Assert.True(t.IsSessionComplete);
        Assert.False(t.IsBetweenRounds);

        t.StartBreak(); // no break after the last round
        Assert.Equal(FocusState.Finished, t.State);
    }

    [Fact]
    public void Runs_the_planned_rounds_with_breaks_between()
    {
        var t = NewTimer();
        t.StartSession(Min25, 3, Min5, "Write report");
        var kinds = new List<(FocusKind Kind, int Round)>();
        t.Finished += s => kinds.Add((s.Kind, t.Round));

        RunOut(t);               // round 1
        Assert.True(t.IsBetweenRounds);
        t.StartBreak();
        Assert.Equal(Min5, t.Remaining);
        RunOut(t);               // break
        Assert.True(t.IsBetweenRounds);
        t.NextRound();
        Assert.Equal(2, t.Round);
        Assert.Equal(Min25, t.Remaining);
        Assert.Equal("Write report", t.Intention);
        RunOut(t);               // round 2
        t.StartBreak();
        RunOut(t);               // break
        t.NextRound();
        RunOut(t);               // round 3

        Assert.True(t.IsSessionComplete);
        Assert.Equal(
            [(FocusKind.Focus, 1), (FocusKind.Break, 1), (FocusKind.Focus, 2), (FocusKind.Break, 2), (FocusKind.Focus, 3)],
            kinds);
    }

    [Fact]
    public void Skip_break_goes_straight_to_the_next_round()
    {
        var t = NewTimer();
        t.StartSession(Min25, 2, Min5, null);
        RunOut(t);

        t.NextRound();

        Assert.Equal(FocusKind.Focus, t.Kind);
        Assert.Equal(2, t.Round);
    }

    [Fact]
    public void No_break_planned_means_no_break_offered()
    {
        var t = NewTimer();
        t.StartSession(Min25, 2, TimeSpan.Zero, null);
        RunOut(t);

        t.StartBreak();

        Assert.False(t.HasBreak);
        Assert.Equal(FocusState.Finished, t.State);
        Assert.True(t.IsBetweenRounds);
    }

    [Fact]
    public void One_more_after_the_last_round_adds_a_round()
    {
        var t = NewTimer();
        t.StartSession(Min25, 1, Min5, null);
        RunOut(t);

        t.NextRound();

        Assert.Equal(2, t.Round);
        Assert.Equal(2, t.Rounds);
        Assert.Equal(FocusState.Running, t.State);
    }

    [Fact]
    public void Rounds_are_limited()
    {
        var t = NewTimer();
        t.StartSession(Min25, 99, Min5, null);
        Assert.Equal(FocusPlan.MaxRounds, t.Rounds);

        t.StartSession(Min25, 0, Min5, null);
        Assert.Equal(1, t.Rounds);
    }

    [Fact]
    public void Late_tick_after_sleep_still_finishes()
    {
        var t = NewTimer();
        t.StartSession(Min25, 1, Min5, null);

        _time.Advance(TimeSpan.FromHours(2)); // laptop slept
        t.Tick();

        Assert.Equal(FocusState.Finished, t.State);
        Assert.True(t.IsSessionComplete);
    }

    [Fact]
    public void Clock_moved_back_never_shows_more_than_the_full_length()
    {
        var t = NewTimer();
        t.StartSession(Min25, 1, Min5, null);

        _time.AdjustTime(_time.GetUtcNow() - TimeSpan.FromHours(1));

        Assert.Equal(Min25, t.Remaining);
        Assert.Equal(0, t.Progress);
    }

    [Fact]
    public void Break_after_the_last_round_is_not_offered()
    {
        var t = NewTimer();
        t.StartSession(Min25, 2, Min5, null);
        RunOut(t);
        t.StartBreak();
        RunOut(t);
        t.NextRound();
        RunOut(t);

        t.StartBreak();

        Assert.True(t.IsSessionComplete);
        Assert.Equal(FocusState.Finished, t.State);
    }

    [Fact]
    public void Stopping_ends_the_session()
    {
        var t = NewTimer();
        t.StartSession(Min25, 3, Min5, null);
        _time.Advance(Min(12));

        t.Stop();

        Assert.Equal(FocusState.Idle, t.State);
        Assert.Equal(0, t.Round);
        Assert.Null(t.Deadline);
    }

    [Fact]
    public void Starting_a_new_session_mid_round_replaces_it()
    {
        var t = NewTimer();
        t.StartSession(Min25, 1, Min5, null);
        _time.Advance(Min(5));

        t.StartSession(Min(45), 2, Min(10), null);

        Assert.Equal(Min(45), t.Remaining);
        Assert.Equal(1, t.Round);
    }

    [Fact]
    public void Dismiss_ends_the_session_without_starting_anything()
    {
        var t = NewTimer();
        t.StartSession(Min25, 3, Min5, null);
        RunOut(t);

        t.Dismiss();

        Assert.Equal(FocusState.Idle, t.State);
        Assert.Equal(0, t.Round);
    }

    [Fact]
    public void Remembers_the_last_choices()
    {
        var first = NewTimer();
        first.StartSession(Min(40), 3, Min(10), null);

        var t = NewTimer(first.Data);

        Assert.Equal(Min(40), t.FocusLength);
        Assert.Equal(3, t.Rounds);
        Assert.Equal(Min(10), t.BreakLength);
    }

    [Fact]
    public void Changed_is_raised_for_each_transition()
    {
        var t = NewTimer();
        var count = 0;
        t.Changed += () => count++;

        t.StartSession(Min25, 1, Min5, null); // 1
        t.Pause();                            // 2
        t.Pause();                            // ignored
        t.Resume();                           // 3
        RunOut(t);                            // 4
        t.Dismiss();                          // 5

        Assert.Equal(5, count);
    }

    [Fact]
    public void Running_round_continues_after_relaunch()
    {
        var first = NewTimer();
        first.StartSession(Min25, 3, Min5, "Write report");
        RunOut(first);
        first.NextRound(); // round 2
        _time.Advance(Min(10));
        var saved = first.Data;

        _time.Advance(Min(2)); // closed for two minutes
        var t = NewTimer(saved);

        Assert.Equal(FocusState.Running, t.State);
        Assert.Equal(Min(13), t.Remaining);
        Assert.Equal("Write report", t.Intention);
        Assert.Equal(2, t.Round);
        Assert.Equal(3, t.Rounds);
        Assert.Null(t.FinishedWhileAway);
    }

    [Fact]
    public void Paused_round_stays_paused_after_relaunch()
    {
        var first = NewTimer();
        first.StartSession(Min25, 1, Min5, null);
        _time.Advance(Min(10));
        first.Pause();

        _time.Advance(TimeSpan.FromHours(3));
        var t = NewTimer(first.Data);

        Assert.Equal(FocusState.Paused, t.State);
        Assert.Equal(Min(15), t.Remaining);
    }

    [Fact]
    public void Round_that_ran_out_while_closed_is_reported()
    {
        var first = NewTimer();
        first.StartSession(Min25, 2, Min5, "Write report");

        _time.Advance(TimeSpan.FromHours(1));
        var t = NewTimer(first.Data);

        Assert.Equal(FocusState.Idle, t.State);
        Assert.NotNull(t.FinishedWhileAway);
        Assert.Equal(0, t.Round);
        Assert.Null(t.Data.Active);
    }

    [Fact]
    public void Finished_state_is_not_persisted()
    {
        var t = NewTimer();
        t.StartSession(Min25, 1, Min5, null);
        RunOut(t);

        Assert.Null(t.Data.Active);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  Write report  ", "Write report")]
    [InlineData("line one\r\nline two", "line one line two")]
    public void Intention_is_cleaned(string? input, string? expected)
    {
        Assert.Equal(expected, FocusTimer.CleanIntention(input));
    }

    [Fact]
    public void Long_intention_is_shortened()
    {
        var cleaned = FocusTimer.CleanIntention(new string('a', 200));

        Assert.Equal(FocusTimer.MaxIntentionLength, cleaned!.Length);
    }

    [Fact]
    public void Zero_length_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewTimer().StartSession(TimeSpan.Zero, 1, Min5, null));
    }
}
