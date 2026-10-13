using Mithu.Core.Motion;

namespace Mithu.Tests;

public sealed class SpringTests
{
    [Theory]
    [InlineData(0.42, 0.78)]
    [InlineData(0.30, 1.0)]
    [InlineData(0.22, 0.85)]
    public void Starts_at_zero_and_settles_at_one(double response, double damping)
    {
        var spring = new Spring(response, damping);

        Assert.Equal(0, spring.Value(0));
        Assert.Equal(0, spring.Value(-1));
        Assert.InRange(spring.Value(spring.SettleTime), 1 - Spring.SettleTolerance - 1e-6, 1 + Spring.SettleTolerance + 1e-6);
        Assert.InRange(spring.Value(spring.SettleTime * 2), 0.9999, 1.0001);
    }

    [Fact]
    public void Open_spring_has_a_small_friendly_overshoot()
    {
        // Enough to feel alive, never wobbly.
        Assert.InRange(Spring.Open.PeakValue, 1.01, 1.06);
    }

    [Fact]
    public void Close_spring_never_overshoots()
    {
        // Critically damped approaches 1 from below and never passes it.
        for (var t = 0.0; t < 3; t += 0.001)
        {
            Assert.True(Spring.Close.Value(t) <= 1.0, $"overshot at t={t}");
        }

        Assert.InRange(Spring.Close.PeakValue, 1 - (2 * Spring.SettleTolerance), 1.0);
    }

    [Fact]
    public void Critically_damped_spring_is_monotonic()
    {
        var previous = 0.0;
        for (var t = 0.0; t < 1; t += 0.01)
        {
            var v = Spring.Close.Value(t);
            Assert.True(v >= previous - 1e-12, $"Decreased at t={t}");
            previous = v;
        }
    }

    [Fact]
    public void Open_is_slower_and_softer_than_close()
    {
        Assert.True(Spring.Open.SettleTime > Spring.Close.SettleTime);
    }

    [Fact]
    public void Settle_times_are_in_a_polished_range()
    {
        // Too fast feels abrupt; too slow feels sluggish.
        Assert.InRange(Spring.Open.SettleTime, 0.40, 0.75);
        Assert.InRange(Spring.Close.SettleTime, 0.30, 0.55);
    }
}

public sealed class EaseTests
{
    [Fact]
    public void Curves_hit_endpoints()
    {
        Assert.Equal(0, Ease.OutCubic(0));
        Assert.Equal(1, Ease.OutCubic(1));
        Assert.Equal(0, Ease.InQuad(0));
        Assert.Equal(1, Ease.InQuad(1));
    }

    [Fact]
    public void Curves_clamp_out_of_range_input()
    {
        Assert.Equal(0, Ease.OutCubic(-2));
        Assert.Equal(1, Ease.OutCubic(5));
    }

    [Fact]
    public void Stage_respects_delay_and_duration()
    {
        Assert.Equal(0, Ease.Stage(0.05, delay: 0.1, duration: 0.2));
        Assert.Equal(0.5, Ease.Stage(0.2, delay: 0.1, duration: 0.2), 6);
        Assert.Equal(1, Ease.Stage(0.5, delay: 0.1, duration: 0.2));
    }
}

public sealed class IslandChoreographyTests
{
    private static readonly IslandPose Notch = new(132, 24, 12, Header: 0, Panel: 0, NotchEyes: 1);
    private static readonly IslandPose Compact = new(372, 60, 24, Header: 1, Panel: 0, NotchEyes: 0);
    private static readonly IslandPose Expanded = new(420, 356, 26, Header: 1, Panel: 1, NotchEyes: 0);

