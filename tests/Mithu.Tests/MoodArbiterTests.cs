using Mithu.Core.Models;
using Mithu.Core.Mood;

namespace Mithu.Tests;

public sealed class MoodArbiterTests
{
    [Theory]
    [InlineData(FaceMood.Idle, false, null, FaceMood.Idle)]
    [InlineData(FaceMood.Idle, true, null, FaceMood.Hover)]
    [InlineData(FaceMood.Thinking, true, null, FaceMood.Thinking)]   // Thinking > Hover
    [InlineData(FaceMood.Listening, true, null, FaceMood.Listening)] // Listening > Hover
    [InlineData(FaceMood.Thinking, false, FaceMood.Happy, FaceMood.Happy)]       // Confirmation > Thinking
    [InlineData(FaceMood.Listening, true, FaceMood.Concerned, FaceMood.Concerned)] // Error > Listening
    [InlineData(FaceMood.Sleepy, true, null, FaceMood.Hover)]        // Hover wakes the face
    [InlineData(FaceMood.Sleepy, false, null, FaceMood.Sleepy)]
    public void Resolve_follows_priority_rules(FaceMood baseMood, bool hover, FaceMood? transient, FaceMood expected)
    {
        Assert.Equal(expected, MoodArbiter.Resolve(baseMood, hover, transient));
    }

    [Fact]
    public void Transient_reverts_to_previous_state_when_cleared()
    {
        var arbiter = new MoodArbiter();
        arbiter.SetBase(FaceMood.Thinking);

        arbiter.PushTransient(FaceMood.Happy);
        Assert.Equal(FaceMood.Happy, arbiter.Current);

        arbiter.ClearTransient();
        Assert.Equal(FaceMood.Thinking, arbiter.Current);
    }

    [Fact]
    public void Changed_fires_only_on_real_changes()
    {
        var arbiter = new MoodArbiter();
        var changes = new List<FaceMood>();
        arbiter.Changed += changes.Add;

        arbiter.SetBase(FaceMood.Idle);  // no change
        arbiter.SetHover(true);          // Hover
        arbiter.SetHover(true);          // no change
        arbiter.SetBase(FaceMood.Thinking); // Thinking
        arbiter.SetHover(false);         // still Thinking: no change

        Assert.Equal([FaceMood.Hover, FaceMood.Thinking], changes);
    }

    [Fact]
    public void Waiting_on_the_user_listens_until_every_question_is_answered()
    {
        var arbiter = new MoodArbiter();
        arbiter.SetBase(FaceMood.Focused);

        arbiter.SetWaiting("reminder", true);
        arbiter.SetWaiting("claude", true);
        Assert.Equal(FaceMood.Listening, arbiter.Current); // over a focus round too

        arbiter.PushTransient(FaceMood.Happy);
        Assert.Equal(FaceMood.Happy, arbiter.Current);     // a reaction still shows
        arbiter.ClearTransient();

        arbiter.SetWaiting("reminder", false);
        Assert.Equal(FaceMood.Listening, arbiter.Current); // Claude Code still asks
        arbiter.SetWaiting("claude", false);
        Assert.Equal(FaceMood.Focused, arbiter.Current);
    }

    [Fact]
    public void Sleepy_only_takes_an_idle_face_and_hover_wakes_it()
    {
        var arbiter = new MoodArbiter();
        arbiter.SetSleepy(true);
        Assert.Equal(FaceMood.Sleepy, arbiter.Current);

        arbiter.SetHover(true);
        Assert.Equal(FaceMood.Hover, arbiter.Current);
        arbiter.SetHover(false);

        arbiter.SetBase(FaceMood.Focused);                  // a focus round keeps going while you read
        Assert.Equal(FaceMood.Focused, arbiter.Current);
        arbiter.SetBase(FaceMood.Idle);
        arbiter.SetWaiting("reminder", true);               // a reminder wakes it
        Assert.Equal(FaceMood.Listening, arbiter.Current);
        arbiter.SetWaiting("reminder", false);

        arbiter.SetSleepy(false);
        Assert.Equal(FaceMood.Idle, arbiter.Current);
    }

    [Theory]
    [InlineData(9, 14, false)]   // 9 min away in the afternoon: awake
    [InlineData(10, 14, true)]   // 10 min: dozing
    [InlineData(2, 23, true)]    // late at night: 2 min is enough
    [InlineData(2, 3, true)]
    [InlineData(1, 23, false)]
    [InlineData(2, 5, false)]    // 5 am: day rules again
    public void Mithu_dozes_when_you_are_away(int idleMinutes, int hour, bool sleepy) =>
        Assert.Equal(sleepy, SleepPolicy.ShouldSleep(TimeSpan.FromMinutes(idleMinutes), TimeSpan.FromHours(hour)));

    [Fact]
    public void Hover_cannot_be_set_as_base_mood()
    {
        Assert.Throws<ArgumentException>(() => new MoodArbiter().SetBase(FaceMood.Hover));
    }
}
