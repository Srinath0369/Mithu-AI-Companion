namespace Mithu.Core.Layout;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    /// <summary>Area of overlap with <paramref name="other"/>, or 0 when they don't intersect.</summary>
    public long IntersectionArea(PixelRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w > 0 && h > 0 ? (long)w * h : 0;
    }
}
