using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Mithu.Core.Interfaces;
using Mithu.Core.Todos;

namespace Mithu.App.ViewModels;

/// <summary>A task on the page. Edited in place: click the words to rename.</summary>
public sealed partial class TodoRow(TodoItem item, string listName) : ObservableObject
{
    public TodoItem Item { get; private set; } = item;

    public string Title => Item.Title;

    public bool Done => Item.Done;

    public bool Important => Item.Important;

    /// <summary>The task's list, shown in the All view.</summary>
    public string ListName { get; private set; } = listName;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = string.Empty;

    public void Update(TodoItem item, string listName)
    {
        Item = item;
        ListName = listName;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Done));
        OnPropertyChanged(nameof(Important));
        OnPropertyChanged(nameof(ListName));
    }
}

/// <summary>
/// To-do: simple tasks without dates (anything with a time is a reminder). "My tasks" plus the user's own lists;
/// the All view shows every list. Open tasks (starred first, newest on top), a folded Done section, quick add,
/// tick, star, rename in place, move to another list, delete and Clear done with Undo. Never logs task text.
/// </summary>
public sealed partial class TodosViewModel : ObservableObject
{
    private static readonly TimeSpan UndoDuration = TimeSpan.FromSeconds(6);

    private readonly ITodoRepository _repository;
    private readonly TimeProvider _time;
    private readonly ILogger<TodosViewModel> _logger;
    private readonly DispatcherTimer _undoTimer;
    private IReadOnlyList<TodoList> _lists = [];
    private IReadOnlyList<TodoItem> _items = [];
    private List<TodoItem> _undoItems = [];

    public TodosViewModel(ITodoRepository repository, TimeProvider time, ILogger<TodosViewModel> logger)
    {
        _repository = repository;
        _time = time;
        _logger = logger;
        _undoTimer = new DispatcherTimer { Interval = UndoDuration };
        _undoTimer.Tick += (_, _) =>
        {
            _undoTimer.Stop();
            _undoItems = [];
            CanUndo = false;
        };
        Refresh();
    }

    /// <summary>A short line for Mithu's status text.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>All, then each list (with how many tasks are open).</summary>
    public ObservableCollection<ReminderChip> ListChips { get; } = [];

    public ObservableCollection<TodoRow> Open { get; } = [];

    public ObservableCollection<TodoRow> Finished { get; } = [];

    public IReadOnlyList<TodoList> Lists => _lists;

    /// <summary>The list shown (null = All lists).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllView), nameof(SelectedListName), nameof(CanEditList), nameof(AddHint))]
    private string? _selectedListId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newTitle = string.Empty;

    /// <summary>The Done section is open.</summary>
    [ObservableProperty]
    private bool _showDone;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private string _undoText = string.Empty;

    public bool IsAllView => SelectedListId is null;

    public string SelectedListName => _lists.FirstOrDefault(l => l.Id == SelectedListId)?.Name ?? "All";

    /// <summary>The shown list can be renamed or deleted (not All, not "My tasks").</summary>
    public bool CanEditList => SelectedListId is not null && SelectedListId != TodoList.DefaultId;

    public string AddHint => IsAllView ? "Add a task…" : $"Add to {SelectedListName}…";

    /// <summary>Open tasks in every list (Home's tile).</summary>
    public int OpenCount => _items.Count(i => !i.Done);

    public bool HasOpenAnywhere => OpenCount > 0;

    /// <summary>"3 to do" / "All done" for Home.</summary>
    public string HomeDetail => OpenCount == 0 ? "All done" : $"{OpenCount} to do";

    public bool HasOpen => Open.Count > 0;

    public bool HasFinished => Finished.Count > 0;

    public string FinishedHeader => $"Done ({Finished.Count})";

    public string EmptyText => _items.Count == 0
        ? "Nothing to do yet. Add your first task above."
        : HasFinished ? "All done here. Nice." : "Nothing here yet.";

    // ===== Loading =================================================================

    /// <summary>Reloads lists and tasks (after a change here, or from the AI).</summary>
    public void Refresh()
    {
        try
        {
            _lists = _repository.Lists();
            _items = _repository.Items();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "Could not load to-dos");
            Say("Couldn't load your to-dos.");
            return;
        }

        if (SelectedListId is not null && _lists.All(l => l.Id != SelectedListId))
        {
            SelectedListId = null;
        }

        ListChips.Clear();
        ListChips.Add(Chip(null, "All", _items.Count(i => !i.Done)));
        foreach (var list in _lists)
        {
            ListChips.Add(Chip(list.Id, list.Name, _items.Count(i => !i.Done && i.ListId == list.Id)));
        }

