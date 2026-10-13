using static Mithu.App.Interop.NativeMethods;

namespace Mithu.App.Services;

/// <summary>
/// Gentle "done" sound: the user's own Windows notification sound, so it matches their sound scheme
/// and stays silent if they turned system sounds off. Asynchronous; never blocks the UI.
/// </summary>
public static class SoftChime
{
    public static void Play() => PlaySound("Notification.Default", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT);
}
