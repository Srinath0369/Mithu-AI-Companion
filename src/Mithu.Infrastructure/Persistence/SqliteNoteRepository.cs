using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Mithu.Core.Interfaces;
using Mithu.Core.Notes;

namespace Mithu.Infrastructure.Persistence;

/// <summary>
/// Notes in SQLite. Search uses FTS5 word-prefix matching over title and content ("rep" finds "report"),
/// plus tag-prefix matching; every typed word must match. Never logs note content.
/// </summary>
public sealed class SqliteNoteRepository(SqliteDatabase database) : INoteRepository
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public bool StorageWasReset => database.WasRecovered;

    private const string SelectColumns = """
        SELECT n.id, n.title, n.content, n.is_pinned, n.created_at_utc, n.updated_at_utc, n.is_archived,
               (SELECT group_concat(t.tag, ',') FROM note_tags t WHERE t.note_id = n.id) AS tags
        FROM notes n
        """;

    public IReadOnlyList<Note> Find(NoteQuery query)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder(SelectColumns).Append(" WHERE n.is_archived = $archived");
        command.Parameters.AddWithValue("$archived", query.Archived ? 1 : 0);

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM note_tags t WHERE t.note_id = n.id AND t.tag = $tag)");
            command.Parameters.AddWithValue("$tag", query.Tag.Trim().TrimStart('#'));
        }

        var words = SearchWords(query.Search);
        for (var i = 0; i < words.Count; i++)
        {
            // Each word: a word in the title/content starts with it, or a tag does.
            sql.Append($"""
                 AND (n.id IN (SELECT note_id FROM notes_fts WHERE notes_fts MATCH $match{i})
                      OR EXISTS (SELECT 1 FROM note_tags t WHERE t.note_id = n.id AND t.tag LIKE $tagLike{i} ESCAPE '\'))
                """);
            command.Parameters.AddWithValue($"$match{i}", $"\"{words[i]}\"*");
            command.Parameters.AddWithValue($"$tagLike{i}", EscapeLike(words[i]) + "%");
        }

        sql.Append(" ORDER BY n.is_pinned DESC, ").Append(query.Sort switch
        {
            NoteSort.Newest => "n.created_at_utc DESC",
            NoteSort.Oldest => "n.created_at_utc ASC",
            _ => "n.updated_at_utc DESC",
        }).Append(", n.id");

        command.CommandText = sql.ToString();
        return ReadNotes(command);
    }

    public Note? Get(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE n.id = $id";
        command.Parameters.AddWithValue("$id", id);
        return ReadNotes(command).FirstOrDefault();
    }

    public void Save(Note note)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO notes (id, title, content, is_pinned, is_archived, created_at_utc, updated_at_utc)
                VALUES ($id, $title, $content, $pinned, $archived, $created, $updated)
                ON CONFLICT (id) DO UPDATE SET
                    title = excluded.title,
                    content = excluded.content,
                    is_pinned = excluded.is_pinned,
                    is_archived = excluded.is_archived,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            upsert.Parameters.AddWithValue("$id", note.Id);
            upsert.Parameters.AddWithValue("$title", note.Title);
            upsert.Parameters.AddWithValue("$content", note.Content);
            upsert.Parameters.AddWithValue("$pinned", note.IsPinned ? 1 : 0);
            upsert.Parameters.AddWithValue("$archived", note.IsArchived ? 1 : 0);
            upsert.Parameters.AddWithValue("$created", ToText(note.CreatedUtc));
            upsert.Parameters.AddWithValue("$updated", ToText(note.UpdatedUtc));
            upsert.ExecuteNonQuery();
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM note_tags WHERE note_id = $id;";
            clear.Parameters.AddWithValue("$id", note.Id);
            clear.ExecuteNonQuery();
        }

        foreach (var tag in NoteText.NormalizeTags(note.Tags))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO note_tags (note_id, tag) VALUES ($id, $tag);";
            insert.Parameters.AddWithValue("$id", note.Id);
            insert.Parameters.AddWithValue("$tag", tag);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM notes WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<TagCount> GetTags(bool archived = false)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.tag, COUNT(*) FROM note_tags t JOIN notes n ON n.id = t.note_id
            WHERE n.is_archived = $archived
            GROUP BY t.tag ORDER BY COUNT(*) DESC, t.tag;
            """;
        command.Parameters.AddWithValue("$archived", archived ? 1 : 0);
        using var reader = command.ExecuteReader();
        var tags = new List<TagCount>();
        while (reader.Read())
        {
            tags.Add(new TagCount(reader.GetString(0), reader.GetInt32(1)));
        }

        return tags;
    }

    /// <summary>Words to search for, with characters that mean something to FTS5 removed.</summary>
    internal static IReadOnlyList<string> SearchWords(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return [];
        }

        var words = new List<string>();
        foreach (var raw in search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray()).Trim('-', '_');
            if (word.Length > 0)
            {
                words.Add(word);
            }
        }

        return words;
    }

    private static IReadOnlyList<Note> ReadNotes(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var notes = new List<Note>();
        while (reader.Read())
        {
            var tags = reader.IsDBNull(7)
                ? []
                : reader.GetString(7).Split(',', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();

            notes.Add(new Note(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                tags,
                reader.GetInt32(3) != 0,
                FromText(reader.GetString(4)),
                FromText(reader.GetString(5)),
                IsArchived: reader.GetInt32(6) != 0));
        }

        return notes;
    }

    internal static string ToText(DateTimeOffset time) => time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    internal static DateTimeOffset FromText(string text) =>
        DateTimeOffset.ParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
