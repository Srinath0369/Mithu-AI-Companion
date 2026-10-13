using Mithu.Core.Layout;
using Mithu.Core.Models;

namespace Mithu.Tests;

public sealed class DockingTests
{
    private static readonly MonitorInfo Primary = new(
        @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 1.0, true);

    // Taskbar at the top: work area starts at y = 48.
    private static readonly MonitorInfo TopTaskbar = new(
        @"\\.\DISPLAY2", new PixelRect(1920, 0, 1920, 1080), new PixelRect(1920, 48, 1920, 1032), 1.5, false);

    private static readonly IReadOnlyList<MonitorInfo> Monitors = [Primary, TopTaskbar];

    [Fact]
    public void Docked_window_touches_top_of_work_area_with_no_margin()
    {
        var rect = PlacementCalculator.Resolve(null, Monitors, 380, 76, dockToTop: true);

        Assert.Equal(0, rect.Y);
        Assert.Equal(770, rect.X); // still centered
    }

    [Fact]
    public void Docked_window_sits_below_a_top_taskbar()
    {
        var placement = new WindowPlacement(TopTaskbar.DeviceName, 0.5, 0.3);

        var rect = PlacementCalculator.Resolve(placement, Monitors, 380, 76, dockToTop: true);

        Assert.Equal(48, rect.Y);
    }

    [Fact]
    public void Docking_keeps_horizontal_position()
    {
        var placement = new WindowPlacement(Primary.DeviceName, 0.25, 0.4);

        var floating = PlacementCalculator.Resolve(placement, Monitors, 380, 76);
        var docked = PlacementCalculator.Resolve(placement, Monitors, 380, 76, dockToTop: true);

        Assert.Equal(floating.X, docked.X);
        Assert.Equal(0, docked.Y);
    }

    [Fact]
    public void Tucked_strip_stays_centered_on_the_same_anchor()
    {
        var placement = new WindowPlacement(Primary.DeviceName, 0.5, 0);

        var bar = PlacementCalculator.Resolve(placement, Monitors, 380, 76, dockToTop: true);
        var strip = PlacementCalculator.Resolve(placement, Monitors, 120, 10, dockToTop: true);

        Assert.Equal(bar.X + (bar.Width / 2), strip.X + (strip.Width / 2));
        Assert.Equal(bar.Y, strip.Y);
    }

    [Theory]
    [InlineData(0, true)]     // exactly at the edge
    [InlineData(-30, true)]   // dragged above the edge
    [InlineData(4, true)]     // within threshold
    [InlineData(5, false)]    // just outside
    [InlineData(8, false)]    // default floating margin must NOT snap
    [InlineData(200, false)]
    public void ShouldSnapToTop_uses_threshold(int y, bool expected)
    {
        var rect = new PixelRect(700, y, 380, 76);

        Assert.Equal(expected, PlacementCalculator.ShouldSnapToTop(rect, Monitors));
    }

    [Fact]
    public void Snap_threshold_scales_with_dpi_and_respects_work_area()
    {
        // 150 % monitor, work area top 48: threshold = 6 px → y ≤ 54 snaps.
        Assert.True(PlacementCalculator.ShouldSnapToTop(new PixelRect(2500, 54, 570, 114), Monitors));
        Assert.False(PlacementCalculator.ShouldSnapToTop(new PixelRect(2500, 55, 570, 114), Monitors));
    }

    [Fact]
    public void Default_floating_position_does_not_snap()
    {
        var rect = PlacementCalculator.Resolve(null, Monitors, 380, 76);

        Assert.False(PlacementCalculator.ShouldSnapToTop(rect, Monitors));
    }

    [Fact]
    public void Settings_defaults_for_docking()
    {
        var s = new AppSettings();

        Assert.True(s.DockToTop);            // notch design is the default
        Assert.True(s.CloseOnClickAway);
        Assert.Equal(WindowMode.Expanded, s.LastVisibleMode); // hover opens the full panel
    }

    // ----- Sliding the hidden notch along the top edge -----

    private static readonly PixelRect Notch = new(890, 0, 200, 48);

    [Fact]
    public void Slide_moves_horizontally_by_the_drag_distance()
    {
        var r = PlacementCalculator.SlideAlongTop(Notch, -300, Monitors);

        Assert.Equal(590, r.X);
        Assert.Equal(0, r.Y);
        Assert.Equal(Notch.Width, r.Width);
        Assert.Equal(Notch.Height, r.Height);
    }

