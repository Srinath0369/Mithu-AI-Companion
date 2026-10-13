using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mithu.App.ViewModels;

namespace Mithu.App.Views;

/// <summary>
/// To-do page. The view model owns the tasks; this adds on Enter, renames in place (Enter keeps, Esc cancels,
/// clicking away keeps), and opens the menus (move / delete a task; new, rename, delete a list).
/// </summary>
public partial class TodoPage : UserControl
{
    public TodoPage() => InitializeComponent();

    private TodosViewModel? Todos => (DataContext as MainViewModel)?.Todos;

    /// <summary>Puts the cursor in "Add a task…".</summary>
    public void FocusQuickAdd() =>
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(QuickBox);
            QuickBox.CaretIndex = QuickBox.Text.Length;
        }, DispatcherPriority.Input);

    private void QuickBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Todos?.AddCommand.CanExecute(null) == true)
        {
            Todos.AddCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ===== Rename in place =========================================================

    private void Title_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoRow row } && Todos is { } todos)
        {
            todos.BeginEditCommand.Execute(row);
            e.Handled = true;
        }
    }

    private void EditBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Keyboard.Focus(box);
                box.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void EditBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoRow row } || Todos is not { } todos)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            todos.CommitEdit(row);
            e.Handled = true;
            FocusQuickAdd();
        }
        else if (e.Key == Key.Escape)
        {
            TodosViewModel.CancelEdit(row);
            e.Handled = true; // Esc ends the rename, not the page
        }
    }

    private void EditBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TodoRow row })
        {
            Todos?.CommitEdit(row);
        }
    }

    // ===== Menus =====================================================================

    private void TaskMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TodoRow row } button || Todos is not { } todos)
        {
            return;
        }

        var menu = NewMenu();
        menu.Items.Add(Item("Rename", () => todos.BeginEditCommand.Execute(row)));
        menu.Items.Add(Item(row.Important ? "Not important" : "Mark important", () => todos.StarCommand.Execute(row)));
        var move = new MenuItem { Header = "Move to", Style = (Style)FindResource("IslandMenuItem") };
        foreach (var list in todos.Lists.Where(l => l.Id != row.Item.ListId))
        {
            move.Items.Add(Item(list.Name, () => todos.MoveTo(row, list.Id)));
        }

        move.IsEnabled = move.Items.Count > 0;
        menu.Items.Add(move);
        menu.Items.Add(new Separator { Style = (Style)FindResource("IslandMenuSeparator") });
        menu.Items.Add(Item("Delete", () => todos.DeleteCommand.Execute(row)));
        Show(menu, button);
        e.Handled = true;
    }

    private void ListMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button || Todos is not { } todos)
        {
            return;
        }

        var menu = NewMenu();
        menu.Items.Add(Item("Rename list", () => Dispatcher.BeginInvoke(() => AskName(button, todos.SelectedListName, "List name", todos.RenameList), DispatcherPriority.Input)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("IslandMenuSeparator") });
        menu.Items.Add(Item("Delete list (tasks move to My tasks)", () => todos.DeleteListCommand.Execute(null)));
        Show(menu, button);
        e.Handled = true;
    }

    private void NewList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement button && Todos is { } todos)
        {
            AskName(button, string.Empty, "New list name", todos.NewList);
        }
    }

    /// <summary>A small box under the button: type a name, Enter to keep it (Esc or a click away cancels).</summary>
    private void AskName(FrameworkElement anchor, string start, string label, Action<string> done)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("IslandInput"),
            FontSize = 13,
            Width = 200,
            MaxLength = Core.Todos.TodoList.MaxNameLength,
            Text = start,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)FindResource("MenuSurfaceBrush"),
                BorderBrush = (Brush)FindResource("IslandRimBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 8, 14, 8),
                Child = box,
            },
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                popup.IsOpen = false;
                done(box.Text);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };

        if (Window.GetWindow(this) is FloatingWindow window)
        {
            window.TrackPopup(popup);
        }

        popup.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(box);
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private ContextMenu NewMenu() => new() { Style = (Style)FindResource("IslandMenu") };

    private MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource("IslandMenuItem") };
        item.Click += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };
        return item;
    }

    private void Show(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        if (Window.GetWindow(this) is FloatingWindow window)
        {
            window.TrackMenu(menu);
        }

        menu.IsOpen = true;
    }
}
