namespace Mithu.Core.Layout;

/// <summary>
/// Pure functions that place the companion window safely on connected monitors.
/// The window is anchored by its <b>top-center</b>, so it grows downward and sideways from its dock.
/// </summary>
public static class PlacementCalculator
{
    /// <summary>Gap between the top of the work area and the window in the default dock.</summary>
    public const double DefaultTopMarginDip = 8;

    /// <summary>
    /// Dropping the window this close to (or above) the top edge sticks it to the top.
    /// Smaller than <see cref="DefaultTopMarginDip"/> so the default floating position never snaps by accident.
    /// </summary>
    public const double SnapThresholdDip = 4;

    /// <summary>
    /// Computes the physical window rectangle for a window of the given DIP size.
    /// Falls back to top-center of the primary monitor when there is no placement or its monitor is gone.
    /// </summary>
    /// <param name="dockToTop">When true the window touches the top of the work area exactly (no margin).</param>
    public static PixelRect Resolve(
        WindowPlacement? placement,
        IReadOnlyList<MonitorInfo> monitors,
        double widthDip,
        double heightDip,
        bool dockToTop = false)
    {
        var saved = placement is null ? null : FindMonitor(placement.MonitorDeviceName, monitors);
        var monitor = saved ?? PrimaryOf(monitors);
        var area = monitor.WorkArea;

        var width = (int)Math.Round(widthDip * monitor.Scale);
        var height = (int)Math.Round(heightDip * monitor.Scale);

        double centerX;
        double top;
        if (saved is not null)
        {
            centerX = area.X + (placement!.AnchorX * area.Width);
            top = area.Y + (placement.AnchorY * area.Height);
        }
        else
        {
            centerX = area.X + (area.Width / 2.0);
            top = area.Y + (DefaultTopMarginDip * monitor.Scale);
        }

        if (dockToTop)
        {
            top = area.Y;
        }

        var rect = new PixelRect((int)Math.Round(centerX - (width / 2.0)), (int)Math.Round(top), width, height);
        return Clamp(rect, area);
    }

    /// <summary>Converts the current window rectangle into a monitor-relative anchor.</summary>
    public static WindowPlacement Capture(PixelRect windowRect, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = MonitorFor(windowRect, monitors);
        var area = monitor.WorkArea;
        var anchorX = (windowRect.X + (windowRect.Width / 2.0) - area.X) / area.Width;
        var anchorY = (double)(windowRect.Y - area.Y) / area.Height;
        return new WindowPlacement(monitor.DeviceName, Math.Clamp(anchorX, 0, 1), Math.Clamp(anchorY, 0, 1));
    }

    /// <summary>True when a window dropped at <paramref name="windowRect"/> is close enough to the top edge to stick.</summary>
    public static bool ShouldSnapToTop(PixelRect windowRect, IReadOnlyList<MonitorInfo> monitors)
    {
        var monitor = MonitorFor(windowRect, monitors);
        return windowRect.Y - monitor.WorkArea.Y <= SnapThresholdDip * monitor.Scale;
    }

    /// <summary>
    /// Slides a docked window horizontally along the top edge while it is being dragged.
    /// Stays glued to the top of the work area, never leaves the screen, and can cross onto a monitor
    /// that sits beside the current one. Vertical movement is ignored on purpose.
    /// </summary>
    /// <param name="start">Window rectangle when the drag began (physical pixels).</param>
    /// <param name="deltaX">Horizontal pointer movement since the drag began (physical pixels).</param>
    public static PixelRect SlideAlongTop(PixelRect start, int deltaX, IReadOnlyList<MonitorInfo> monitors)
    {
        var moved = start with { X = start.X + deltaX };

        // The monitor it is over; if dragged past every screen, the screen whose edge is nearest
        // (so it stops at that edge instead of jumping back to where the drag started).
        var area = (monitors.Any(m => moved.IntersectionArea(m.Bounds) > 0)
            ? MonitorFor(moved, monitors)
            : NearestHorizontally(moved, monitors)).WorkArea;

        var x = moved.Width >= area.Width ? area.X : Math.Clamp(moved.X, area.X, area.Right - moved.Width);
        return moved with { X = x, Y = area.Y };
    }

    /// <summary>Moves (never resizes) a rectangle so it lies inside <paramref name="area"/>.</summary>
    public static PixelRect Clamp(PixelRect rect, PixelRect area)
    {
        var x = rect.Width >= area.Width ? area.X : Math.Clamp(rect.X, area.X, area.Right - rect.Width);
        var y = rect.Height >= area.Height ? area.Y : Math.Clamp(rect.Y, area.Y, area.Bottom - rect.Height);
        return rect with { X = x, Y = y };
    }

    /// <summary>The monitor that overlaps the rectangle most, or the primary monitor if none does.</summary>
    public static MonitorInfo MonitorFor(PixelRect rect, IReadOnlyList<MonitorInfo> monitors)
    {
        MonitorInfo? best = null;
        long bestArea = 0;
        foreach (var monitor in monitors)
        {
            var overlap = rect.IntersectionArea(monitor.Bounds);
            if (overlap > bestArea)
            {
                best = monitor;
                bestArea = overlap;
            }
        }

        return best ?? PrimaryOf(monitors);
    }

    /// <summary>Monitor whose horizontal span is closest to the rectangle's centre (ties: the primary).</summary>
    private static MonitorInfo NearestHorizontally(PixelRect rect, IReadOnlyList<MonitorInfo> monitors)
    {
        var center = rect.X + (rect.Width / 2);
        MonitorInfo? best = null;
        var bestDistance = int.MaxValue;
        foreach (var monitor in monitors)
        {
            var b = monitor.Bounds;
            var distance = center < b.X ? b.X - center : center > b.Right ? center - b.Right : 0;
            if (distance < bestDistance || (distance == bestDistance && monitor.IsPrimary))
            {
                best = monitor;
                bestDistance = distance;
            }
        }

        return best ?? PrimaryOf(monitors);
    }

    private static MonitorInfo? FindMonitor(string deviceName, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    private static MonitorInfo PrimaryOf(IReadOnlyList<MonitorInfo> monitors)
    {
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }

        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }
}
