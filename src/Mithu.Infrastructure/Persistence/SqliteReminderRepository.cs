using System.Globalization;
using Microsoft.Data.Sqlite;
using Mithu.Core.Interfaces;
using Mithu.Core.Reminders;

namespace Mithu.Infrastructure.Persistence;

/// <summary>Reminders in SQLite. Enums are stored by name so the file stays readable. Never logs titles or notes.</summary>
public sealed class SqliteReminderRepository(SqliteDatabase database) : IReminderRepository
{
    private const string LocalFormat = "yyyy-MM-dd'T'HH:mm:ss";

    private const string SelectColumns = """
        SELECT id, title, notes, start_local, time_zone_id, repeat_kind, repeat_every, repeat_unit, due_at_utc,
               snoozed_until_utc, priority, status, last_notified_at_utc, done_at_utc, created_at_utc, updated_at_utc
        FROM reminders
        """;

    public bool StorageWasReset => database.WasRecovered;

    public IReadOnlyList<Reminder> GetAll()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " ORDER BY due_at_utc, id;";
        return Read(command);
    }

    public Reminder? Get(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return Read(command).FirstOrDefault();
    }

    public void Save(params IReadOnlyList<Reminder> reminders)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var r in reminders)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reminders (id, title, notes, start_local, time_zone_id, repeat_kind, repeat_every, repeat_unit,
                    due_at_utc, snoozed_until_utc, priority, status, last_notified_at_utc, done_at_utc, created_at_utc, updated_at_utc)
                VALUES ($id, $title, $notes, $start, $zone, $kind, $every, $unit, $due, $snoozed, $priority, $status,
                    $notified, $done, $created, $updated)
                ON CONFLICT (id) DO UPDATE SET
                    title = excluded.title, notes = excluded.notes, start_local = excluded.start_local,
                    time_zone_id = excluded.time_zone_id, repeat_kind = excluded.repeat_kind,
                    repeat_every = excluded.repeat_every, repeat_unit = excluded.repeat_unit,
                    due_at_utc = excluded.due_at_utc, snoozed_until_utc = excluded.snoozed_until_utc,
                    priority = excluded.priority, status = excluded.status,
                    last_notified_at_utc = excluded.last_notified_at_utc, done_at_utc = excluded.done_at_utc,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            var p = command.Parameters;
            p.AddWithValue("$id", r.Id);
            p.AddWithValue("$title", r.Title);
            p.AddWithValue("$notes", r.Notes);
            p.AddWithValue("$start", r.StartLocal.ToString(LocalFormat, CultureInfo.InvariantCulture));
            p.AddWithValue("$zone", r.TimeZoneId);
            p.AddWithValue("$kind", r.Repeat.Kind.ToString());
            p.AddWithValue("$every", r.Repeat.Every);
            p.AddWithValue("$unit", r.Repeat.Unit.ToString());
            p.AddWithValue("$due", SqliteNoteRepository.ToText(r.DueUtc));
            p.AddWithValue("$snoozed", Nullable(r.SnoozedUntilUtc));
            p.AddWithValue("$priority", r.Priority.ToString());
            p.AddWithValue("$status", r.Status.ToString());
            p.AddWithValue("$notified", Nullable(r.NotifiedUtc));
            p.AddWithValue("$done", Nullable(r.DoneUtc));
            p.AddWithValue("$created", SqliteNoteRepository.ToText(r.CreatedUtc));
            p.AddWithValue("$updated", SqliteNoteRepository.ToText(r.UpdatedUtc));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Delete(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reminders WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<Reminder> Read(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var list = new List<Reminder>();
        while (reader.Read())
        {
            var repeat = new Repeat(
                Enum<RepeatKind>(reader.GetString(5), RepeatKind.None),
                Math.Clamp(reader.GetInt32(6), 1, Repeat.MaxEvery),
                Enum<RepeatUnit>(reader.GetString(7), RepeatUnit.Days));

            list.Add(new Reminder(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTime.ParseExact(reader.GetString(3), LocalFormat, CultureInfo.InvariantCulture, DateTimeStyles.None),
                reader.GetString(4),
                repeat,
                SqliteNoteRepository.FromText(reader.GetString(8)),
                Enum<ReminderPriority>(reader.GetString(10), ReminderPriority.Normal),
                Enum<ReminderStatus>(reader.GetString(11), ReminderStatus.Pending),
                SqliteNoteRepository.FromText(reader.GetString(14)),
                SqliteNoteRepository.FromText(reader.GetString(15)),
                SnoozedUntilUtc: Time(reader, 9),
                NotifiedUtc: Time(reader, 12),
                DoneUtc: Time(reader, 13)));
        }

        return list;
    }

    private static object Nullable(DateTimeOffset? time) => time is { } t ? SqliteNoteRepository.ToText(t) : DBNull.Value;

    private static DateTimeOffset? Time(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : SqliteNoteRepository.FromText(reader.GetString(column));

    /// <summary>An unknown name (e.g. from a newer version) falls back instead of failing the whole list.</summary>
    private static T Enum<T>(string text, T fallback) where T : struct, Enum =>
        System.Enum.TryParse<T>(text, ignoreCase: true, out var value) && System.Enum.IsDefined(value) ? value : fallback;
}
