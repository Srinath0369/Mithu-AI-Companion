using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Mithu.Core.Interfaces;
using Mithu.Core.Layout;
using static Mithu.App.Interop.NativeMethods;

namespace Mithu.App.Services;

/// <summary>
/// Positions the companion window in physical pixels from the saved monitor-relative anchor.
/// Every resize goes through <see cref="PlacementCalculator.Resolve"/>, so the window always grows
/// from its top-center dock and is always clamped onto a connected monitor.
/// </summary>
public sealed class WindowPlacementService(
    IMonitorProvider monitorProvider,
    ISettingsStore settings,
    ILogger<WindowPlacementService> logger)
{
    /// <summary>Sizes and positions the window for a content size in DIPs.</summary>
    public void Apply(Window window, Size sizeDip)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var rect = PlacementCalculator.Resolve(
            settings.Current.Placement, monitorProvider.GetMonitors(), sizeDip.Width, sizeDip.Height, settings.Current.DockToTop);

        if (!SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER | SWP_NOACTIVATE))
        {
            logger.LogWarning("SetWindowPos failed ({Error})", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        }
    }

    /// <summary>
    /// Stores the current window location as the new anchor (after a drag).
    /// Dropping at the top edge sticks the window to the top; dragging it down un-sticks it.
    /// </summary>
    public void SaveCurrent(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
        {
            return;
        }

        var monitors = monitorProvider.GetMonitors();
        var rect = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var placement = PlacementCalculator.Capture(rect, monitors);
        var dock = PlacementCalculator.ShouldSnapToTop(rect, monitors);
        settings.Update(s => s with { Placement = placement, DockToTop = dock });
        logger.LogDebug(
            "Saved placement on {Monitor} ({X:0.000}, {Y:0.000}), docked: {Docked}",
            placement.MonitorDeviceName, placement.AnchorX, placement.AnchorY, dock);
    }

    /// <summary>Current window rectangle in physical pixels, or null if the window has no handle yet.</summary>
    public PixelRect? GetRect(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var r)
            ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            : null;
    }

    /// <summary>
    /// Slides the docked window horizontally during a drag (physical pixels, no resize).
    /// Returns the rectangle actually applied.
    /// </summary>
    public PixelRect SlideAlongTop(Window window, PixelRect start, int deltaX)
    {
        var rect = PlacementCalculator.SlideAlongTop(start, deltaX, monitorProvider.GetMonitors());
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
        }

        return rect;
    }

    /// <summary>Saves a docked position after sliding (keeps it stuck to the top).</summary>
    public void SaveDocked(PixelRect rect)
    {
        var placement = PlacementCalculator.Capture(rect, monitorProvider.GetMonitors());
        settings.Update(s => s with { Placement = placement, DockToTop = true });
        logger.LogInformation(
            "Notch moved to {Monitor} at {X:P0} across", placement.MonitorDeviceName, placement.AnchorX);
    }

    /// <summary>Forgets the saved anchor so the window returns to top-center of the primary monitor.</summary>
    public void Reset() => settings.Update(s => s with { Placement = null });
}
