using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mithu.Core.Focus;
using Mithu.Core.Interfaces;
using Mithu.Infrastructure.Focus;

namespace Mithu.Infrastructure.Persistence;

/// <summary>
/// Focus timer data in the database: the user's choices (length, rounds, break, current round) as "focus.*" keys
/// in <c>settings</c>, and the running or paused countdown as the single row of <c>timer_sessions</c>.
/// On first run the old <c>focus.json</c> is imported and renamed to <c>focus.json.migrated</c>.
/// Failures are logged, never thrown (the timer keeps working in memory).
/// </summary>
public sealed class SqliteFocusStore(SqliteDatabase database, string legacyJsonPath, ILoggerFactory loggers) : IFocusStore
{
    private const string Prefix = "focus.";

    private readonly ILogger<SqliteFocusStore> _logger = loggers.CreateLogger<SqliteFocusStore>();

    public FocusData Load()
    {
        try
        {
            using var connection = database.Open();
            var choices = KeyValues.Read(connection, Prefix);
            if (choices is null)
            {
                return ImportLegacy();
            }

            var data = choices.Deserialize<FocusData>(JsonFile.Options) ?? new FocusData();
            return data with { Active = ReadActive(connection) };
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not load focus data; starting fresh");
            return new FocusData();
        }
    }

    public void Save(FocusData data) => TrySave(data);

    private FocusData ImportLegacy()
    {
        var hadFile = File.Exists(legacyJsonPath);
        var data = hadFile
            ? new JsonFocusStore(legacyJsonPath, loggers.CreateLogger<JsonFocusStore>()).Load()
            : new FocusData();

        if (TrySave(data) && hadFile)
        {
            KeyValues.MarkMigrated(legacyJsonPath);
            _logger.LogInformation("Focus data moved from focus.json into the database");
        }

        return data;
    }

    private bool TrySave(FocusData data)
    {
        try
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();

            var choices = JsonSerializer.SerializeToNode(data with { Active = null }, JsonFile.Options)!.AsObject();
            choices.Remove("active");
            KeyValues.Write(connection, transaction, Prefix, choices);

            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM timer_sessions;";
                clear.ExecuteNonQuery();
            }

            if (data.Active is { } active)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO timer_sessions (id, kind, duration_ms, remaining_ms, deadline_utc, intention)
                    VALUES (1, $kind, $duration, $remaining, $deadline, $intention);
                    """;
                insert.Parameters.AddWithValue("$kind", active.Kind.ToString());
                insert.Parameters.AddWithValue("$duration", (long)active.Duration.TotalMilliseconds);
                insert.Parameters.AddWithValue("$remaining", (long)active.Remaining.TotalMilliseconds);
                insert.Parameters.AddWithValue("$deadline", active.DeadlineUtc is { } deadline ? SqliteNoteRepository.ToText(deadline) : DBNull.Value);
                insert.Parameters.AddWithValue("$intention", (object?)active.Intention ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not save focus data");
            return false;
        }
    }

    private static FocusSession? ReadActive(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, duration_ms, remaining_ms, deadline_utc, intention FROM timer_sessions WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !Enum.TryParse<FocusKind>(reader.GetString(0), out var kind))
        {
            return null;
        }

        return new FocusSession(
            kind,
            TimeSpan.FromMilliseconds(reader.GetInt64(1)),
            TimeSpan.FromMilliseconds(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : SqliteNoteRepository.FromText(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }
}
