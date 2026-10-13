using Microsoft.Win32;
using Mithu.Infrastructure.ClaudeCode;
using Mithu.Infrastructure.Notifications;
using Mithu.Infrastructure.Windows;

namespace Mithu.App.Services;

/// <summary>
/// Run by the uninstaller (Velopack, before it removes the program files): takes out everything Mithu put into
/// Windows and other apps — its Claude Code hooks, the launch-at-login entry, its notifications. The user's notes,
/// reminders, chats and settings in %LOCALAPPDATA%\Mithu are kept on purpose (docs/packaging.md). Each step is best
/// effort and quick (the uninstaller allows ~30 s).
/// </summary>
public static class UninstallCleanup
{
    public static void Run()
    {
        Try(() => new ClaudeCodeHooks(ClaudeCodeHooks.DefaultPath).Uninstall());
        Try(() =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryStartupRegistration.DefaultRunKey, writable: true);
            key?.DeleteValue("Mithu", throwOnMissingValue: false);
        });
        Try(ToastReminderNotifier.RemoveFromWindows);
    }

    private static void Try(Action step)
    {
        try
        {
            step();
        }
        catch (Exception)
        {
            // Best effort: one step failing must not stop the uninstall or the other steps.
        }
    }
}
