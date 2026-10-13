using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using Mithu.Core.Input;
using static Mithu.App.Interop.NativeMethods;

namespace Mithu.App.Services;

/// <summary>
/// System-wide shortcut via Win32 <c>RegisterHotKey</c>, delivered to a message-only window.
/// If another app already owns the combination, registration fails gracefully and is reported.
/// </summary>
public class GlobalHotkeyService(ILogger<GlobalHotkeyService> logger) : IDisposable
{
    /// <summary>Id of the registration (each shortcut service has its own).</summary>
    protected virtual int HotkeyId => 0x4D49; // "MI"

    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public HotkeyGesture? Current { get; private set; }

    /// <summary>Registers (or re-registers) the shortcut. Returns false if it's taken or invalid.</summary>
    public bool Register(string gestureText)
    {
        if (!HotkeyGesture.TryParse(gestureText, out var gesture))
        {
            logger.LogWarning("Invalid hotkey '{Hotkey}'", gestureText);
            return false;
        }

        EnsureSource();
        Unregister();

        if (!RegisterHotKey(_source!.Handle, HotkeyId, (uint)gesture.Modifiers | MOD_NOREPEAT, (uint)gesture.VirtualKey))
        {
            logger.LogWarning(
                "Hotkey {Hotkey} is already used by another app (Win32 error {Error})",
                gesture, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return false;
        }

        _registered = true;
        Current = gesture;
        logger.LogInformation("Registered global hotkey {Hotkey}", gesture);
        return true;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
        }

        _registered = false;
        Current = null;
    }

    private void EnsureSource()
    {
        if (_source is not null)
        {
            return;
        }

        // HWND_MESSAGE parent (-3) = invisible message-only window; costs nothing when idle.
        var parameters = new HwndSourceParameters("Mithu.Hotkey") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Unregister();
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
