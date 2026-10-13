using Microsoft.Win32;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Windows;

/// <summary>
/// Launch-at-login via the per-user Run key (no admin rights needed).
/// Replaced by an MSIX StartupTask when the app is packaged (Phase 4).
/// </summary>
public sealed class RegistryStartupRegistration : IStartupRegistration
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _keyPath;
    private readonly string _valueName;
    private readonly string _command;

    public RegistryStartupRegistration()
        : this(DefaultRunKey, "Mithu", Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path."))
    {
    }

    public RegistryStartupRegistration(string keyPath, string valueName, string executablePath)
    {
        _keyPath = keyPath;
        _valueName = valueName;
        _command = $"\"{executablePath}\" --startup";
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return string.Equals(key?.GetValue(_valueName) as string, _command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        if (enabled)
        {
            key.SetValue(_valueName, _command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }
}
