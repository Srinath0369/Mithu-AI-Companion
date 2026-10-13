using Microsoft.Win32;
using Mithu.Infrastructure.Windows;

namespace Mithu.Tests;

/// <summary>Uses a throwaway HKCU key so the real Run key is never touched.</summary>
public sealed class RegistryStartupRegistrationTests : IDisposable
{
    private readonly string _key = $@"Software\MithuTests\{Guid.NewGuid():N}";

    [Fact]
    public void Enable_then_disable_round_trips()
    {
        var registration = new RegistryStartupRegistration(_key, "Mithu", @"C:\Apps\Mithu.exe");

        Assert.False(registration.IsEnabled);

        registration.SetEnabled(true);
        Assert.True(registration.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
        {
            Assert.Equal("\"C:\\Apps\\Mithu.exe\" --startup", key?.GetValue("Mithu"));
        }

        registration.SetEnabled(false);
        Assert.False(registration.IsEnabled);
    }

    [Fact]
    public void Entry_for_a_different_exe_is_not_considered_enabled()
    {
        new RegistryStartupRegistration(_key, "Mithu", @"C:\Old\Mithu.exe").SetEnabled(true);

        Assert.False(new RegistryStartupRegistration(_key, "Mithu", @"C:\New\Mithu.exe").IsEnabled);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);
}
