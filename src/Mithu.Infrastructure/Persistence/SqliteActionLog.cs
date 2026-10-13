using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mithu.Core.Desktop;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Persistence;

/// <summary>Desktop control's action log in SQLite (v5 table). Never logs paths to the app log.</summary>
public sealed class SqliteActionLog(SqliteDatabase database) : IActionLog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public long Add(string chatId, string callId, string summary, IReadOnlyList<FileOp> ops, DateTimeOffset nowUtc)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO actions (chat_id, call_id, summary, ops_json, undone, created_at_utc)
            VALUES ($chat, $call, $summary, $ops, 0, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$chat", chatId);
        command.Parameters.AddWithValue("$call", callId);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$ops", JsonSerializer.Serialize(ops, Json));
        command.Parameters.AddWithValue("$created", SqliteNoteRepository.ToText(nowUtc));
        return (long)command.ExecuteScalar()!;
    }

    public IReadOnlyList<ActionRecord> ForChat(string chatId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, chat_id, call_id, summary, ops_json, undone, created_at_utc FROM actions WHERE chat_id = $chat ORDER BY id;";
        command.Parameters.AddWithValue("$chat", chatId);
        return Read(command);
    }

    public ActionRecord? Get(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, chat_id, call_id, summary, ops_json, undone, created_at_utc FROM actions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return Read(command).FirstOrDefault();
    }

    public void MarkUndone(long id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE actions SET undone = 1 WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static List<ActionRecord> Read(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<ActionRecord>();
        while (reader.Read())
        {
            list.Add(new ActionRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                SqliteNoteRepository.FromText(reader.GetString(6)),
                JsonSerializer.Deserialize<List<FileOp>>(reader.GetString(4), Json) ?? [],
                reader.GetInt64(5) != 0));
        }

        return list;
    }
}
