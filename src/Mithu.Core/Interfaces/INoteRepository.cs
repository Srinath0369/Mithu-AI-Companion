using Mithu.Core.Notes;

namespace Mithu.Core.Interfaces;

/// <summary>
/// Stores notes (SQLite). Methods are synchronous and may block briefly: call them off the UI thread.
/// Failures throw, so the UI can show a visible "couldn't save" state.
/// </summary>
public interface INoteRepository
{
    /// <summary>Notes matching the query, pinned first, then in the requested order.</summary>
    IReadOnlyList<Note> Find(NoteQuery query);

    Note? Get(string id);

    /// <summary>Inserts or updates the note (and its tags) in one transaction.</summary>
    void Save(Note note);

    void Delete(string id);

    /// <summary>Tags in use by active (or, with <paramref name="archived"/>, archived) notes, most used first.</summary>
    IReadOnlyList<TagCount> GetTags(bool archived = false);

    /// <summary>The stored notes were unreadable, so storage started fresh (the damaged file was kept aside).</summary>
    bool StorageWasReset { get; }
}
