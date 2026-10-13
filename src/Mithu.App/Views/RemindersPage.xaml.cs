using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Mithu.App.ViewModels;
using Mithu.Core.Reminders;

namespace Mithu.App.Views;

/// <summary>
/// Reminders page. The view model owns the data; this code-behind opens the small menus and the date picker,
/// and puts the cursor where typing makes sense.
/// </summary>
public partial class RemindersPage : UserControl
{
    private RemindersViewModel? _reminders;

    public RemindersPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach((DataContext as MainViewModel)?.Reminders);

        Calendar.DatePicked += date =>
        {
            _reminders?.PickDate(date);
            DatePopup.IsOpen = false;
            FocusSoon(TimeBox, selectAll: true);
        };

        // "Every N": digits only.
        EveryBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

        // Esc with the date picker open closes only the picker.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && DatePopup.IsOpen)
            {
                DatePopup.IsOpen = false;
                e.Handled = true;
            }
        };
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is FloatingWindow window)
            {
                window.TrackPopup(DatePopup);
            }
        };
    }

    /// <summary>Puts the cursor in the quick-add box (the page was opened with the keyboard).</summary>
    public void FocusQuickAdd() => FocusSoon(QuickBox, selectAll: false);

    private void Attach(RemindersViewModel? reminders)
    {
        if (_reminders is not null)
        {
            _reminders.EditorOpened -= OnEditorOpened;
        }

        _reminders = reminders;
        if (_reminders is not null)
        {
            _reminders.EditorOpened += OnEditorOpened;
        }
    }

    /// <summary>New reminder: cursor in the title. Existing one: in the title too, at the end.</summary>
    private void OnEditorOpened(bool isNew)
    {
        EditorScroller.ScrollToTop();
        FocusSoon(TitleBox, selectAll: false);
    }

    // ===== Date and time ======================================================

    private void DateButton_Click(object sender, RoutedEventArgs e) => OpenCalendar();

    /// <summary>"Custom" opens the date picker.</summary>
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReminderChip { Key: "custom" } })
        {
            OpenCalendar();
        }
    }

    private void OpenCalendar()
    {
        if (_reminders is null)
        {
            return;
        }

        Calendar.Earliest = DateTime.Today;
        Calendar.ShowDate(_reminders.EditDate);
        DatePopup.IsOpen = true;
    }

    private void UnitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reminders is not { } reminders)
        {
            return;
        }

        var menu = NewMenu();
        foreach (var unit in new[] { RepeatUnit.Minutes, RepeatUnit.Hours, RepeatUnit.Days, RepeatUnit.Weeks, RepeatUnit.Months })
        {
            menu.Items.Add(Item(Repeat.UnitName(unit, 2), () => reminders.SetUnitCommand.Execute(unit), reminders.CustomUnit == unit));
        }

        Show(menu, UnitButton);
    }

    // ===== Menus ==============================================================

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_reminders is not { } reminders)
        {
            return;
        }

        var menu = NewMenu();
        menu.Items.Add(Header("Day"));
        foreach (var date in new[] { ReminderDateFilter.Any, ReminderDateFilter.Today, ReminderDateFilter.Tomorrow, ReminderDateFilter.Yesterday, ReminderDateFilter.ThisWeek })
        {
            menu.Items.Add(Item(RemindersViewModel.DateName(date), () => reminders.SetDateFilter(date), reminders.DateFilter == date));
        }

        menu.Items.Add(Separator());
        menu.Items.Add(Header("Priority"));
        menu.Items.Add(Item("Any priority", () => reminders.SetPriorityFilter(null), reminders.PriorityFilter is null));
        foreach (var priority in new[] { ReminderPriority.Normal, ReminderPriority.Important, ReminderPriority.Urgent })
        {
            menu.Items.Add(Item(ReminderText.PriorityName(priority), () => reminders.SetPriorityFilter(priority), reminders.PriorityFilter == priority));
        }

        if (reminders.HasFilter)
        {
            menu.Items.Add(Separator());
            menu.Items.Add(Item("Clear filters", () => reminders.ClearFiltersCommand.Execute(null)));
        }

        Show(menu, FilterButton);
    }

    private void ItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReminderItem item } button)
        {
            Show(ItemMenu(item), button);
            e.Handled = true;
        }
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ReminderItem item } card)
        {
            Show(ItemMenu(item), card, PlacementMode.MousePoint);
            e.Handled = true;
        }
    }

    private void EditorMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_reminders is not { } reminders)
        {
            return;
        }

        var menu = NewMenu();
        menu.Items.Add(Item("Duplicate", () => reminders.DuplicateEditingCommand.Execute(null)));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Delete", () => reminders.DeleteEditingCommand.Execute(null)));
        Show(menu, EditorMenuButton);
    }

    private ContextMenu ItemMenu(ReminderItem item)
    {
        var reminders = _reminders!;
        var menu = NewMenu();
        if (item.IsPending)
        {
            menu.Items.Add(Item("Mark as done", () => reminders.CompleteCommand.Execute(item)));
            menu.Items.Add(Item(item.IsRepeating ? "Skip this time" : "Skip", () => reminders.SkipCommand.Execute(item)));
            menu.Items.Add(Separator());

            // Snooze is for a reminder that has gone off ("remind me again in…").
            if (item.IsOverdue)
            {
                foreach (var minutes in RemindersViewModel.SnoozeMinutes)
                {
                    menu.Items.Add(Item($"Snooze {minutes} min", () => reminders.Snooze(item, minutes)));
                }

                menu.Items.Add(Separator());
            }
        }
        else
        {
            menu.Items.Add(Item("Mark as not done", () => reminders.ReopenCommand.Execute(item)));
        }

        menu.Items.Add(Item("Edit", () => reminders.EditCommand.Execute(item)));
        menu.Items.Add(Item("Duplicate", () => reminders.DuplicateCommand.Execute(item)));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Delete", () => reminders.DeleteItemCommand.Execute(item)));
        return menu;
    }

    private ContextMenu NewMenu() => new() { Style = (Style)FindResource("IslandMenu") };

    private MenuItem Item(string header, Action onClick, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, Style = (Style)FindResource("IslandMenuItem"), IsChecked = isChecked };
        item.Click += (_, e) =>
        {
            onClick();
            e.Handled = true;
        };
        return item;
    }

    private MenuItem Header(string text) =>
        new() { Header = text, Style = (Style)FindResource("IslandMenuItem"), IsEnabled = false, FontSize = 11 };

    private Separator Separator() => new() { Style = (Style)FindResource("IslandMenuSeparator") };

    /// <summary>Opens a menu; while it is open Mithu doesn't close when the menu takes the click.</summary>
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

    private void FocusSoon(TextBox box, bool selectAll) =>
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(box);
            if (selectAll)
            {
                box.SelectAll();
            }
            else
            {
                box.CaretIndex = box.Text.Length;
            }
        }, DispatcherPriority.Input);
}
