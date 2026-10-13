using Mithu.Core.Models;
using Mithu.Core.Mood;

namespace Mithu.Tests;

public sealed class ClickReactionMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

    private static readonly double ComboSec = ClickReactionMachine.ComboWindow.TotalSeconds;
    private static readonly double PunchSec = ClickReactionMachine.PunchDuration.TotalSeconds;
    private static readonly double AngrySec = ClickReactionMachine.AngryDuration.TotalSeconds;

    [Fact]
    public void Starts_calm()
    {
        var m = new ClickReactionMachine();

        Assert.Equal(ReactionPhase.Calm, m.Phase);
        Assert.Equal(FaceMood.Idle, m.Mood);
        Assert.Null(m.NextWakeUp);
    }

    [Fact]
    public void Single_click_smiles_then_returns_to_calm()
    {
        var m = new ClickReactionMachine();

        Assert.Equal(ClickReaction.Smile, m.Click(T0));
        Assert.Equal(ReactionPhase.Smiling, m.Phase);
        Assert.Equal(FaceMood.Happy, m.Mood);

        m.Tick(T0 + ClickReactionMachine.SmileDuration);

        Assert.Equal(ReactionPhase.Calm, m.Phase);
        Assert.Equal(0, m.PunchCount);
    }

    [Fact]
    public void Two_quick_clicks_smile_then_punch()
    {
        var m = new ClickReactionMachine();

        Assert.Equal(ClickReaction.Smile, m.Click(At(0)));
        Assert.Equal(ClickReaction.Punch, m.Click(At(0.3)));

        Assert.Equal(ReactionPhase.BeingPunched, m.Phase);
        Assert.Equal(FaceMood.Hurt, m.Mood);
        Assert.Equal(1, m.PunchCount);
    }

    [Fact]
    public void Slow_clicks_are_each_a_friendly_smile()
    {
        var m = new ClickReactionMachine();

        Assert.Equal(ClickReaction.Smile, m.Click(At(0)));
        Assert.Equal(ClickReaction.Smile, m.Click(At(ComboSec + 0.1)));
        Assert.Equal(ClickReaction.Smile, m.Click(At((2 * ComboSec) + 0.2)));
        Assert.Equal(0, m.PunchCount);
    }

    [Fact]
    public void Continuous_clicking_keeps_punching_and_counts()
    {
        var m = new ClickReactionMachine();

        m.Click(At(0));
        for (var i = 1; i <= 5; i++)
        {
            m.Tick(At(i * 0.25));
            Assert.Equal(ClickReaction.Punch, m.Click(At(i * 0.25)));
        }

        Assert.Equal(5, m.PunchCount);
        Assert.Equal(ReactionPhase.BeingPunched, m.Phase);
    }

    [Fact]
    public void Gets_angry_only_after_the_last_punch_finishes()
    {
        var m = new ClickReactionMachine();
        m.Click(At(0));
        m.Click(At(0.2)); // punch

        var punchEnd = 0.2 + Math.Max(PunchSec, ComboSec);

        m.Tick(At(punchEnd - 0.01));
        Assert.Equal(ReactionPhase.BeingPunched, m.Phase);

        m.Tick(At(punchEnd));
        Assert.Equal(ReactionPhase.Angry, m.Phase);
        Assert.Equal(FaceMood.Angry, m.Mood);
    }

    [Fact]
    public void Anger_lasts_a_few_seconds_then_calms_down()
    {
        var m = new ClickReactionMachine();
        m.Click(At(0));
        m.Click(At(0.2));
        var angryStart = 0.2 + Math.Max(PunchSec, ComboSec);
        m.Tick(At(angryStart));

        m.Tick(At(angryStart + AngrySec - 0.01));
        Assert.Equal(ReactionPhase.Angry, m.Phase);

        m.Tick(At(angryStart + AngrySec));
        Assert.Equal(ReactionPhase.Calm, m.Phase);
        Assert.Equal(0, m.PunchCount);
    }

    [Fact]
    public void Clicking_while_angry_punches_again_and_restarts_anger()
    {
        var m = new ClickReactionMachine();
        m.Click(At(0));
        m.Click(At(0.2));
        var angryStart = 0.2 + Math.Max(PunchSec, ComboSec);
        m.Tick(At(angryStart + 1)); // angry for 1 s

        Assert.Equal(ClickReaction.Punch, m.Click(At(angryStart + 1)));
        Assert.Equal(ReactionPhase.BeingPunched, m.Phase);
        Assert.Equal(2, m.PunchCount);

        // Full anger again after this punch.
        var secondAngry = angryStart + 1 + Math.Max(PunchSec, ComboSec);
        m.Tick(At(secondAngry));
        Assert.Equal(ReactionPhase.Angry, m.Phase);
        m.Tick(At(secondAngry + AngrySec - 0.01));
        Assert.Equal(ReactionPhase.Angry, m.Phase);
    }

    [Fact]
    public void Late_tick_skips_straight_to_calm()
    {
        // E.g. the window was hidden for a while: no stale anger.
        var m = new ClickReactionMachine();
        m.Click(At(0));
        m.Click(At(0.2));

        m.Tick(At(60));

        Assert.Equal(ReactionPhase.Calm, m.Phase);
    }

    [Fact]
    public void Phase_changes_are_reported_in_order()
    {
        var m = new ClickReactionMachine();
        var phases = new List<ReactionPhase>();
        m.PhaseChanged += phases.Add;

        m.Click(At(0));
        m.Click(At(0.2));
        m.Click(At(0.4)); // still punching: no duplicate event
        var angryStart = 0.4 + Math.Max(PunchSec, ComboSec);
        m.Tick(At(angryStart));
        m.Tick(At(angryStart + AngrySec));

        Assert.Equal([ReactionPhase.Smiling, ReactionPhase.BeingPunched, ReactionPhase.Angry, ReactionPhase.Calm], phases);
    }

    [Fact]
    public void Next_wake_up_points_at_the_next_transition()
    {
        var m = new ClickReactionMachine();
        m.Click(At(0));

        Assert.Equal(T0 + ClickReactionMachine.SmileDuration, m.NextWakeUp);
    }

    [Fact]
    public void Reset_returns_to_calm()
    {
        var m = new ClickReactionMachine();
        m.Click(At(0));
        m.Click(At(0.1));

        m.Reset();

        Assert.Equal(ReactionPhase.Calm, m.Phase);
        Assert.Equal(0, m.PunchCount);
        Assert.Equal(ClickReaction.Smile, m.Click(At(0.2))); // fresh start
    }

    [Fact]
    public void Timings_feel_right()
    {
        Assert.InRange(ClickReactionMachine.ComboWindow.TotalMilliseconds, 400, 800);
        Assert.InRange(ClickReactionMachine.AngryDuration.TotalSeconds, 1, 2);
    }
}
