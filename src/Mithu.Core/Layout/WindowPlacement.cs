namespace Mithu.Core.Layout;

/// <summary>
/// Monitor-relative dock anchor, stable across resolution and DPI changes.
/// </summary>
/// <param name="MonitorDeviceName">Device name such as <c>\\.\DISPLAY1</c>.</param>
/// <param name="AnchorX">Window's horizontal center as a fraction (0–1) of the work-area width.</param>
/// <param name="AnchorY">Window's top edge as a fraction (0–1) of the work-area height.</param>
public sealed record WindowPlacement(string MonitorDeviceName, double AnchorX, double AnchorY);
