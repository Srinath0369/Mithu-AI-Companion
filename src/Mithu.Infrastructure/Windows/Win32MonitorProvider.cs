using Mithu.Core.Interfaces;
using Mithu.Core.Layout;
using static Mithu.Infrastructure.Windows.NativeMethods;

namespace Mithu.Infrastructure.Windows;

/// <summary>Enumerates connected monitors with physical-pixel bounds, work areas and per-monitor DPI.</summary>
public sealed class Win32MonitorProvider : IMonitorProvider
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        bool Callback(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data)
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (!GetMonitorInfo(hMonitor, ref info))
            {
                return true;
            }

            var scale = GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0
                ? dpiX / 96.0
                : 1.0;

            monitors.Add(new MonitorInfo(
                info.szDevice,
                ToPixelRect(info.rcMonitor),
                ToPixelRect(info.rcWork),
                scale,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            // Extremely unlikely (e.g. during display driver reset). Provide a safe virtual monitor.
            var fallback = new PixelRect(0, 0, 1280, 720);
            monitors.Add(new MonitorInfo("fallback", fallback, fallback, 1.0, true));
        }

        return monitors;
    }

    private static PixelRect ToPixelRect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
