using Mithu.Core.Reminders;

namespace Mithu.Core.Interfaces;

/// <summary>Reminders stored on this computer.</summary>
public interface IReminderRepository
{
    /// <summary>Every reminder (pending, completed and skipped). Lists are small, so filtering happens in memory.</summary>
    IReadOnlyList<Reminder> GetAll();

    Reminder? Get(string id);

    /// <summary>Inserts or updates, all in one transaction (e.g. a repeating reminder and its history entry).</summary>
    void Save(params IReadOnlyList<Reminder> reminders);

    void Delete(string id);

    /// <summary>The database was unreadable and a fresh one was started (the old file was kept).</summary>
    bool StorageWasReset { get; }
}
