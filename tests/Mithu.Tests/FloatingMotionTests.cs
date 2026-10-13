using Mithu.Core.Motion;

namespace Mithu.Tests;

/// <summary>Floating mode: the face glides between the bubble and the bar with the island's width.</summary>
public sealed class FloatingMotionTests
{
    private static readonly IslandPose BubblePose = new(76, 60, 30, Header: 0, Panel: 0, NotchEyes: 0, Bubble: 1);
    private static readonly IslandPose CompactPose = new(372, 60, 30, Header: 1, Panel: 0, NotchEyes: 0, Bubble: 0);
    private static readonly IslandPose ExpandedPose = new(420, 356, 26, Header: 1, Panel: 1, NotchEyes: 0, Bubble: 0);

    private static IEnumerable<IslandFrame> Frames(IslandChoreography c)
    {
        for (var t = 0.0; t <= c.Duration + 0.02; t += 1 / 120.0)
        {
            yield return c.Sample(t);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Opening_from_the_bubble_glides_the_face_out(bool toExpanded)
    {
        var c = new IslandChoreography(BubblePose, toExpanded ? ExpandedPose : CompactPose);
        var frames = Frames(c).ToList();

        Assert.Equal(1, frames[0].Bubble);
        Assert.Equal(0, c.Final.Bubble);
        Assert.Contains(frames, f => f.Bubble is > 0.2 and < 0.8); // in between: no jump
        Assert.All(frames, f => Assert.InRange(f.Bubble, 0, 1));
    }

    [Fact]
    public void Closing_to_the_bubble_glides_the_face_back_without_a_jump()
    {
        var c = new IslandChoreography(CompactPose, BubblePose);
        var frames = Frames(c).ToList();

        Assert.Equal(0, frames[0].Bubble);
        Assert.Equal(1, c.Final.Bubble);
        var biggestStep = frames.Zip(frames.Skip(1), (a, b) => Math.Abs(b.Bubble - a.Bubble)).Max();
        Assert.True(biggestStep < 0.12, $"largest per-frame step {biggestStep:0.000}");
    }

    [Fact]
    public void Face_moves_together_with_the_width()
    {
        var c = new IslandChoreography(BubblePose, CompactPose);
        foreach (var f in Frames(c))
        {
            // Width progress (0 = bubble width, 1 = bar width) and bubble (1 = bubble) add up to ~1, overshoot aside.
            var widthProgress = (f.Width - BubblePose.Width) / (CompactPose.Width - BubblePose.Width);
            Assert.InRange(widthProgress + f.Bubble, 0.99, Math.Max(1.01, widthProgress + 0.01));
        }
    }

    [Fact]
    public void Docked_poses_stay_out_of_the_bubble()
    {
        var notch = new IslandPose(132, 24, 12, Header: 0, Panel: 0, NotchEyes: 1);
        var c = new IslandChoreography(notch, ExpandedPose with { Bubble = 0 });

        Assert.All(Frames(c), f => Assert.Equal(0, f.Bubble));
    }

    [Fact]
    public void Interrupting_a_transition_continues_from_the_current_face_position()
    {
        var opening = new IslandChoreography(BubblePose, ExpandedPose);
        var midway = opening.Sample(0.12);

        var closing = new IslandChoreography(midway.ToPose(), BubblePose);

        Assert.Equal(midway.Bubble, closing.Sample(0).Bubble, 6);
    }
}
