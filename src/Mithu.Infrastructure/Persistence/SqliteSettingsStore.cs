using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mithu.Core.Interfaces;
using Mithu.Core.Models;
using Mithu.Infrastructure.Settings;

namespace Mithu.Infrastructure.Persistence;

/// <summary>
/// App settings in the database (<c>settings</c> table, keys "app.*"). On first run the old <c>settings.json</c> is
/// imported (with its legacy upgrades) and renamed to <c>settings.json.migrated</c>. If the database can't be used,
/// Mithu still starts with defaults and logs the problem.
/// </summary>
public sealed class SqliteSettingsStore : ISettingsStore
{
    private const string Prefix = "app.";

    private readonly SqliteDatabase _database;
    private readonly string _legacyPath;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<SqliteSettingsStore> _logger;
    private readonly Lock _gate = new();

    public SqliteSettingsStore(SqliteDatabase database, string legacyJsonPath, ILoggerFactory loggers)
    {
        _database = database;
        _legacyPath = legacyJsonPath;
        _loggers = loggers;
        _logger = loggers.CreateLogger<SqliteSettingsStore>();
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        AppSettings next;
        lock (_gate)
        {
            next = change(Current);
            if (next == Current)
            {
                return;
            }

            Current = next;
            Save(next);
        }

        Changed?.Invoke(this, next);
    }

    private AppSettings Load()
    {
        try
        {
            using var connection = _database.Open();
            var stored = KeyValues.Read(connection, Prefix);
            if (stored is null)
            {
                return ImportLegacy();
            }

            var json = JsonSettingsStore.UpgradeLegacyJson(stored.ToJsonString());
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonFile.Options) ?? new AppSettings();
            var migrated = SettingsMigrator.Migrate(loaded, out var changed);
            if (changed)
            {
                Save(migrated);
            }

            return migrated;
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not load settings from the database; using defaults");
            return new AppSettings();
        }
    }

    /// <summary>First run on the database: take over settings.json (if any), then retire it.</summary>
    private AppSettings ImportLegacy()
    {
        var hadFile = File.Exists(_legacyPath);
        var settings = hadFile
            ? new JsonSettingsStore(_legacyPath, _loggers.CreateLogger<JsonSettingsStore>()).Current
            : new AppSettings { SetupDone = false }; // a brand-new install: the first-run setup shows

        if (Save(settings) && hadFile)
        {
            KeyValues.MarkMigrated(_legacyPath);
            _logger.LogInformation("Settings moved from settings.json into the database");
        }

        return settings;
    }

    private bool Save(AppSettings settings)
    {
        try
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            KeyValues.Write(connection, transaction, Prefix, JsonSerializer.SerializeToNode(settings, JsonFile.Options)!.AsObject());
            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            // Settings are a convenience: keep running with the in-memory value.
            _logger.LogError(ex, "Could not save settings");
            return false;
        }
    }
}
