using Mithu.Core.Layout;

namespace Mithu.Tests;

public sealed class PlacementCalculatorTests
{
    // Primary 1920x1080 @100 %, taskbar at bottom (work area height 1032).
    private static readonly MonitorInfo Primary = new(
        @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 1.0, true);

    // Secondary 2560x1440 @150 % to the right.
    private static readonly MonitorInfo Secondary = new(
        @"\\.\DISPLAY2", new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), 1.5, false);

    private static readonly IReadOnlyList<MonitorInfo> Both = [Primary, Secondary];

    [Fact]
    public void Default_is_top_center_of_primary_with_margin()
    {
        var rect = PlacementCalculator.Resolve(null, Both, 380, 76);

        Assert.Equal(new PixelRect(770, 8, 380, 76), rect);
    }

    [Fact]
    public void Size_is_scaled_by_monitor_dpi()
    {
        var placement = new WindowPlacement(Secondary.DeviceName, 0.5, 0);

        var rect = PlacementCalculator.Resolve(placement, Both, 380, 76);

        Assert.Equal(570, rect.Width);
        Assert.Equal(114, rect.Height);
        Assert.Equal(1920 + 1280 - 285, rect.X); // centered
    }

    [Fact]
    public void Missing_monitor_falls_back_to_primary_default()
    {
        var placement = new WindowPlacement(@"\\.\DISPLAY9", 0.1, 0.5);

        var rect = PlacementCalculator.Resolve(placement, Both, 380, 76);

        Assert.Equal(new PixelRect(770, 8, 380, 76), rect);
    }

    [Fact]
    public void Monitor_name_match_is_case_insensitive()
    {
        var placement = new WindowPlacement(@"\\.\display2", 0.5, 0);

        var rect = PlacementCalculator.Resolve(placement, Both, 100, 100);

        Assert.True(rect.X >= Secondary.WorkArea.X);
    }

    [Fact]
    public void Window_is_clamped_inside_work_area_at_edges()
    {
        // Anchored at far right/bottom: must be pulled back fully on-screen.
        var placement = new WindowPlacement(Primary.DeviceName, 1.0, 1.0);

        var rect = PlacementCalculator.Resolve(placement, Both, 420, 340);

        Assert.Equal(1920 - 420, rect.X);
        Assert.Equal(1032 - 340, rect.Y);
    }

    [Fact]
    public void Expanding_keeps_top_center_anchor()
    {
        var placement = new WindowPlacement(Primary.DeviceName, 0.5, 0.2);

        var bar = PlacementCalculator.Resolve(placement, Both, 380, 76);
        var expanded = PlacementCalculator.Resolve(placement, Both, 420, 340);

        Assert.Equal(bar.Y, expanded.Y);
        Assert.Equal(bar.X + (bar.Width / 2), expanded.X + (expanded.Width / 2));
    }

    [Fact]
    public void Capture_then_resolve_round_trips_position()
    {
        var original = new PixelRect(2500, 300, 570, 114);

        var placement = PlacementCalculator.Capture(original, Both);
        var restored = PlacementCalculator.Resolve(placement, Both, 380, 76);

        Assert.Equal(Secondary.DeviceName, placement.MonitorDeviceName);
        Assert.InRange(Math.Abs(restored.X - original.X), 0, 1);
        Assert.InRange(Math.Abs(restored.Y - original.Y), 0, 1);
    }

    [Fact]
    public void MonitorFor_picks_largest_overlap()
    {
        // Mostly on the secondary monitor.
        var straddling = new PixelRect(1800, 100, 400, 100);

        Assert.Equal(Secondary, PlacementCalculator.MonitorFor(straddling, Both));
    }

    [Fact]
    public void MonitorFor_offscreen_rect_returns_primary()
    {
        var offscreen = new PixelRect(-5000, -5000, 100, 100);

        Assert.Equal(Primary, PlacementCalculator.MonitorFor(offscreen, Both));
    }

    [Fact]
    public void Oversized_window_is_pinned_to_work_area_origin()
    {
        var tiny = new PixelRect(0, 0, 300, 200);

        var rect = PlacementCalculator.Clamp(new PixelRect(50, 50, 400, 300), tiny);

        Assert.Equal(0, rect.X);
        Assert.Equal(0, rect.Y);
    }

    [Fact]
    public void Resolve_without_monitors_throws()
    {
        Assert.Throws<ArgumentException>(() => PlacementCalculator.Resolve(null, [], 100, 100));
    }
}
