using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Mithu.App.ViewModels;
using Mithu.Core.Notes;

namespace Mithu.App.Views;

/// <summary>
/// Notes page. The view model owns the data; this code-behind wires the toolbar to the live editor,
/// handles the tag box keys, puts the cursor in the right place, and opens the small menus.
/// </summary>
public partial class NotesPage : UserControl
{
    private NotesViewModel? _notes;

    public NotesPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach((DataContext as MainViewModel)?.Notes);
    }

    /// <summary>Puts the cursor in the search box (used when the page opens with the keyboard).</summary>
    public void FocusSearch() => FocusSoon(SearchBox);

    private void Attach(NotesViewModel? notes)
    {
        if (_notes is not null)
        {
            _notes.NoteOpened -= OnNoteOpened;
        }

        _notes = notes;
        if (_notes is not null)
        {
            _notes.NoteOpened += OnNoteOpened;
        }
    }

    /// <summary>A note was opened: fresh undo history, and the cursor in the title (new note) or at the end of the text.</summary>
    private void OnNoteOpened(bool isNew)
    {
        Editor.ResetHistory();
        Dispatcher.BeginInvoke(() =>
        {
            if (isNew)
            {
                Keyboard.Focus(TitleBox);
            }
            else
            {
                Editor.FocusAtEnd();
            }
        }, DispatcherPriority.Input);
    }

    // ===== Formatting (applies right away) =====================================

    private void Bold_Click(object sender, RoutedEventArgs e) => Editor.ToggleBold();

    private void Italic_Click(object sender, RoutedEventArgs e) => Editor.ToggleItalic();

    private void Heading_Click(object sender, RoutedEventArgs e) => Editor.ToggleHeading();

    private void Bullet_Click(object sender, RoutedEventArgs e) => Editor.ToggleBullets();

    private void Check_Click(object sender, RoutedEventArgs e) => Editor.ToggleChecklist();

    // ===== Tags ===================================================================

    /// <summary>Enter makes a chip; Backspace in an empty box removes the last chip.</summary>
    private void TagsBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_notes is not { } notes)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            notes.CommitTags(final: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Back && TagsBox.Text.Length == 0)
        {
            notes.RemoveLastTag();
            e.Handled = true;
        }
    }

    private void TagsBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => _notes?.CommitTags(final: true);

    // ===== Menus ==============================================================

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (_notes is not { } notes)
        {
            return;
        }

        var menu = NewMenu();
        foreach (var (sort, label) in new[] { (NoteSort.LastModified, "Last edited"), (NoteSort.Newest, "Newest first"), (NoteSort.Oldest, "Oldest first") })
        {
            menu.Items.Add(Item(label, () => notes.SetSortCommand.Execute(sort), isChecked: notes.Sort == sort));
        }

        Show(menu, SortButton);
    }

    private void NoteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NoteItem item } button)
        {
            Show(NoteMenu(item), button);
            e.Handled = true;
        }
    }

    private void NoteCard_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NoteItem item } card)
        {
            Show(NoteMenu(item), card, PlacementMode.MousePoint);
            e.Handled = true;
        }
    }

    private void EditorMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_notes is not { } notes)
        {
            return;
        }

        var menu = NewMenu();
        menu.Items.Add(Item(notes.PinLabel, () => notes.TogglePinCommand.Execute(null)));
        menu.Items.Add(Item("Copy", () => notes.CopyCommand.Execute(null)));
        menu.Items.Add(Item("Duplicate", () => notes.DuplicateCommand.Execute(null)));
        menu.Items.Add(Item(notes.ArchiveLabel, () => notes.ArchiveCommand.Execute(null)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("IslandMenuSeparator") });
        menu.Items.Add(Item("Delete", () => notes.DeleteCommand.Execute(null)));
        Show(menu, EditorMenuButton);
    }

    private ContextMenu NoteMenu(NoteItem item)
    {
        var notes = _notes!;
        var menu = NewMenu();
        var archived = item.Note.IsArchived;
        if (!archived)
        {
            menu.Items.Add(Item(item.IsPinned ? "Unpin" : "Pin to top", () => notes.ItemPinCommand.Execute(item)));
        }

        menu.Items.Add(Item("Copy", () => notes.ItemCopyCommand.Execute(item)));
        menu.Items.Add(Item("Duplicate", () => notes.ItemDuplicateCommand.Execute(item)));
        menu.Items.Add(Item(archived ? "Unarchive" : "Archive", () => notes.ItemArchiveCommand.Execute(item)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("IslandMenuSeparator") });
        menu.Items.Add(Item("Delete", () => notes.ItemDeleteCommand.Execute(item)));
        return menu;
    }

    private ContextMenu NewMenu() => new() { Style = (Style)FindResource("IslandMenu") };

    private MenuItem Item(string header, Action onClick, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource("IslandMenuItem"), IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// Opens a menu. While it is open the window counts as "menu open", so Mithu doesn't close when the
    /// menu (a separate popup) takes the click.
    /// </summary>
    private void Show(ContextMenu menu, UIElement target, PlacementMode placement = PlacementMode.Bottom)
    {
        menu.PlacementTarget = target;
        menu.Placement = placement;
        if (Window.GetWindow(this) is FloatingWindow window)
        {
            window.TrackMenu(menu);
        }

        menu.IsOpen = true;
    }

    private void FocusSoon(TextBox box) =>
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        }, DispatcherPriority.Input);
}