    [Fact]
    public void Slide_stays_glued_to_the_top_edge()
    {
        // Vertical pointer movement is not even an input: Y always comes from the work area.
        var r = PlacementCalculator.SlideAlongTop(Notch with { Y = 37 }, 50, Monitors);

        Assert.Equal(0, r.Y);
    }

    [Fact]
    public void Slide_stops_at_the_left_screen_edge()
    {
        var r = PlacementCalculator.SlideAlongTop(Notch, -5000, Monitors);

        Assert.Equal(0, r.X);
    }

    [Fact]
    public void Slide_can_cross_onto_the_neighbouring_monitor()
    {
        // Dragged right past the primary's edge onto DISPLAY2 (top taskbar => work area starts at y=48).
        var r = PlacementCalculator.SlideAlongTop(Notch, 1400, Monitors);

        Assert.Equal(2290, r.X);
        Assert.Equal(48, r.Y);
    }

    [Fact]
    public void Slide_stops_at_the_far_right_of_the_last_monitor()
    {
        var r = PlacementCalculator.SlideAlongTop(Notch, 50_000, Monitors);

        Assert.Equal(1920 + 1920 - Notch.Width, r.X);
    }

    [Fact]
    public void Slid_position_round_trips_through_capture_and_resolve()
    {
        var slid = PlacementCalculator.SlideAlongTop(Notch, -400, Monitors);

        var placement = PlacementCalculator.Capture(slid, Monitors);
        var restored = PlacementCalculator.Resolve(placement, Monitors, slid.Width, slid.Height, dockToTop: true);

        Assert.InRange(Math.Abs(restored.X - slid.X), 0, 1);
        Assert.Equal(0, restored.Y);
    }
}

public sealed class ClickAwayPolicyTests
{
    private static readonly CollapseContext Open = new(
        Enabled: true, Visible: true, AlreadyHidden: false, Dragging: false, MenuOpen: false, PeekShowing: false);

    [Fact]
    public void Clicking_elsewhere_closes_an_open_companion() =>
        Assert.True(ClickAwayPolicy.ShouldCloseOnClickAway(Open));

    [Fact]
    public void Closes_when_floating_too() =>
        Assert.True(ClickAwayPolicy.ShouldCloseOnClickAway(Open)); // no dock condition: notch and bubble behave the same

    [Fact]
    public void Stays_open_when_option_is_off() =>
        Assert.False(ClickAwayPolicy.ShouldCloseOnClickAway(Open with { Enabled = false }));

    [Fact]
    public void Nothing_to_close_when_already_hidden() =>
        Assert.False(ClickAwayPolicy.ShouldCloseOnClickAway(Open with { AlreadyHidden = true }));

    [Fact]
    public void Nothing_to_close_when_window_is_hidden_to_tray() =>
        Assert.False(ClickAwayPolicy.ShouldCloseOnClickAway(Open with { Visible = false }));

    [Fact]
    public void Stays_open_while_dragging() =>
        Assert.False(ClickAwayPolicy.ShouldCloseOnClickAway(Open with { Dragging = true }));

    [Fact]
    public void Stays_open_while_its_own_menu_has_focus() =>
        Assert.False(ClickAwayPolicy.ShouldCloseOnClickAway(Open with { MenuOpen = true }));

    [Fact]
    public void Stays_open_while_a_peek_message_is_showing() =>
        Assert.False(ClickAwayPolicy.ShouldCloseOnClickAway(Open with { PeekShowing = true }));

    [Fact]
    public void Unfocused_safety_net_closes_only_when_pointer_is_away()
    {
        Assert.True(ClickAwayPolicy.ShouldCloseUnfocused(Open, isActive: false, pointerOver: false));
        Assert.False(ClickAwayPolicy.ShouldCloseUnfocused(Open, isActive: false, pointerOver: true));
    }

    [Fact]
    public void Unfocused_safety_net_never_fires_while_user_is_working_in_it() =>
        Assert.False(ClickAwayPolicy.ShouldCloseUnfocused(Open, isActive: true, pointerOver: false));

    [Fact]
    public void Unfocused_timeout_is_generous_enough_to_notice()
    {
        Assert.InRange(ClickAwayPolicy.UnfocusedTimeout.TotalSeconds, 2, 10);
    }
}
