using Microsoft.Data.Sqlite;
using Mithu.Core.Interfaces;
using Mithu.Core.Todos;

namespace Mithu.Infrastructure.Persistence;

/// <summary>To-do lists and tasks in SQLite (v6 tables). Never logs task text.</summary>
public sealed class SqliteTodoRepository(SqliteDatabase database) : ITodoRepository
{
    private const string SelectItems = "SELECT id, list_id, title, important, done, sort_order, created_at_utc, updated_at_utc, done_at_utc FROM todos";

    public IReadOnlyList<TodoList> Lists()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, sort_order, created_at_utc FROM todo_lists ORDER BY (id = 'inbox') DESC, sort_order, created_at_utc;";
        using var reader = command.ExecuteReader();
        var lists = new List<TodoList>();
        while (reader.Read())
        {
            lists.Add(new TodoList(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), SqliteNoteRepository.FromText(reader.GetString(3))));
        }

        // "My tasks" always exists (a damaged or hand-edited database may have lost it).
        if (!lists.Any(l => l.IsDefault))
        {
            var inbox = new TodoList(TodoList.DefaultId, TodoList.DefaultName, 0, DateTimeOffset.UtcNow);
            SaveList(inbox);
            lists.Insert(0, inbox);
        }

        return lists;
    }

    public void SaveList(TodoList list)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO todo_lists (id, name, sort_order, created_at_utc) VALUES ($id, $name, $order, $created)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, sort_order = excluded.sort_order;
            """;
        command.Parameters.AddWithValue("$id", list.Id);
        command.Parameters.AddWithValue("$name", list.Name);
        command.Parameters.AddWithValue("$order", list.Order);
        command.Parameters.AddWithValue("$created", SqliteNoteRepository.ToText(list.CreatedUtc));
        command.ExecuteNonQuery();
    }

    public void DeleteList(string listId)
    {
        if (listId == TodoList.DefaultId)
        {
            throw new InvalidOperationException("\"My tasks\" can't be deleted.");
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var move = connection.CreateCommand())
        {
            move.Transaction = transaction;
            move.CommandText = "UPDATE todos SET list_id = 'inbox' WHERE list_id = $id;";
            move.Parameters.AddWithValue("$id", listId);
            move.ExecuteNonQuery();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM todo_lists WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", listId);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<TodoItem> Items(string? listId = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectItems + (listId is null ? ";" : " WHERE list_id = $list;");
        if (listId is not null)
        {
            command.Parameters.AddWithValue("$list", listId);
        }

        return ReadItems(command);
    }

    public void Save(TodoItem item)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO todos (id, list_id, title, important, done, sort_order, created_at_utc, updated_at_utc, done_at_utc)
            VALUES ($id, $list, $title, $important, $done, $order, $created, $updated, $doneAt)
            ON CONFLICT(id) DO UPDATE SET list_id = excluded.list_id, title = excluded.title, important = excluded.important,
                done = excluded.done, sort_order = excluded.sort_order, updated_at_utc = excluded.updated_at_utc,
                done_at_utc = excluded.done_at_utc;
            """;
        var p = command.Parameters;
        p.AddWithValue("$id", item.Id);
        p.AddWithValue("$list", item.ListId);
        p.AddWithValue("$title", item.Title);
        p.AddWithValue("$important", item.Important ? 1 : 0);
        p.AddWithValue("$done", item.Done ? 1 : 0);
        p.AddWithValue("$order", item.Order);
        p.AddWithValue("$created", SqliteNoteRepository.ToText(item.CreatedUtc));
        p.AddWithValue("$updated", SqliteNoteRepository.ToText(item.UpdatedUtc));
        p.AddWithValue("$doneAt", item.DoneUtc is { } done ? SqliteNoteRepository.ToText(done) : DBNull.Value);
        command.ExecuteNonQuery();
    }

    public void Delete(string itemId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM todos WHERE id = $id;";
        command.Parameters.AddWithValue("$id", itemId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TodoItem> ClearDone(string? listId = null)
    {
        var done = Items(listId).Where(i => i.Done).ToList();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var item in done)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM todos WHERE id = $id;";
            command.Parameters.AddWithValue("$id", item.Id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return done;
    }

    private static List<TodoItem> ReadItems(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<TodoItem>();
        while (reader.Read())
        {
            list.Add(new TodoItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                reader.GetInt64(4) != 0,
                reader.GetInt32(5),
                SqliteNoteRepository.FromText(reader.GetString(6)),
                SqliteNoteRepository.FromText(reader.GetString(7)),
                reader.IsDBNull(8) ? null : SqliteNoteRepository.FromText(reader.GetString(8))));
        }

        return list;
    }
}
