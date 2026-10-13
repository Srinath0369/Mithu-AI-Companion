using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mithu.Core.Focus;
using Mithu.Core.Layout;
using Mithu.Core.Models;
using Mithu.Infrastructure.Persistence;

namespace Mithu.Tests;

/// <summary>Settings and focus data in SQLite, including the one-time import from the old JSON files.</summary>
public sealed class SqliteStoresTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "mithu.db");

    private string SettingsJson => Path.Combine(_dir, "settings.json");

    private string FocusJson => Path.Combine(_dir, "focus.json");

    public SqliteStoresTests() => Directory.CreateDirectory(_dir);

    private SqliteDatabase NewDatabase() => new(DbPath, NullLogger<SqliteDatabase>.Instance);

    private SqliteSettingsStore NewSettings() => new(NewDatabase(), SettingsJson, NullLoggerFactory.Instance);

    private SqliteFocusStore NewFocus() => new(NewDatabase(), FocusJson, NullLoggerFactory.Instance);

    // ===== Settings ==========================================================

    [Fact]
    public void Fresh_settings_are_defaults()
    {
        // A brand-new install: defaults, and the first-run setup still to do (kept after a restart).
        Assert.Equal(new AppSettings { SetupDone = false }, NewSettings().Current);
        Assert.False(NewSettings().Current.SetupDone);
    }

    [Fact]
    public void Ai_settings_persist_and_reload()
    {
        NewSettings().Update(s => s with
        {
            Ai = new Core.Ai.AiSettings
            {
                Enabled = true, ConsentGiven = true, AllowNotes = true, PresetId = "ollama",
                Kind = Core.Ai.AiProviderKind.OpenAiCompatible, BaseUrl = "http://localhost:11434/v1", Model = "llama3",
            },
        });
        SqliteConnection.ClearAllPools();

        var ai = NewSettings().Current.Ai;
        Assert.True(ai.IsReady);
        Assert.Equal(("ollama", "llama3", Core.Ai.AiProviderKind.OpenAiCompatible), (ai.PresetId, ai.Model, ai.Kind));
        Assert.True(ai.AllowNotes);
    }

    [Fact]
    public void Settings_update_persists_and_reloads()
    {
        var placement = new WindowPlacement(@"\\.\DISPLAY2", 0.25, 0);
        NewSettings().Update(s => s with { ReducedMotion = true, LastMode = WindowMode.Hidden, Placement = placement });
        SqliteConnection.ClearAllPools();

        var reloaded = NewSettings().Current;

        Assert.True(reloaded.ReducedMotion);
        Assert.Equal(WindowMode.Hidden, reloaded.LastMode);
        Assert.Equal(placement, reloaded.Placement);
    }

    [Fact]
    public void Settings_changed_is_raised_only_when_different()
    {
        var store = NewSettings();
        var count = 0;
        store.Changed += (_, _) => count++;

        store.Update(s => s);
        store.Update(s => s with { QuietMode = true });
        store.Update(s => s with { QuietMode = true });

        Assert.Equal(1, count);
    }

    [Fact]
    public void Settings_json_is_imported_once_and_kept_as_migrated()
    {
        File.WriteAllText(SettingsJson, """
            { "version": 3, "dockToTop": false, "hotkey": "Alt+F2", "lastMode": "Expanded",
              "placement": { "monitorDeviceName": "\\\\.\\DISPLAY1", "anchorX": 0.3, "anchorY": 0 } }
            """);

        var imported = NewSettings().Current;
        SqliteConnection.ClearAllPools();

        Assert.False(imported.DockToTop);
        Assert.Equal("Alt+F2", imported.Hotkey);
        Assert.Equal(WindowMode.Expanded, imported.LastMode);
        Assert.Equal(0.3, imported.Placement!.AnchorX);
        Assert.True(imported.SetupDone); // someone who already used Mithu: no first-run setup
        Assert.False(File.Exists(SettingsJson));
        Assert.True(File.Exists(SettingsJson + ".migrated"));

        // Later runs read the database, not the old file.
        File.WriteAllText(SettingsJson, """{ "version": 3, "hotkey": "Ctrl+Alt+M" }""");
        Assert.Equal("Alt+F2", NewSettings().Current.Hotkey);
    }

    [Fact]
    public void Legacy_v1_settings_json_is_upgraded_while_importing()
    {
        File.WriteAllText(SettingsJson, """{ "lastMode": "Bar", "alwaysOnTop": false }""");

        var imported = NewSettings().Current;

        Assert.Equal(WindowMode.Compact, imported.LastMode); // "Bar" was renamed
        Assert.False(imported.AlwaysOnTop);
        Assert.Equal(AppSettings.CurrentVersion, imported.Version);
    }

    [Fact]
    public void Unusable_database_still_gives_defaults()
    {
        // A folder where the database file should be: SQLite can't open it.
        Directory.CreateDirectory(DbPath);

        var store = NewSettings();
        store.Update(s => s with { QuietMode = true }); // must not throw

        Assert.True(store.Current.QuietMode);
    }

    // ===== Focus ==============================================================

    [Fact]
    public void Fresh_focus_data_is_defaults()
    {
        Assert.Equal(new FocusData(), NewFocus().Load());
    }

    [Fact]
    public void Focus_data_round_trips_with_a_running_countdown()
    {
        var deadline = new DateTimeOffset(2026, 10, 12, 9, 25, 0, TimeSpan.Zero);
        var data = new FocusData
        {
            Active = new FocusSession(FocusKind.Focus, TimeSpan.FromMinutes(37), TimeSpan.FromMinutes(37), deadline, "Write report"),
            FocusLength = TimeSpan.FromMinutes(37),
            Rounds = 3,
            BreakLength = TimeSpan.FromMinutes(10),
            Round = 2,
        };

        NewFocus().Save(data);
        SqliteConnection.ClearAllPools();

        Assert.Equal(data, NewFocus().Load());
    }

    [Fact]
    public void Paused_countdown_round_trips_and_clearing_it_removes_it()
    {
        var paused = new FocusData { Active = new FocusSession(FocusKind.Break, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(83.5), null, null) };
        var store = NewFocus();

        store.Save(paused);
        Assert.Equal(paused, store.Load());

        store.Save(paused with { Active = null });
        Assert.Null(store.Load().Active);
    }

    [Fact]
    public void Focus_json_is_imported_once_and_kept_as_migrated()
    {
        File.WriteAllText(FocusJson, """
            { "active": { "kind": "Focus", "duration": "00:25:00", "remaining": "00:10:00", "deadlineUtc": null, "intention": "Plan" },
              "focusLength": "00:25:00", "rounds": 4, "breakLength": "00:05:00", "round": 1,
              "stats": { "day": "2026-10-11", "sessions": 2 } }
            """);

        var imported = NewFocus().Load();

        Assert.Equal(4, imported.Rounds);
        Assert.Equal(TimeSpan.FromMinutes(10), imported.Active!.Remaining);
        Assert.Equal("Plan", imported.Active.Intention);
        Assert.False(File.Exists(FocusJson));
        Assert.True(File.Exists(FocusJson + ".migrated"));
        Assert.Equal(imported, NewFocus().Load());
    }

    [Fact]
    public void Settings_and_focus_share_the_table_without_clashing()
    {
        NewSettings().Update(s => s with { Hotkey = "Alt+F3" });
        NewFocus().Save(new FocusData { Rounds = 6 });

        Assert.Equal("Alt+F3", NewSettings().Current.Hotkey);
        Assert.Equal(6, NewFocus().Load().Rounds);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
