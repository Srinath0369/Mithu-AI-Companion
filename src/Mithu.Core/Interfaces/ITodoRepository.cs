using Mithu.Core.Todos;

namespace Mithu.Core.Interfaces;

/// <summary>Stores to-do lists and tasks (SQLite). Failures throw so the UI can say so.</summary>
public interface ITodoRepository
{
    /// <summary>All lists, "My tasks" first, then in order.</summary>
    IReadOnlyList<TodoList> Lists();

    void SaveList(TodoList list);

    /// <summary>Deletes a list; its tasks move to "My tasks" (the default list can't be deleted).</summary>
    void DeleteList(string listId);

    /// <summary>Tasks of one list (or all lists when null).</summary>
    IReadOnlyList<TodoItem> Items(string? listId = null);

    void Save(TodoItem item);

    void Delete(string itemId);

    /// <summary>Deletes the done tasks of one list (or all); returns them (for Undo).</summary>
    IReadOnlyList<TodoItem> ClearDone(string? listId = null);
}
