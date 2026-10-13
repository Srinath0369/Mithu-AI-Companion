using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Mithu.Infrastructure.Persistence;

/// <summary>
/// The <c>settings</c> table as groups of keys: an object's properties are stored one row each under a prefix
/// ("app.dockToTop" = "true"), with JSON text values. Adding a property later needs no schema change.
/// </summary>
internal static class KeyValues
{
    /// <summary>All rows under <paramref name="prefix"/> as one JSON object (prefix removed), or null if there are none.</summary>
    public static JsonObject? Read(SqliteConnection connection, string prefix)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings WHERE substr(key, 1, length($prefix)) = $prefix;";
        command.Parameters.AddWithValue("$prefix", prefix);
        using var reader = command.ExecuteReader();

        JsonObject? values = null;
        while (reader.Read())
        {
            values ??= [];
            values[reader.GetString(0)[prefix.Length..]] = JsonNode.Parse(reader.GetString(1));
        }

        return values;
    }

    /// <summary>Writes each property of <paramref name="values"/> as a row under <paramref name="prefix"/>.</summary>
    public static void Write(SqliteConnection connection, SqliteTransaction transaction, string prefix, JsonObject values)
    {
        foreach (var (name, value) in values)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO settings (key, value) VALUES ($key, $value)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", prefix + name);
            command.Parameters.AddWithValue("$value", value?.ToJsonString() ?? "null");
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Renames an imported JSON file to <c>name.migrated</c> so it is kept but no longer used.</summary>
    public static void MarkMigrated(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Move(path, path + ".migrated", overwrite: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
