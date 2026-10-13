using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mithu.Core.Interfaces;
using Mithu.Core.Notes;

namespace Mithu.App.ViewModels;

/// <summary>A note as shown in the list.</summary>
public sealed class NoteItem(Note note, string when)
{
    public Note Note { get; } = note;

    public string Title => Note.DisplayTitle;

    public string Preview => NoteText.Preview(Note.Title, Note.Content);

    public bool HasPreview => Preview.Length > 0;

    public string TagsText => string.Join("  ", Note.Tags.Select(t => "#" + t));

    public bool HasTags => Note.Tags.Count > 0;

    public bool IsPinned => Note.IsPinned;

    /// <summary>"5 min ago", "Yesterday"…</summary>
    public string When { get; } = when;
}

/// <summary>A tag chip above the list ("All", "#work 3").</summary>
public sealed partial class TagFilter(string? tag, string label, Action<TagFilter> select) : ObservableObject
{
    /// <summary>The tag, or null for "All".</summary>
    public string? Tag { get; } = tag;

    public string Label { get; } = label;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => select(this);
}

public enum NoteSaveState
{
    Saved,
    Saving,
    Failed,
}

/// <summary>
/// Notes: the list (instant search, tag filter, sort, pinned first) and the editor (autosave while typing,
/// pin, copy, duplicate, delete with undo, checklists you can tick), plus Quick Note from the Home input.
/// Database work runs off the UI thread; saves are serialised. Never logs note content.
/// </summary>
public sealed partial class NotesViewModel : ObservableObject
{
    private static readonly TimeSpan AutosaveDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(6);

    private readonly INoteRepository _repository;
    private readonly TimeProvider _time;
    private readonly ILogger<NotesViewModel> _logger;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private Note? _current;       // note in the editor, as last saved (or just created)
    private bool _isNew;          // not stored yet (empty new notes are never stored)
    private bool _dirty;          // text/tags changed since the last save
    private bool _pinDirty;       // pin changed since the last save
    private bool _loading;        // filling the editor: not a user edit
    private bool _loadingTags;    // moving typed text into chips: not an edit by itself
    private bool _recoveryAnnounced;
    private int _reloadVersion;
    private string? _selectedTag;
    private Note? _undoNote;     // state to put back if the user taps Undo

