using static Mithu.Infrastructure.Windows.NativeMethods;

namespace Mithu.Infrastructure.Windows;

/// <summary>
/// Detects when another app is fullscreen (game, slideshow, video) so Mithu can get out of the way.
/// Event-driven: checks only when the foreground window changes — no polling.
/// Must be started on a thread with a message loop (the WPF UI thread).
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    // Keep a reference so the delegate isn't garbage-collected while Windows still calls it.
    private readonly WinEventProc _callback;
    private IntPtr _hook;

    public FullscreenWatcher()
    {
        _callback = OnForegroundChanged;
    }

    public bool IsFullscreenActive { get; private set; }

    /// <summary>Raised on the UI thread when fullscreen state changes.</summary>
    public event EventHandler<bool>? Changed;

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        Recheck();
    }

    public void Recheck() => Update(IsFullscreen(GetForegroundWindow()));

    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time) =>
        Update(IsFullscreen(hwnd));

    private void Update(bool isFullscreen)
    {
        if (isFullscreen == IsFullscreenActive)
        {
            return;
        }

        IsFullscreenActive = isFullscreen;
        Changed?.Invoke(this, isFullscreen);
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        // Windows' own signal covers exclusive D3D games and PowerPoint presentation mode.
        if (SHQueryUserNotificationState(out var state) == 0 &&
            state is QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE)
        {
            return true;
        }

        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || hwnd == GetDesktopWindow() || IsShellClass(hwnd))
        {
            return false;
        }

        // Normal maximized windows have a caption; borderless fullscreen (F11, games, video) does not.
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_CAPTION) == WS_CAPTION)
        {
            return false;
        }

        if (!GetWindowRect(hwnd, out var window))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var screen = info.rcMonitor;
        return window.Left <= screen.Left && window.Top <= screen.Top &&
               window.Right >= screen.Right && window.Bottom >= screen.Bottom;
    }

    private static bool IsShellClass(IntPtr hwnd)
    {
        var buffer = new char[64];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        var name = new string(buffer, 0, Math.Max(0, length));
        return ShellClasses.Contains(name, StringComparer.Ordinal);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
