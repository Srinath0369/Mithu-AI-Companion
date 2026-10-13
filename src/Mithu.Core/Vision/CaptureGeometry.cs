namespace Mithu.Core.Vision;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public CaptureRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>The part of this rectangle inside <paramref name="bounds"/> (empty if none).</summary>
    public CaptureRect Intersect(CaptureRect bounds)
    {
        var left = Math.Max(X, bounds.X);
        var top = Math.Max(Y, bounds.Y);
        var right = Math.Min(Right, bounds.Right);
        var bottom = Math.Min(Bottom, bounds.Bottom);
        return right > left && bottom > top ? new CaptureRect(left, top, right - left, bottom - top) : default;
    }
}

/// <summary>What the user picked in the capture overlay.</summary>
public enum CaptureKind
{
    Region,
    Window,
    WholeScreen,
}

/// <summary>Geometry of the screenshot picker (pure, so it can be tested).</summary>
public static class CaptureGeometry
{
    /// <summary>Smaller than this (in pixels) counts as a click, not a drag.</summary>
    public const int DragThreshold = 6;

    /// <summary>
    /// Longest side sent to the AI: larger screenshots are scaled down (providers shrink them anyway; this keeps
    /// requests small and fast).
    /// </summary>
    public const int MaxSideForAi = 1568;

    /// <summary>The rectangle between two points (any drag direction), clamped to the screen.</summary>
    public static CaptureRect FromDrag(int x1, int y1, int x2, int y2, CaptureRect screen)
    {
        var rect = new CaptureRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        return rect.Intersect(screen);
    }

    public static bool IsDrag(int x1, int y1, int x2, int y2) =>
        Math.Abs(x2 - x1) >= DragThreshold || Math.Abs(y2 - y1) >= DragThreshold;

    /// <summary>The top-most window under the point (windows are listed front to back), clipped to the screen.</summary>
    public static CaptureRect? WindowAt(int x, int y, IReadOnlyList<CaptureRect> windowsFrontToBack, CaptureRect screen)
    {
        foreach (var window in windowsFrontToBack)
        {
            if (window.Contains(x, y))
            {
                var visible = window.Intersect(screen);
                return visible.IsEmpty ? null : visible;
            }
        }

        return null;
    }

    /// <summary>Size to send to the AI: the same when small enough, otherwise scaled so the longest side fits.</summary>
    public static (int Width, int Height) FitForAi(int width, int height, int maxSide = MaxSideForAi)
    {
        var longest = Math.Max(width, height);
        if (longest <= maxSide)
        {
            return (width, height);
        }

        var scale = (double)maxSide / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
