using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mithu.App.ViewModels;

namespace Mithu.App.Views;

/// <summary>
/// Ask page. The view model owns the conversation; this code-behind sends on Enter, keeps the conversation scrolled
/// to the newest message, runs the "thinking" dots, and opens the chat menus (rename, delete) and the copy menu.
/// </summary>
public partial class AskPage : UserControl
{
    private AskViewModel? _ask;
    private Storyboard? _dots;

    public AskPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach((DataContext as MainViewModel)?.Ask);

        // A bigger panel (⋯ → Panel size): bubbles use the room too (never narrower than at 1×).
        SizeChanged += (_, e) =>
        {
            var width = e.NewSize.Width;
            Resources["BubbleMax"] = Math.Max(350, width * 0.89);
            Resources["UserBubbleMax"] = Math.Max(320, width * 0.82);
            Resources["BubbleTextMax"] = Math.Max(310, (width * 0.89) - 40);
            Resources["CardMax"] = Math.Max(360, width * 0.92);
        };
        Thinking.IsVisibleChanged += (_, e) =>
        {
            _dots ??= ((Storyboard)FindResource("Dots")).Clone();
            if (e.NewValue is true)
            {
                _dots.Begin(this, isControllable: true);
            }
            else
            {
                _dots.Stop(this);
            }
        };
    }

    /// <summary>Puts the cursor in the message box (page opened with the keyboard, or New chat).</summary>
    public void FocusMessage() =>
        Dispatcher.BeginInvoke(() =>
        {
            Keyboard.Focus(MessageBox);
            MessageBox.CaretIndex = MessageBox.Text.Length;
        }, DispatcherPriority.Input);

    private void Attach(AskViewModel? ask)
    {
        if (_ask is not null)
        {
            _ask.ScrolledToEnd -= ScrollToEnd;
            _ask.ReplyGrew -= FollowReply;
            _ask.InputFocusRequested -= FocusMessage;
        }

        _ask = ask;
        if (_ask is not null)
        {
            _ask.ScrolledToEnd += ScrollToEnd;
            _ask.ReplyGrew += FollowReply;
            _ask.InputFocusRequested += FocusMessage;
        }
    }

    private void ScrollToEnd() =>
        Dispatcher.BeginInvoke(ConversationScroller.ScrollToEnd, DispatcherPriority.Loaded);

    /// <summary>
    /// The reply is growing: keep it in view, unless the reader has scrolled up to read something earlier
    /// (then leave them there, like ChatGPT and Claude do).
    /// </summary>
    private void FollowReply()
    {
        var atBottom = ConversationScroller.VerticalOffset >= ConversationScroller.ScrollableHeight - 48;
        if (atBottom)
        {
            ScrollToEnd();
        }
    }

    /// <summary>Enter sends; Shift+Enter adds a new line.</summary>
    private void MessageBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            if (_ask?.SendCommand.CanExecute(null) == true)
            {
                _ = _ask.SendCommand.ExecuteAsync(null);
            }
        }
    }

    // ===== Menus =================================================================

    /// <summary>Right-click on Mithu's reply: Copy.</summary>
    private void Bubble_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: ChatBubble bubble } element)
        {
            var menu = NewMenu();
            menu.Items.Add(Item("Copy", () => Copy(bubble.Text)));
            Show(menu, element, PlacementMode.MousePoint);
        }
    }

    private void ChatMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatListItem item } button)
        {
            Show(ChatMenu(item, button), button);
            e.Handled = true;
        }
    }

    private void ChatCard_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatListItem item } card)
        {
            Show(ChatMenu(item, card), card, PlacementMode.MousePoint);
            e.Handled = true;
        }
    }

    private ContextMenu ChatMenu(ChatListItem item, FrameworkElement anchor)
    {
        var ask = _ask!;
        var menu = NewMenu();
        menu.Items.Add(Item("Rename", () => Dispatcher.BeginInvoke(() => ShowRename(item, anchor), DispatcherPriority.Input)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("IslandMenuSeparator") });
        menu.Items.Add(Item("Delete", () => ask.DeleteChatCommand.Execute(item)));
        return menu;
    }

    /// <summary>A small box under the chat: type a new title, Enter to keep it (Esc or a click away cancels).</summary>
    private void ShowRename(ChatListItem item, FrameworkElement anchor)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("IslandInput"),
            FontSize = 13,
            Width = 220,
            MaxLength = Core.Ai.ChatRules.MaxTitleLength,
            Text = item.Title,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "New chat title");
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
                _ask?.RenameChat(item, box.Text);
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

    /// <summary>The copy button beside a reply: copies it and briefly shows a tick.</summary>
    private void CopyReply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ChatBubble bubble } button && Copy(bubble.Text))
        {
            button.Content = "";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                button.Content = "";
            };
            timer.Start();
        }
    }

    private static bool Copy(string text) => Services.ClipboardText.TrySet(text);

    /// <summary>A screenshot in the chat: open it full size in the user's image viewer.</summary>
    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatBubble { ImagePath: { } path } })
        {
            try
            {
                Vision.ScreenCapture.Open(path);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.FileNotFoundException)
            {
                // No image viewer, or the file is gone: nothing to open.
            }
        }
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
}
