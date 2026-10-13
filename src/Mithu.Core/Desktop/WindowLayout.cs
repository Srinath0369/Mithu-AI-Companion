using Mithu.Core.Vision;

namespace Mithu.Core.Desktop;

/// <summary>Where a window can go.</summary>
public enum WindowPlace
{
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center,
    Maximize,
    Minimize,
    Restore,
}

/// <summary>
/// Window placement maths (pure, physical pixels): halves and quarters of a monitor's work area (the screen
/// minus the taskbar), and the correction for the invisible resize borders Windows puts around windows.
/// </summary>
public static class WindowLayout
{
    public static WindowPlace? Parse(string? text) =>
        (text ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", "-", StringComparison.Ordinal).Replace("_", "-", StringComparison.Ordinal) switch
        {
            "left" or "left-half" => WindowPlace.Left,
            "right" or "right-half" => WindowPlace.Right,
            "top" or "top-half" => WindowPlace.Top,
            "bottom" or "bottom-half" => WindowPlace.Bottom,
            "top-left" => WindowPlace.TopLeft,
            "top-right" => WindowPlace.TopRight,
            "bottom-left" => WindowPlace.BottomLeft,
            "bottom-right" => WindowPlace.BottomRight,
            "center" or "centre" or "middle" => WindowPlace.Center,
            "maximize" or "maximise" or "full" or "fullscreen" or "full-screen" => WindowPlace.Maximize,
            "minimize" or "minimise" or "hide" => WindowPlace.Minimize,
            "restore" or "normal" => WindowPlace.Restore,
            _ => null,
        };

    /// <summary>The visible frame for a place in a work area (null for maximize / minimize / restore).</summary>
    public static CaptureRect? Target(CaptureRect work, WindowPlace place)
    {
        var halfW = work.Width / 2;
        var halfH = work.Height / 2;
        return place switch
        {
            WindowPlace.Left => work with { Width = halfW },
            WindowPlace.Right => new CaptureRect(work.X + halfW, work.Y, work.Width - halfW, work.Height),
            WindowPlace.Top => work with { Height = halfH },
            WindowPlace.Bottom => new CaptureRect(work.X, work.Y + halfH, work.Width, work.Height - halfH),
            WindowPlace.TopLeft => new CaptureRect(work.X, work.Y, halfW, halfH),
            WindowPlace.TopRight => new CaptureRect(work.X + halfW, work.Y, work.Width - halfW, halfH),
            WindowPlace.BottomLeft => new CaptureRect(work.X, work.Y + halfH, halfW, work.Height - halfH),
            WindowPlace.BottomRight => new CaptureRect(work.X + halfW, work.Y + halfH, work.Width - halfW, work.Height - halfH),
            WindowPlace.Center => new CaptureRect(work.X + work.Width / 8, work.Y + work.Height / 8, work.Width * 3 / 4, work.Height * 3 / 4),
            _ => null,
        };
    }

    /// <summary>
    /// The rectangle to give SetWindowPos so the window's visible frame lands on <paramref name="visible"/>:
    /// windows have invisible resize borders (the gap between their window rectangle and their visible frame).
    /// </summary>
    public static CaptureRect WithBorders(CaptureRect visible, CaptureRect windowRect, CaptureRect frameRect)
    {
        var left = Math.Max(0, frameRect.X - windowRect.X);
        var top = Math.Max(0, frameRect.Y - windowRect.Y);
        var right = Math.Max(0, windowRect.Right - frameRect.Right);
        var bottom = Math.Max(0, windowRect.Bottom - frameRect.Bottom);
        return new CaptureRect(visible.X - left, visible.Y - top, visible.Width + left + right, visible.Height + top + bottom);
    }

    /// <summary>"left half", "top-right quarter", "maximized"…</summary>
    public static string Describe(WindowPlace place) => place switch
    {
        WindowPlace.Left => "the left half",
        WindowPlace.Right => "the right half",
        WindowPlace.Top => "the top half",
        WindowPlace.Bottom => "the bottom half",
        WindowPlace.TopLeft => "the top-left quarter",
        WindowPlace.TopRight => "the top-right quarter",
        WindowPlace.BottomLeft => "the bottom-left quarter",
        WindowPlace.BottomRight => "the bottom-right quarter",
        WindowPlace.Center => "the middle",
        WindowPlace.Maximize => "full screen",
        WindowPlace.Minimize => "the taskbar",
        _ => "its normal size",
    };
}
