using Microsoft.Extensions.Logging.Abstractions;
using Mithu.Core.Layout;
using Mithu.Core.Models;
using Mithu.Infrastructure.Settings;

namespace Mithu.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private JsonSettingsStore NewStore() => new(SettingsPath, NullLogger<JsonSettingsStore>.Instance);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var store = NewStore();

        Assert.Equal(new AppSettings(), store.Current);
        Assert.Equal("Ctrl+Shift+Space", store.Current.Hotkey);
        Assert.True(store.Current.AlwaysOnTop);
    }

    [Fact]
    public void Update_persists_and_reloads()
    {
        var placement = new WindowPlacement(@"\\.\DISPLAY2", 0.25, 0.1);
        NewStore().Update(s => s with { ReducedMotion = true, LastMode = WindowMode.Hidden, Placement = placement });

        var reloaded = NewStore().Current;

        Assert.True(reloaded.ReducedMotion);
        Assert.Equal(WindowMode.Hidden, reloaded.LastMode);
        Assert.Equal(placement, reloaded.Placement);
    }

    [Fact]
    public void Update_raises_changed_only_when_different()
    {
        var store = NewStore();
        var count = 0;
        store.Changed += (_, _) => count++;

        store.Update(s => s);                        // identical
        store.Update(s => s with { QuietMode = true }); // changed
        store.Update(s => s with { QuietMode = true }); // identical again

        Assert.Equal(1, count);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_keeps_backup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var store = NewStore();

        Assert.Equal(new AppSettings(), store.Current);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Saved_file_uses_readable_enum_names()
    {
        NewStore().Update(s => s with { LastMode = WindowMode.Expanded });

        Assert.Contains("\"Expanded\"", File.ReadAllText(SettingsPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
