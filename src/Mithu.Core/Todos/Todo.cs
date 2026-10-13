namespace Mithu.Core.Todos;

/// <summary>A to-do list ("My tasks", "Work", "Shopping"…).</summary>
public sealed record TodoList(string Id, string Name, int Order, DateTimeOffset CreatedUtc)
{
    /// <summary>The list every task starts in, and where tasks go when their list is deleted.</summary>
    public const string DefaultId = "inbox";

    public const string DefaultName = "My tasks";

    public const int MaxNameLength = 40;

    public bool IsDefault => Id == DefaultId;

    public static TodoList Create(string name, int order, DateTimeOffset nowUtc) =>
        new(Guid.NewGuid().ToString("N"), CleanName(name), order, nowUtc);

    /// <summary>One line, trimmed, at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string name)
    {
        var clean = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].TrimEnd() : clean;
    }
}

/// <summary>
/// A task: words, done or not, starred or not. No dates or times: anything with a time is a reminder.
/// <see cref="Order"/> sorts open tasks (lower first; new tasks go on top).
/// </summary>
public sealed record TodoItem(
    string Id,
    string ListId,
    string Title,
    bool Important,
    bool Done,
    int Order,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset? DoneUtc)
{
    public const int MaxTitleLength = 200;

    public static TodoItem Create(string listId, string title, int order, DateTimeOffset nowUtc, bool important = false) =>
        new(Guid.NewGuid().ToString("N"), listId, Clean(title), important, false, order, nowUtc, nowUtc, null);

    /// <summary>Ticked or unticked (the time it was done is kept for the Done list).</summary>
    public TodoItem WithDone(bool done, DateTimeOffset nowUtc) =>
        this with { Done = done, DoneUtc = done ? nowUtc : null, UpdatedUtc = nowUtc };

    /// <summary>One line, trimmed, at most <see cref="MaxTitleLength"/> characters.</summary>
    public static string Clean(string title)
    {
        var clean = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > MaxTitleLength ? clean[..MaxTitleLength].TrimEnd() : clean;
    }
}

/// <summary>Pure rules for lists of tasks (ordering, finding a task by its words). Unit-tested.</summary>
public static class TodoRules
{
    /// <summary>Open tasks: starred first, then in the user's order; done tasks: most recently done first.</summary>
    public static IReadOnlyList<TodoItem> Sort(IEnumerable<TodoItem> items) =>
        items.OrderBy(i => i.Done)
            .ThenByDescending(i => !i.Done && i.Important)
            .ThenBy(i => i.Done ? 0 : i.Order)
            .ThenByDescending(i => i.Done ? i.DoneUtc : null)
            .ThenBy(i => i.CreatedUtc)
            .ToList();

    /// <summary>The order for a new task so it goes on top.</summary>
    public static int TopOrder(IEnumerable<TodoItem> items) =>
        items.Where(i => !i.Done).Select(i => i.Order).DefaultIfEmpty(1).Min() - 1;

    /// <summary>
    /// The task meant by some words ("the report" → "Finish the quarterly report"): an exact title first, then a title
    /// containing all the words. Open tasks before done ones. Null when nothing (or more than one equally) fits.
    /// </summary>
    public static TodoItem? Find(string words, IReadOnlyList<TodoItem> items)
    {
        var query = words.Trim();
        if (query.Length == 0)
        {
            return null;
        }

        var exact = items.Where(i => i.Title.Equals(query, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Done).FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        var parts = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Filler.Contains(w, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        var matches = items.Where(i => parts.All(p => i.Title.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        var open = matches.Where(i => !i.Done).ToList();
        var pool = open.Count > 0 ? open : matches;
        return pool.Count == 1 ? pool[0] : null;
    }

    /// <summary>Tasks with some of the words (to suggest when <see cref="Find"/> isn't sure).</summary>
    public static IReadOnlyList<TodoItem> Candidates(string words, IReadOnlyList<TodoItem> items) =>
        items.Where(i => words.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !Filler.Contains(w, StringComparer.OrdinalIgnoreCase))
                .Any(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(5)
            .ToList();

    private static readonly string[] Filler = ["the", "a", "an", "my", "to", "task", "todo", "to-do"];
}