        var names = _lists.ToDictionary(l => l.Id, l => l.Name);
        var shown = TodoRules.Sort(_items.Where(i => SelectedListId is null || i.ListId == SelectedListId));
        Open.Clear();
        Finished.Clear();
        foreach (var item in shown)
        {
            var row = new TodoRow(item, names.GetValueOrDefault(item.ListId, TodoList.DefaultName));
            (item.Done ? Finished : Open).Add(row);
        }

        OnPropertyChanged(nameof(OpenCount));
        OnPropertyChanged(nameof(HasOpenAnywhere));
        OnPropertyChanged(nameof(HomeDetail));
        OnPropertyChanged(nameof(HasOpen));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(FinishedHeader));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(SelectedListName));
        OnPropertyChanged(nameof(AddHint));
    }

    private ReminderChip Chip(string? listId, string name, int open)
    {
        var chip = new ReminderChip(listId ?? string.Empty, open > 0 ? $"{name}  {open}" : name, c =>
        {
            SelectedListId = c.Key.Length == 0 ? null : c.Key;
            Refresh();
        })
        {
            IsSelected = listId == SelectedListId,
        };
        return chip;
    }

    // ===== Tasks ====================================================================

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        var title = TodoItem.Clean(NewTitle);
        if (title.Length == 0)
        {
            return;
        }

        var listId = SelectedListId ?? TodoList.DefaultId;
        var item = TodoItem.Create(listId, title, TodoRules.TopOrder(_items.Where(i => i.ListId == listId)), _time.GetUtcNow());
        if (Run(() => _repository.Save(item)))
        {
            NewTitle = string.Empty;
            Refresh();
        }
    }

    private bool CanAdd() => NewTitle.Trim().Length > 0;

    [RelayCommand]
    private void Toggle(TodoRow row)
    {
        var updated = row.Item.WithDone(!row.Done, _time.GetUtcNow());
        if (Run(() => _repository.Save(updated)))
        {
            Refresh();
            Say(updated.Done ? OpenCount == 0 ? "All done! 🎉" : "Done ✓" : "Back on the list");
        }
    }

    [RelayCommand]
    private void Star(TodoRow row)
    {
        var updated = row.Item with { Important = !row.Important, UpdatedUtc = _time.GetUtcNow() };
        if (Run(() => _repository.Save(updated)))
        {
            Refresh();
        }
    }

    [RelayCommand]
    private void BeginEdit(TodoRow row)
    {
        foreach (var other in Open.Concat(Finished).Where(r => r.IsEditing && r != row))
        {
            CommitEdit(other);
        }

        row.EditText = row.Title;
        row.IsEditing = true;
    }

    /// <summary>Saves the new words (empty words keep the old ones).</summary>
    public void CommitEdit(TodoRow row)
    {
        if (!row.IsEditing)
        {
            return;
        }

        row.IsEditing = false;
        var title = TodoItem.Clean(row.EditText);
        if (title.Length == 0 || title == row.Title)
        {
            return;
        }

        if (Run(() => _repository.Save(row.Item with { Title = title, UpdatedUtc = _time.GetUtcNow() })))
        {
            Refresh();
        }
    }

    public static void CancelEdit(TodoRow row) => row.IsEditing = false;

    [RelayCommand]
    private void Delete(TodoRow row)
    {
        if (Run(() => _repository.Delete(row.Item.Id)))
        {
            OfferUndo([row.Item], "Task deleted");
            Refresh();
        }
    }

    /// <summary>Moves a task to another list (from its ⋯ menu).</summary>
    public void MoveTo(TodoRow row, string listId)
    {
        if (row.Item.ListId == listId)
        {
            return;
        }

        var order = TodoRules.TopOrder(_items.Where(i => i.ListId == listId));
        if (Run(() => _repository.Save(row.Item with { ListId = listId, Order = order, UpdatedUtc = _time.GetUtcNow() })))
        {
            Refresh();
            Say($"Moved to {_lists.FirstOrDefault(l => l.Id == listId)?.Name}");
        }
    }

    [RelayCommand]
    private void ClearDone()
    {
        IReadOnlyList<TodoItem> cleared = [];
        if (Run(() => cleared = _repository.ClearDone(SelectedListId)) && cleared.Count > 0)
        {
            OfferUndo(cleared.ToList(), cleared.Count == 1 ? "1 done task cleared" : $"{cleared.Count} done tasks cleared");
            Refresh();
        }
    }

    [RelayCommand]
    private void ToggleDone() => ShowDone = !ShowDone;

    [RelayCommand]
    private void Undo()
    {
        _undoTimer.Stop();
        var items = _undoItems;
        _undoItems = [];
        CanUndo = false;
        if (items.Count > 0 && Run(() =>
            {
                foreach (var item in items)
                {
                    _repository.Save(item);
                }
            }))
        {
            Refresh();
            Say("Restored");
        }
    }

    private void OfferUndo(List<TodoItem> items, string text)
    {
        _undoItems = items;
        UndoText = text;
        CanUndo = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    // ===== Lists ====================================================================

    /// <summary>Adds a list and shows it.</summary>
    public void NewList(string name)
    {
        var clean = TodoList.CleanName(name);
        if (clean.Length == 0)
        {
            return;
        }

        if (_lists.FirstOrDefault(l => l.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            SelectedListId = existing.Id;
            Refresh();
            return;
        }

        var list = TodoList.Create(clean, _lists.Count == 0 ? 1 : _lists.Max(l => l.Order) + 1, _time.GetUtcNow());
        if (Run(() => _repository.SaveList(list)))
        {
            SelectedListId = list.Id;
            Refresh();
            Say($"List {clean} created");
        }
    }

    public void RenameList(string name)
    {
        var clean = TodoList.CleanName(name);
        if (!CanEditList || clean.Length == 0 || _lists.FirstOrDefault(l => l.Id == SelectedListId) is not { } list)
        {
            return;
        }

        if (Run(() => _repository.SaveList(list with { Name = clean })))
        {
            Refresh();
        }
    }

    /// <summary>Deletes the shown list; its tasks move to "My tasks".</summary>
    [RelayCommand]
    private void DeleteList()
    {
        if (!CanEditList || _lists.FirstOrDefault(l => l.Id == SelectedListId) is not { } list)
        {
            return;
        }

        var moved = _items.Count(i => i.ListId == list.Id);
        if (Run(() => _repository.DeleteList(list.Id)))
        {
            SelectedListId = null;
            Refresh();
            Say(moved > 0 ? $"List deleted · its {moved} task{(moved == 1 ? string.Empty : "s")} moved to {TodoList.DefaultName}" : "List deleted");
        }
    }

    // ===== For the AI ================================================================

    /// <summary>Adds a task to a list by name (a new list is made if there's none by that name).</summary>
    public (TodoItem Item, string ListName, bool NewList) AddFromAssistant(string title, string? listName, bool important)
    {
        var lists = _repository.Lists();
        var name = TodoList.CleanName(listName ?? string.Empty);
        var list = name.Length == 0
            ? lists.First(l => l.IsDefault)
            : lists.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
              ?? lists.FirstOrDefault(l => name.Contains(l.Name, StringComparison.OrdinalIgnoreCase) || l.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        var created = false;
        if (list is null)
        {
            list = TodoList.Create(name, lists.Max(l => l.Order) + 1, _time.GetUtcNow());
            _repository.SaveList(list);
            created = true;
        }

        var item = TodoItem.Create(list.Id, title, TodoRules.TopOrder(_repository.Items(list.Id)), _time.GetUtcNow(), important);
        _repository.Save(item);
        Refresh();
        return (item, list.Name, created);
    }

    /// <summary>Ticks the task meant by the words; null with candidates when it isn't clear.</summary>
    public (TodoItem? Done, IReadOnlyList<TodoItem> Candidates) CompleteFromAssistant(string words)
    {
        var items = _repository.Items();
        if (TodoRules.Find(words, items) is not { } item)
        {
            return (null, TodoRules.Candidates(words, items));
        }

        if (!item.Done)
        {
            item = item.WithDone(true, _time.GetUtcNow());
            _repository.Save(item);
            Refresh();
        }

        return (item, []);
    }

    /// <summary>The tasks of a list by name (or all), for the AI.</summary>
    public (IReadOnlyList<TodoItem> Items, IReadOnlyList<TodoList> Lists) ForAssistant(string? listName)
    {
        var lists = _repository.Lists();
        var list = string.IsNullOrWhiteSpace(listName)
            ? null
            : lists.FirstOrDefault(l => l.Name.Equals(listName.Trim(), StringComparison.OrdinalIgnoreCase));
        return (TodoRules.Sort(_repository.Items(list?.Id)), list is null ? lists : [list]);
    }

    // ===== Helpers =====================================================================

    private bool Run(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            _logger.LogError(ex, "To-do storage action failed");
            Say("Couldn't save that. Please try again.");
            return false;
        }
    }

    private void Say(string text) => StatusChanged?.Invoke(text);

    private static bool IsStorageError(Exception ex) =>
        ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException;
}