    [Fact]
    public void Starts_exactly_at_from_pose()
    {
        var f = new IslandChoreography(Notch, Expanded).Sample(0);

        Assert.Equal(132, f.Width, 6);
        Assert.Equal(24, f.Height, 6);
        Assert.Equal(0, f.HeaderOpacity);
        Assert.Equal(0, f.PanelOpacity);
        Assert.Equal(1, f.NotchEyesOpacity);
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Ends_exactly_at_to_pose(IslandPose from, IslandPose to)
    {
        var c = new IslandChoreography(from, to);
        var f = c.Sample(c.Duration);

        Assert.Equal(to.Width, f.Width, 1);
        Assert.Equal(to.Height, f.Height, 1);
        Assert.Equal(to.Radius, f.Radius, 1);
        Assert.Equal(to.Header, f.HeaderOpacity, 3);
        Assert.Equal(to.Panel, f.PanelOpacity, 3);
        Assert.Equal(to.NotchEyes, f.NotchEyesOpacity, 3);
        Assert.Equal(0, f.HeaderOffsetY, 3);
        Assert.Equal(0, f.PanelOffsetY, 3);
    }

    public static TheoryData<IslandPose, IslandPose> Transitions => new()
    {
        { Notch, Expanded },
        { Expanded, Notch },
        { Notch, Compact },
        { Compact, Expanded },
        { Expanded, Compact },
        { Compact, Notch },
    };

    [Fact]
    public void Opening_width_leads_height()
    {
        var c = new IslandChoreography(Notch, Expanded);
        var f = c.Sample(0.06);

        var widthProgress = (f.Width - 132) / (420 - 132);
        var heightProgress = (f.Height - 24) / (356 - 24);
        Assert.True(widthProgress > heightProgress, $"width {widthProgress:P0} vs height {heightProgress:P0}");
    }

    [Fact]
    public void Opening_content_waits_for_the_shape()
    {
        // Content must not appear while the shape is still tiny (that is what made it look "cut off").
        var c = new IslandChoreography(Notch, Expanded);
        var early = c.Sample(0.15);

        Assert.Equal(0, early.PanelOpacity);
        Assert.True(early.HeaderOpacity < 0.01);
    }

    [Fact]
    public void Opening_panel_cascades_after_header()
    {
        var c = new IslandChoreography(Notch, Expanded);
        var mid = c.Sample(0.30);

        Assert.True(mid.HeaderOpacity > mid.PanelOpacity);
    }

    [Fact]
    public void Closing_content_leaves_before_the_shape_shrinks()
    {
        var c = new IslandChoreography(Expanded, Notch);

        // Content responds instantly to the click; the shape waits a beat.
        var early = c.Sample(0.04);
        Assert.True(early.PanelOpacity < 0.6, $"panel should already be fading ({early.PanelOpacity:F2})");
        Assert.Equal(356, early.Height, 3);

        // By the time the shape is clearly shrinking, the content is almost gone (never squashed visibly).
        for (var t = 0.0; t <= c.Duration; t += 0.004)
        {
            var f = c.Sample(t);
            if (f.Height < 300)
            {
                Assert.True(f.PanelOpacity < 0.25, $"panel still {f.PanelOpacity:F2} visible at height {f.Height:F0}");
            }
        }
    }

    [Fact]
    public void Closing_never_overshoots_into_negative_or_below_target()
    {
        var c = new IslandChoreography(Expanded, Notch);
        for (var t = 0.0; t <= c.Duration; t += 0.004)
        {
            var f = c.Sample(t);
            Assert.True(f.Width >= 132 - 0.5, $"width {f.Width} at {t}");
            Assert.True(f.Height >= 24 - 0.5, $"height {f.Height} at {t}");
        }
    }

    [Fact]
    public void Opening_overshoot_is_subtle()
    {
        var c = new IslandChoreography(Notch, Expanded);
        var maxHeight = 0.0;
        for (var t = 0.0; t <= c.Duration; t += 0.004)
        {
            maxHeight = Math.Max(maxHeight, c.Sample(t).Height);
        }

        Assert.InRange(maxHeight, 356, 356 + 20); // a few DIP, never a big bounce
    }

    [Fact]
    public void Face_slides_down_into_place()
    {
        var c = new IslandChoreography(Notch, Expanded);

        Assert.True(c.Sample(0.1).FaceOffsetY < -10);
        Assert.Equal(0, c.Sample(c.Duration).FaceOffsetY, 3);
        Assert.Equal(1, c.Sample(c.Duration).FaceScale, 3);
    }

    [Fact]
    public void Reversing_mid_flight_continues_from_current_frame_without_a_jump()
    {
        // User opens, then clicks away halfway through.
        var opening = new IslandChoreography(Notch, Expanded);
        var midFrame = opening.Sample(0.2);

        var closing = new IslandChoreography(midFrame.ToPose(), Notch);
        var first = closing.Sample(0);

        Assert.Equal(midFrame.Width, first.Width, 6);
        Assert.Equal(midFrame.Height, first.Height, 6);
        Assert.Equal(midFrame.HeaderOpacity, first.HeaderOpacity, 6);
        Assert.Equal(midFrame.PanelOpacity, first.PanelOpacity, 6);
        Assert.Equal(midFrame.NotchEyesOpacity, first.NotchEyesOpacity, 6);
    }

    [Fact]
    public void Frames_change_smoothly_with_no_large_per_frame_jumps()
    {
        // At 60 fps no dimension may jump more than a sane amount between consecutive frames.
        foreach (var (from, to) in new[] { (Notch, Expanded), (Expanded, Notch), (Compact, Expanded), (Expanded, Compact) })
        {
            var c = new IslandChoreography(from, to);
            var previous = c.Sample(0);
            for (var t = 1 / 60.0; t <= c.Duration + (1 / 60.0); t += 1 / 60.0)
            {
                var f = c.Sample(t);
                Assert.True(Math.Abs(f.Width - previous.Width) < 40, $"width jump {f.Width - previous.Width:F1} at {t:F3}");
                Assert.True(Math.Abs(f.Height - previous.Height) < 45, $"height jump {f.Height - previous.Height:F1} at {t:F3}");
                Assert.True(Math.Abs(f.PanelOpacity - previous.PanelOpacity) < 0.25, $"opacity jump at {t:F3}");
                previous = f;
            }
        }
    }

    [Fact]
    public void Simple_spring_mode_is_a_single_stage_move()
    {
        var swell = Notch with { Width = 148, Height = 27 };
        var c = new IslandChoreography(Notch, swell, Spring.Snappy);

        Assert.InRange(c.Duration, 0.15, 0.45);
        Assert.Equal(148, c.Sample(c.Duration).Width, 6);
        Assert.Equal(1, c.Sample(0.05).NotchEyesOpacity); // content untouched
    }

    [Fact]
    public void Durations_feel_unhurried_but_not_slow()
    {
        Assert.InRange(new IslandChoreography(Notch, Expanded).Duration, 0.45, 0.85);
        Assert.InRange(new IslandChoreography(Expanded, Notch).Duration, 0.35, 0.65);
    }
}