    public NotesViewModel(INoteRepository repository, TimeProvider time, ILogger<NotesViewModel> logger)
    {
        _repository = repository;
        _time = time;
        _logger = logger;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = AutosaveDelay };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveNowAsync();
        };

        _searchTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = SearchDelay };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await ReloadAsync();
        };

        _undoTimer = new DispatcherTimer { Interval = UndoDuration };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            _undoNote = null;
            CanUndo = false;
        };
    }

    /// <summary>A short line for Mithu's status text.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>A note was opened in the editor (true = a new, empty one).</summary>
    public event Action<bool>? NoteOpened;

    // ===== List =============================================================

    public ObservableCollection<NoteItem> Items { get; } = [];

    public ObservableCollection<TagFilter> Tags { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearch))]
    private string _search = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortText))]
    private NoteSort _sort = NoteSort.LastModified;

    public bool HasSearch => Search.Length > 0;

    public bool HasNotes => Items.Count > 0;

    public bool HasTags => Tags.Count > 1;

    public string SortText => Sort switch
    {
        NoteSort.Newest => "Newest",
        NoteSort.Oldest => "Oldest",
        _ => "Last edited",
    };

    /// <summary>Listing archived notes instead of the active ones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListTitle), nameof(CanCreate))]
    private bool _showArchived;

    public string ListTitle => ShowArchived ? "Archived" : "Notes";

    /// <summary>New notes are made from the active list only.</summary>
    public bool CanCreate => !ShowArchived;

    public string EmptyText => HasSearch || _selectedTag is not null ? "No notes match."
        : ShowArchived ? "No archived notes.\nArchive a note from its ⋯ menu to tidy it away."
        : "No notes yet.\nType on Home and press Enter, or tap New.";

    // ===== Editor ===========================================================

    [ObservableProperty]
    private bool _isEditing;


    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    /// <summary>Tags of the open note, shown as chips.</summary>
    public ObservableCollection<string> EditorTags { get; } = [];

    /// <summary>What is being typed in the tag box; a comma turns it into a chip straight away.</summary>
    [ObservableProperty]
    private string _tagDraft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveText), nameof(SaveFailed))]
    private NoteSaveState _saveState;

    [ObservableProperty]
    private bool _canUndo;


    public string ArchiveLabel => _current?.IsArchived == true ? "Unarchive" : "Archive";

    /// <summary>"Note deleted", "Note archived"… shown next to Undo.</summary>
    [ObservableProperty]
    private string _undoText = string.Empty;

    public string PinLabel => IsPinned ? "Unpin" : "Pin to top";

    public bool SaveFailed => SaveState == NoteSaveState.Failed;

    /// <summary>"Saving…", "Saved · just now", "Couldn't save — retrying".</summary>
    public string SaveText => SaveState switch
    {
        NoteSaveState.Saving => "Saving…",
        NoteSaveState.Failed => "Couldn't save — retrying",
        _ when _isNew || _current is null => string.Empty,
        _ => $"Saved · {NoteText.Ago(_current.UpdatedUtc, _time.GetLocalNow())}",
    };

    partial void OnTitleChanged(string value) => MarkDirty();

    partial void OnContentChanged(string value) => MarkDirty();

    partial void OnTagDraftChanged(string value)
    {
        if (value.Contains(','))
        {
            CommitTags(final: false);
            return;
        }

        // A half-typed tag is saved too (it becomes a chip on comma, Enter, or leaving the note).
        if (!_loadingTags)
        {
            MarkDirty();
        }
    }

    partial void OnSearchChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnSortChanged(NoteSort value) => _ = ReloadAsync();

    // ===== Commands: list ===================================================

    /// <summary>The Notes page is being shown: refresh the list.</summary>
    public void Refresh() => _ = ReloadAsync();

    [RelayCommand]
    private void ClearSearch() => Search = string.Empty;

    [RelayCommand]
    private void SetSort(NoteSort sort) => Sort = sort;

    [RelayCommand]
    private async Task NewNoteAsync()
    {
        await SaveNowAsync();
        var note = Note.Create(_time.GetUtcNow(), tags: _selectedTag is null ? null : [_selectedTag]);
        LoadEditor(note, isNew: true);
    }

    [RelayCommand]
    private async Task OpenAsync(NoteItem item)
    {
        await SaveNowAsync();
        LoadEditor(item.Note, isNew: false);
    }

    [RelayCommand]
    private async Task ItemPinAsync(NoteItem item) =>
        await RunAsync(() => _repository.Save(item.Note with { IsPinned = !item.Note.IsPinned }),
            item.Note.IsPinned ? "Unpinned" : "Pinned to the top", reload: true);

    [RelayCommand]
    private async Task ItemDuplicateAsync(NoteItem item)
    {
        var copy = item.Note.Duplicate(_time.GetUtcNow());
        await RunAsync(() => _repository.Save(copy), "Duplicated", reload: true);
    }

    [RelayCommand]
    private void ItemCopy(NoteItem item) => CopyToClipboard(item.Note);

    [RelayCommand]
    private async Task ItemDeleteAsync(NoteItem item)
    {
        if (await RunAsync(() => _repository.Delete(item.Note.Id), "Note deleted", reload: true))
        {
            OfferUndo("Note deleted", item.Note);
        }
    }

    /// <summary>Archive (or, in the archive, unarchive) a note from its menu. Doesn't count as an edit.</summary>
    [RelayCommand]
    private async Task ItemArchiveAsync(NoteItem item) => await SetArchivedAsync(item.Note, !item.Note.IsArchived);

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_undoNote is not { } note)
        {
            return;
        }

        _undoTimer.Stop();
        _undoNote = null;
        CanUndo = false;
        await RunAsync(() => _repository.Save(note), "Undone", reload: true);
    }

    /// <summary>Archive button in the list header, and ← Back from the archive.</summary>
    [RelayCommand]
    private void ToggleArchived() => ShowArchived = !ShowArchived;

    partial void OnShowArchivedChanged(bool value)
    {
        _selectedTag = null;
        _ = ReloadAsync();
    }

    // ===== Commands: editor =================================================

    /// <summary>Back to the list: saves first, and drops the note if it ended up empty.</summary>
    [RelayCommand]
    private async Task CloseEditorAsync()
    {
        CommitTags(final: true);
        await SaveNowAsync();
        if (_current is { } note && !_isNew && BuildNote(touch: false).IsEmpty)
        {
            await RunAsync(() => _repository.Delete(note.Id), null, reload: false);
        }

        _current = null;
        IsEditing = false;
        await ReloadAsync();
    }

    /// <summary>✕ on a tag chip.</summary>
    [RelayCommand]
    private void RemoveTag(string tag)
    {
        if (EditorTags.Remove(tag))
        {
            MarkDirty();
        }
    }

    /// <summary>Backspace in an empty tag box removes the last chip.</summary>
    public void RemoveLastTag()
    {
        if (TagDraft.Length == 0 && EditorTags.Count > 0)
        {
            EditorTags.RemoveAt(EditorTags.Count - 1);
            MarkDirty();
        }
    }

    /// <summary>
    /// Turns typed tag text into chips: everything before the last comma (or all of it when <paramref name="final"/>,
    /// e.g. Enter or leaving the box). Tags are cleaned and duplicates ignored.
    /// </summary>
    public void CommitTags(bool final)
    {
        var parts = TagDraft.Split(',');
        var done = final ? parts : parts[..^1];
        var added = false;
        foreach (var tag in NoteText.NormalizeTags(done))
        {
            if (!EditorTags.Contains(tag) && EditorTags.Count < NoteText.MaxTags)
            {
                EditorTags.Add(tag);
                added = true;
            }
        }

        _loadingTags = true;
        TagDraft = final ? string.Empty : parts[^1].TrimStart();
        _loadingTags = false;
        if (added)
        {
            MarkDirty();
        }
    }

    [RelayCommand]
    private async Task TogglePinAsync()
    {
        IsPinned = !IsPinned;
        _pinDirty = true;
        await SaveNowAsync();
        Say(IsPinned ? "Pinned to the top" : "Unpinned");
    }

    [RelayCommand]
    private void Copy()
    {
        if (_current is not null)
        {
            CopyToClipboard(BuildNote(touch: false));
        }
    }

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (_current is null)
        {
            return;
        }

        _dirty |= _isNew;
        await SaveNowAsync();
        var copy = BuildNote(touch: false).Duplicate(_time.GetUtcNow());
        if (await RunAsync(() => _repository.Save(copy), "Duplicated — you're editing the copy", reload: false))
        {
            LoadEditor(copy, isNew: false);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_current is not { } note)
        {
            return;
        }

        _saveTimer.Stop();
        _dirty = _pinDirty = false;
        var wasNew = _isNew;
        var snapshot = BuildNote(touch: false);
        _current = null;
        IsEditing = false;

        if (wasNew)
        {
            await ReloadAsync();
            return;
        }

        if (await RunAsync(() => _repository.Delete(note.Id), "Note deleted", reload: true))
        {
            OfferUndo("Note deleted", snapshot);
        }
    }

    /// <summary>Editor ⋯ → Archive / Unarchive: saves, closes the note and offers Undo.</summary>
    [RelayCommand]
    private async Task ArchiveAsync()
    {
        if (_current is null)
        {
            return;
        }

        _dirty |= _isNew;
        await SaveNowAsync();
        if (_isNew)
        {
            // Nothing written: there is nothing to archive.
            await CloseEditorAsync();
            return;
        }

        var note = BuildNote(touch: false);
        _current = null;
        IsEditing = false;
        await SetArchivedAsync(note, !note.IsArchived);
    }

    private async Task SetArchivedAsync(Note note, bool archived)
    {
        if (await RunAsync(() => _repository.Save(note with { IsArchived = archived }),
                archived ? "Archived" : "Back in your notes", reload: true))
        {
            OfferUndo(archived ? "Note archived" : "Note unarchived", note);
        }
    }

    // ===== Quick Note =======================================================

    /// <summary>Saves <paramref name="text"/> as a new note (#hashtags become tags). False if it couldn't be saved.</summary>
    public async Task<bool> QuickSaveAsync(string text)
    {
        var content = text.Trim();
        if (content.Length == 0)
        {
            return false;
        }

        var note = Note.Create(_time.GetUtcNow(), content: content, tags: NoteText.Hashtags(content));
        var saved = await RunAsync(() => _repository.Save(note), null, reload: IsVisibleList);
        Say(saved ? "Saved to Notes ✓" : "Couldn't save the note — your text is kept.");
        if (saved)
        {
            _logger.LogInformation("Quick note saved ({Tags} tags)", note.Tags.Count);
        }

        return saved;
    }

    /// <summary>Saves any pending edit right now, on this thread (used at exit).</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        if (_current is null || !(_dirty || _pinDirty))
        {
            return;
        }

        var note = BuildNote(touch: _dirty);
        if (_isNew && note.IsEmpty)
        {
            return;
        }

        try
        {
            _repository.Save(note);
            _dirty = _pinDirty = false;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not save note at exit");
        }
    }

    // ===== Internals ========================================================

    private bool IsVisibleList => !IsEditing;

    private void LoadEditor(Note note, bool isNew)
    {
        _loading = true;
        _current = note;
        _isNew = isNew;
        _dirty = _pinDirty = false;
        Title = note.Title;
        Content = note.Content;
        EditorTags.Clear();
        foreach (var tag in note.Tags)
        {
            EditorTags.Add(tag);
        }

        TagDraft = string.Empty;
        IsPinned = note.IsPinned;
        SaveState = NoteSaveState.Saved;
        _loading = false;

        IsEditing = true;
        OnPropertyChanged(nameof(ArchiveLabel));
        OnPropertyChanged(nameof(SaveText));
        NoteOpened?.Invoke(isNew);
    }

    private void MarkDirty()
    {
        if (_loading || _current is null)
        {
            return;
        }

        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Interval = AutosaveDelay;
        _saveTimer.Start();
    }

    /// <summary>The editor's current state as a note. <paramref name="touch"/> = it was edited now.</summary>
    private Note BuildNote(bool touch) => _current! with
    {
        Title = Title,
        Content = Content,
        Tags = NoteText.NormalizeTags(EditorTags.Append(TagDraft)), // a half-typed tag counts too
        IsPinned = IsPinned,
        UpdatedUtc = touch ? _time.GetUtcNow() : _current.UpdatedUtc,
    };

    /// <summary>Saves pending changes. Empty new notes are not stored. A failed save retries after a moment.</summary>
    private async Task SaveNowAsync()
    {
        _saveTimer.Stop();
        if (_current is null || !(_dirty || _pinDirty))
        {
            return;
        }

        var note = BuildNote(touch: _dirty);
        if (_isNew && note.IsEmpty)
        {
            _dirty = _pinDirty = false;
            return;
        }

        _dirty = _pinDirty = false;
        SaveState = NoteSaveState.Saving;
        await _saveGate.WaitAsync();
        try
        {
            await Task.Run(() => _repository.Save(note));

            // Still the same note in the editor? (The user may have moved on.)
            if (_current?.Id == note.Id)
            {
                _current = note;
                _isNew = false;
                SaveState = NoteSaveState.Saved;
                        OnPropertyChanged(nameof(SaveText));
            }

            AnnounceRecovery();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not save note");
            if (_current?.Id == note.Id)
            {
                _dirty = true;
                SaveState = NoteSaveState.Failed;
                _saveTimer.Interval = RetryDelay;
                _saveTimer.Start();
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task ReloadAsync()
    {
        var version = ++_reloadVersion;
        var query = new NoteQuery(Search, _selectedTag, Sort, ShowArchived);
        try
        {
            var (notes, tags) = await Task.Run(() => (_repository.Find(query), _repository.GetTags(query.Archived)));
            if (version != _reloadVersion)
            {
                return; // a newer search is on its way
            }

            var now = _time.GetLocalNow();
            Items.Clear();
            foreach (var note in notes)
            {
                Items.Add(new NoteItem(note, NoteText.Ago(note.UpdatedUtc, now)));
            }

            RebuildTags(tags);
            OnPropertyChanged(nameof(HasNotes));
            OnPropertyChanged(nameof(EmptyText));
            AnnounceRecovery();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not load notes");
            Say("Couldn't open your notes.");
        }
    }

    private void RebuildTags(IReadOnlyList<TagCount> tags)
    {
        // The selected tag disappeared (last note with it deleted): show everything again.
        if (_selectedTag is not null && tags.All(t => t.Tag != _selectedTag))
        {
            _selectedTag = null;
        }

        Tags.Clear();
        Tags.Add(new TagFilter(null, "All", SelectTag) { IsSelected = _selectedTag is null });
        foreach (var tag in tags)
        {
            Tags.Add(new TagFilter(tag.Tag, $"#{tag.Tag}  {tag.Count}", SelectTag) { IsSelected = tag.Tag == _selectedTag });
        }

        OnPropertyChanged(nameof(HasTags));
    }

    private void SelectTag(TagFilter filter)
    {
        _selectedTag = filter.Tag;
        foreach (var tag in Tags)
        {
            tag.IsSelected = tag == filter;
        }

        _ = ReloadAsync();
    }

    /// <summary>Runs a database action off the UI thread. Returns false (and tells the user) if it failed.</summary>
    private async Task<bool> RunAsync(Action action, string? success, bool reload)
    {
        try
        {
            await Task.Run(action);
            if (success is not null)
            {
                Say(success);
            }

            if (reload)
            {
                await ReloadAsync();
            }

            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Notes action failed");
            Say("Something went wrong with your notes. Please try again.");
            return false;
        }
    }

    /// <summary>Shows "<paramref name="text"/> · Undo" for a few seconds; Undo saves <paramref name="restore"/> back.</summary>
    private void OfferUndo(string text, Note restore)
    {
        _undoNote = restore;
        UndoText = text;
        CanUndo = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void CopyToClipboard(Note note)
    {
        // The title comes along as a heading; other apps get clean text, other notes get the formatting.
        var title = note.Title.Trim();
        var content = note.Content.Trim();
        var markdown = title.Length == 0 ? content
            : NoteMarkdown.Write([new NoteBlock(NoteBlockKind.Heading2, [new NoteSpan(title, false, false)])])
              + (content.Length > 0 ? "\n\n" + content : string.Empty);
        if (markdown.Length == 0)
        {
            Say("Nothing to copy yet.");
            return;
        }

        // The clipboard can be briefly locked by another app: try a few times.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(Controls.NoteClipboard.Create(markdown), copy: true);
                Say("Copied to clipboard");
                return;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }

        Say("Couldn't reach the clipboard. Try again.");
    }

    private void AnnounceRecovery()
    {
        if (!_recoveryAnnounced && _repository.StorageWasReset)
        {
            _recoveryAnnounced = true;
            Say("Your notes file was damaged, so I started a fresh one (the old file is kept).");
        }
    }

    private void Say(string text) => StatusChanged?.Invoke(text);

    private static bool IsStorageError(Exception ex) =>
        ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;
}
