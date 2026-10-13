using Microsoft.Data.Sqlite;
using Mithu.Core.Ai;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Persistence;

/// <summary>AI chats in SQLite (v4 tables). Deleting a chat deletes its messages (foreign key cascade). Never logs content.</summary>
public sealed class SqliteChatRepository(SqliteDatabase database) : IChatRepository
{
    private const string SelectChats = """
        SELECT c.id, c.title, c.created_at_utc, c.updated_at_utc,
               (SELECT COUNT(*) FROM chat_messages m WHERE m.chat_id = c.id) AS n
        FROM chats c
        """;

    public IReadOnlyList<ChatThread> ListChats(string? search = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        var words = (search ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var where = new List<string>();
        for (var i = 0; i < words.Length; i++)
        {
            where.Add($"""
                (c.title LIKE $w{i} ESCAPE '\'
                 OR EXISTS (SELECT 1 FROM chat_messages m WHERE m.chat_id = c.id AND m.text LIKE $w{i} ESCAPE '\'))
                """);
            command.Parameters.AddWithValue($"$w{i}", "%" + EscapeLike(words[i]) + "%");
        }

        command.CommandText = SelectChats
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
            + " ORDER BY c.updated_at_utc DESC, c.id;";
        return ReadChats(command);
    }

    public ChatThread? GetChat(string chatId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectChats + " WHERE c.id = $id;";
        command.Parameters.AddWithValue("$id", chatId);
        return ReadChats(command).FirstOrDefault();
    }

    public IReadOnlyList<ChatEntry> GetMessages(string chatId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, role, parts_json, created_at_utc, model FROM chat_messages WHERE chat_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", chatId);
        using var reader = command.ExecuteReader();
        var list = new List<ChatEntry>();
        while (reader.Read())
        {
            var role = reader.GetString(1) == "assistant" ? AiRole.Assistant : AiRole.User;
            list.Add(new ChatEntry(
                reader.GetInt64(0),
                chatId,
                new AiMessage(role, AiMessageJson.Read(reader.GetString(2))),
                SqliteNoteRepository.FromText(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return list;
    }

    public void CreateChat(ChatThread chat)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO chats (id, title, created_at_utc, updated_at_utc) VALUES ($id, $title, $created, $updated);";
        command.Parameters.AddWithValue("$id", chat.Id);
        command.Parameters.AddWithValue("$title", chat.Title);
        command.Parameters.AddWithValue("$created", SqliteNoteRepository.ToText(chat.CreatedUtc));
        command.Parameters.AddWithValue("$updated", SqliteNoteRepository.ToText(chat.UpdatedUtc));
        command.ExecuteNonQuery();
    }

    public void AddMessages(string chatId, IReadOnlyList<AiMessage> messages, DateTimeOffset nowUtc, string? model = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var message in messages)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO chat_messages (chat_id, role, parts_json, text, model, created_at_utc)
                VALUES ($chat, $role, $parts, $text, $model, $created);
                """;
            insert.Parameters.AddWithValue("$chat", chatId);
            insert.Parameters.AddWithValue("$role", message.Role == AiRole.Assistant ? "assistant" : "user");
            insert.Parameters.AddWithValue("$parts", AiMessageJson.Write(message.Parts));
            insert.Parameters.AddWithValue("$text", message.Text);
            insert.Parameters.AddWithValue("$model", message.Role == AiRole.Assistant && model is not null ? model : DBNull.Value);
            insert.Parameters.AddWithValue("$created", SqliteNoteRepository.ToText(nowUtc));
            insert.ExecuteNonQuery();
        }

        using (var touch = connection.CreateCommand())
        {
            touch.Transaction = transaction;
            touch.CommandText = "UPDATE chats SET updated_at_utc = $now WHERE id = $id;";
            touch.Parameters.AddWithValue("$now", SqliteNoteRepository.ToText(nowUtc));
            touch.Parameters.AddWithValue("$id", chatId);
            touch.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void RenameChat(string chatId, string title) =>
        Execute("UPDATE chats SET title = $title WHERE id = $id;", ("$title", title), ("$id", chatId));

    public void DeleteChat(string chatId) => Execute("DELETE FROM chats WHERE id = $id;", ("$id", chatId));

    public void DeleteAllChats() => Execute("DELETE FROM chat_messages; DELETE FROM chats;");

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<ChatThread> ReadChats(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<ChatThread>();
        while (reader.Read())
        {
            list.Add(new ChatThread(
                reader.GetString(0),
                reader.GetString(1),
                SqliteNoteRepository.FromText(reader.GetString(2)),
                SqliteNoteRepository.FromText(reader.GetString(3)),
                reader.GetInt32(4)));
        }

        return list;
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
