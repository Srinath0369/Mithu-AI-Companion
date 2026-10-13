using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mithu.Core.Interfaces;
using Mithu.Core.Models;

namespace Mithu.Infrastructure.Settings;

/// <summary>
/// Small JSON settings file. Writes atomically (temp file + rename) so a crash can't leave half a file.
/// A corrupt file is kept as a backup and defaults are used. Moves into SQLite in Phase 2.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static JsonSerializerOptions JsonOptions => JsonFile.Options;

    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly Lock _gate = new();

    public JsonSettingsStore(string path, ILogger<JsonSettingsStore> logger)
    {
        _path = path;
        _logger = logger;
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
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = UpgradeLegacyJson(File.ReadAllText(_path));
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            var migrated = SettingsMigrator.Migrate(loaded, out var changed);
            if (changed)
            {
                _logger.LogInformation("Migrated settings from v{From} to v{To}", loaded.Version, migrated.Version);
                Save(migrated);
            }

            return migrated;
        }
        catch (JsonException ex)
        {
            var backup = JsonFile.MoveAside(_path);
            _logger.LogWarning(ex, "Settings file was corrupt; using defaults. Backup: {Backup}", backup);
            return new AppSettings();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read settings; using defaults");
            return new AppSettings();
        }
    }

    /// <summary>
    /// v1 files have no "version" (defaults would wrongly read as current) and use the old size names.
    /// </summary>
    internal static string UpgradeLegacyJson(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
        if (node is null)
        {
            return json;
        }

        node["version"] ??= 1;

        // v3: "autoHideWhenDocked" became "closeOnClickAway" (same intent: get out of the way when not in use).
        if (node["autoHideWhenDocked"] is { } autoHide)
        {
            node.Remove("autoHideWhenDocked");
            node["closeOnClickAway"] ??= autoHide.GetValue<bool>();
        }
        foreach (var key in new[] { "lastMode", "lastVisibleMode" })
        {
            var value = node[key]?.GetValue<string>();
            var renamed = value switch
            {
                "Mini" => "Hidden",
                "Bar" => "Compact",
                _ => null,
            };

            if (renamed is not null)
            {
                node[key] = renamed;
            }
        }

        return node.ToJsonString();
    }

    private void Save(AppSettings settings)
    {
        try
        {
            JsonFile.WriteAtomic(_path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience: keep running with the in-memory value.
            _logger.LogError(ex, "Could not save settings");
        }
    }
}
