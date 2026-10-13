namespace Mithu.Core.Layout;

/// <summary>
/// A connected display. Rectangles are physical pixels; <see cref="Scale"/> is DPI / 96 (1.0 = 100 %).
/// </summary>
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary);
