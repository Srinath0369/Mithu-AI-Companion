using Mithu.Core.Motion;

namespace Mithu.Tests;

public sealed class PunchCurveTests
{
    [Fact]
    public void Starts_and_ends_at_normal_size()
    {
        Assert.Equal((1.0, 1.0), PunchCurve.Sample(0));
        Assert.Equal((1.0, 1.0), PunchCurve.Sample(PunchCurve.Duration));
    }

    [Fact]
    public void Squashes_wider_and_shorter_on_impact()
    {
        var (x, y) = PunchCurve.Sample(PunchCurve.SquashTime);

        Assert.Equal(PunchCurve.SquashX, x, 3);
        Assert.Equal(PunchCurve.SquashY, y, 3);
        Assert.True(x > 1 && y < 1);
    }

    [Fact]
    public void Wobble_is_small_and_friendly()
    {
        // After the squash it overshoots the other way a little (jelly), never wildly.
        var minX = 1.0;
        var maxY = 1.0;
        for (var t = PunchCurve.SquashTime; t < PunchCurve.Duration; t += 0.002)
        {
            var (x, y) = PunchCurve.Sample(t);
            minX = Math.Min(minX, x);
            maxY = Math.Max(maxY, y);
        }

        Assert.InRange(minX, 0.95, 0.995);   // visible rebound
        Assert.InRange(maxY, 1.005, 1.05);
    }

    [Fact]
    public void Frames_are_smooth_at_60fps()
    {
        var previous = PunchCurve.Sample(0);
        for (var t = 1 / 60.0; t <= PunchCurve.Duration + (1 / 60.0); t += 1 / 60.0)
        {
            var current = PunchCurve.Sample(t);
            Assert.True(Math.Abs(current.X - previous.X) < 0.12, $"X jump at {t:F3}");
            Assert.True(Math.Abs(current.Y - previous.Y) < 0.12, $"Y jump at {t:F3}");
            previous = current;
        }
    }

    [Fact]
    public void Is_settled_before_the_end_so_the_final_snap_is_invisible()
    {
        var (x, y) = PunchCurve.Sample(PunchCurve.Duration - 0.001);

        Assert.InRange(x, 0.997, 1.003);
        Assert.InRange(y, 0.997, 1.003);
    }

    [Fact]
    public void Feels_quick_not_sluggish() =>
        Assert.InRange(PunchCurve.Duration, 0.35, 0.7);
}
