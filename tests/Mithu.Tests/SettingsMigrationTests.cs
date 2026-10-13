using Microsoft.Extensions.Logging.Abstractions;
using Mithu.Core.Models;
using Mithu.Infrastructure.Settings;

namespace Mithu.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Legacy_v1_file_upgrades_names_and_enables_notch()
    {
        // Exactly what Phase 1 wrote: no "dockToTop", old size names.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """
            {
              "version": 1,
              "reducedMotion": true,
              "alwaysOnTop": true,
              "quietMode": false,
              "autoHideInFullscreen": true,
              "hotkey": "Ctrl+Alt+M",
              "lastMode": "Bar",
              "placement": { "monitorDeviceName": "\\\\.\\DISPLAY2", "anchorX": 0.3, "anchorY": 0 }
            }
            """);

        var s = new JsonSettingsStore(SettingsPath, NullLogger<JsonSettingsStore>.Instance).Current;

        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.Equal(WindowMode.Compact, s.LastMode);          // "Bar" → Compact
        Assert.Equal(WindowMode.Compact, s.LastVisibleMode);   // remembers what the user used
        Assert.True(s.DockToTop);                              // new notch design on by default
        Assert.True(s.CloseOnClickAway);
        Assert.True(s.ReducedMotion);                          // preferences kept
        Assert.Equal("Ctrl+Alt+M", s.Hotkey);
        Assert.Equal(@"\\.\DISPLAY2", s.Placement?.MonitorDeviceName);
    }

    [Fact]
    public void Legacy_file_without_version_is_treated_as_v1()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "lastMode": "Mini" }""");

        var s = new JsonSettingsStore(SettingsPath, NullLogger<JsonSettingsStore>.Instance).Current;

        Assert.Equal(WindowMode.Hidden, s.LastMode);           // "Mini" → Hidden
        Assert.Equal(WindowMode.Expanded, s.LastVisibleMode);  // hidden isn't a reveal target
        Assert.True(s.DockToTop);
    }

    [Fact]
    public void Migration_is_saved_so_it_runs_once()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, """{ "version": 1, "lastMode": "Bar" }""");

        _ = new JsonSettingsStore(SettingsPath, NullLogger<JsonSettingsStore>.Instance);
        var text = File.ReadAllText(SettingsPath);

        Assert.Contains($"\"version\": {AppSettings.CurrentVersion}", text);
        Assert.Contains("\"Compact\"", text);
    }

    [Fact]
    public void Current_version_user_choice_to_float_is_respected()
    {
        // A v2 user who turned docking off must not be forced back on.
        var migrated = SettingsMigrator.Migrate(new AppSettings { DockToTop = false }, out var changed);

        Assert.False(migrated.DockToTop);
        Assert.False(changed);
    }

    [Fact]
    public void Peek_is_never_restored()
    {
        var migrated = SettingsMigrator.Migrate(new AppSettings { LastMode = WindowMode.Peek }, out var changed);

        Assert.Equal(WindowMode.Compact, migrated.LastMode);
        Assert.True(changed);
    }

    [Fact]
    public void Upgrade_json_is_noop_for_current_files()
    {
        var json = """{"version":3,"lastMode":"Expanded"}""";

        Assert.Equal(json, JsonSettingsStore.UpgradeLegacyJson(json));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void V2_auto_hide_choice_carries_over_to_close_on_click_away(bool autoHide)
    {
        // A v2 user who turned auto-hide off keeps Mithu open when clicking elsewhere.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, $$"""{ "version": 2, "autoHideWhenDocked": {{(autoHide ? "true" : "false")}} }""");

        var s = new JsonSettingsStore(SettingsPath, NullLogger<JsonSettingsStore>.Instance).Current;

        Assert.Equal(autoHide, s.CloseOnClickAway);
        Assert.Equal(3, s.Version);
        Assert.DoesNotContain("autoHideWhenDocked", File.ReadAllText(SettingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
