namespace Mithu.Core.Notes;

/// <summary>
/// A note. <see cref="Content"/> is plain text with a small Markdown subset
/// (<c>**bold**</c>, <c>*italic*</c>, <c># heading</c>, <c>- bullet</c>, <c>- [ ] checklist</c>).
/// Immutable: change with <c>with</c>. User content: never log title, content or tags.
/// </summary>
public sealed record Note(
    string Id,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    bool IsPinned,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    bool IsArchived = false)
{
    public static Note Create(DateTimeOffset nowUtc, string title = "", string content = "", IEnumerable<string>? tags = null) =>
        new(NewId(), title, content, NoteText.NormalizeTags(tags ?? []), IsPinned: false, nowUtc, nowUtc);

    /// <summary>Nothing written yet (such notes are never kept).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Content);

    /// <summary>Title to show: the title, else the first line of the content, else "Untitled note".</summary>
    public string DisplayTitle => NoteText.DisplayTitle(Title, Content);

    /// <summary>A new, unpinned, active copy ("Title (copy)") with fresh timestamps.</summary>
    public Note Duplicate(DateTimeOffset nowUtc) => this with
    {
        Id = NewId(),
        Title = Title.Trim().Length == 0 ? Title : $"{Title.Trim()} (copy)",
        IsPinned = false,
        IsArchived = false,
        CreatedUtc = nowUtc,
        UpdatedUtc = nowUtc,
    };

    /// <summary>What "Copy" puts on the clipboard: title, blank line, content.</summary>
    public string ToClipboardText() =>
        Title.Trim().Length == 0 ? Content.Trim() : $"{Title.Trim()}{Environment.NewLine}{Environment.NewLine}{Content.Trim()}".Trim();

    private static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>How the note list is ordered (pinned notes always come first).</summary>
public enum NoteSort
{
    LastModified,
    Newest,
    Oldest,
}

/// <summary>Which notes to list.</summary>
/// <param name="Search">Words to find in titles, content or tags (prefix match, all words must match); null = all.</param>
/// <param name="Tag">Only notes with this tag; null = any.</param>
/// <param name="Archived">List archived notes instead of active ones.</param>
public sealed record NoteQuery(string? Search = null, string? Tag = null, NoteSort Sort = NoteSort.LastModified, bool Archived = false);

/// <summary>A tag and how many notes use it.</summary>
public readonly record struct TagCount(string Tag, int Count);
