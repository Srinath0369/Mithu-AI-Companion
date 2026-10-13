using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mithu.App.ViewModels;

namespace Mithu.App.Views;

/// <summary>
/// Panel shown in Expanded mode, with three pages: Home, Focus and Notes (<see cref="MainViewModel.Page"/>).
/// Inherits the MainViewModel DataContext. Switching pages slides them sideways (instant with reduced motion);
/// pages that are away are collapsed, so they never take clicks or keyboard focus.
/// </summary>
public partial class ExpandedPanel : UserControl
{
    private static readonly Duration PageSlide = new(TimeSpan.FromMilliseconds(220));
    private static readonly IEasingFunction PageEase = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private const double SlideDistance = 28;

    private readonly TranslateTransform _notesShift = new();
    private readonly TranslateTransform _remindersShift = new();
    private readonly TranslateTransform _aiSettingsShift = new();
    private readonly TranslateTransform _askShift = new();
    private readonly TranslateTransform _desktopShift = new();
    private readonly TranslateTransform _todoShift = new();
    private readonly TranslateTransform _setupShift = new();
    private MainViewModel? _main;
    private PanelPage _shown = PanelPage.Home;

    public ExpandedPanel()
    {
        InitializeComponent();
        NotesView.RenderTransform = _notesShift;
        RemindersView.RenderTransform = _remindersShift;
        AiSettingsView.RenderTransform = _aiSettingsShift;
        AskView.RenderTransform = _askShift;
        DesktopControlView.RenderTransform = _desktopShift;
        TodoView.RenderTransform = _todoShift;
        FirstRunView.RenderTransform = _setupShift;

        // Cyan rim on the input while typing (focus is visible without a heavy focus rectangle).
        InputBox.GotKeyboardFocus += (_, _) => InputPill.BorderBrush = (Brush)FindResource("CyanBrush");
        InputBox.LostKeyboardFocus += (_, _) => InputPill.BorderBrush = (Brush)FindResource("CardRimBrush");

        // Opening the focus set-up puts the cursor in "What are you focusing on?" so you can just type + Enter.
        IntentionBox.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Dispatcher.BeginInvoke(() => Keyboard.Focus(IntentionBox), System.Windows.Threading.DispatcherPriority.Input);
            }
        };

        // Choosing "Custom" puts the cursor in the minutes box, with the number selected so typing replaces it.
        // (Only when chosen on the open page; opening the page focuses the intention instead.)
        CustomMinutesBox.IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true && SetupView.IsKeyboardFocusWithin)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    Keyboard.Focus(CustomMinutesBox);
                    CustomMinutesBox.SelectAll();
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
        };

        // Minutes only: ignore anything that isn't a digit.
        CustomMinutesBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
        DataObject.AddPastingHandler(CustomMinutesBox, (_, e) =>
        {
            if (e.DataObject.GetData(typeof(string)) is not string text || !text.Trim().All(char.IsAsciiDigit))
            {
                e.CancelCommand();
            }
        });

        DataContextChanged += (_, _) => Attach(DataContext as MainViewModel);
    }

    /// <summary>When true, pages switch instantly (no slide).</summary>
    public bool ReducedMotion { get; set; }

    /// <summary>The global hotkey opened the panel: put the cursor where typing makes sense on this page.</summary>
    public void FocusInput()
    {
        if (_shown == PanelPage.Notes && _main?.Notes.IsEditing == false)
        {
            NotesView.FocusSearch();
            return;
        }

        if (_shown == PanelPage.Reminders && _main?.Reminders.IsEditing == false)
        {
            RemindersView.FocusQuickAdd();
            return;
        }

        if (_shown == PanelPage.Ask)
        {
            AskView.FocusMessage();
            return;
        }

        if (_shown == PanelPage.Todos)
        {
            TodoView.FocusQuickAdd();
            return;
        }

        if (_shown == PanelPage.Setup)
        {
            FirstRunView.FocusStep();
            return;
        }

        if (_shown != PanelPage.Home)
        {
            return;
        }

        InputBox.Focus();
        Keyboard.Focus(InputBox);
        InputBox.CaretIndex = InputBox.Text.Length;
    }

    private void Attach(MainViewModel? main)
    {
        if (_main is not null)
        {
            _main.PropertyChanged -= OnMainPropertyChanged;
        }

        _main = main;
        if (_main is not null)
        {
            _main.PropertyChanged += OnMainPropertyChanged;
            ShowPage(_main.Page, animate: false);
        }
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Page) && _main is not null)
        {
            ShowPage(_main.Page, animate: IsVisible && !ReducedMotion);
        }
    }

    private (FrameworkElement Page, TranslateTransform Shift) Parts(PanelPage page) => page switch
    {
        PanelPage.Focus => (FocusPage, FocusShift),
        PanelPage.Notes => (NotesView, _notesShift),
        PanelPage.Reminders => (RemindersView, _remindersShift),
        PanelPage.AiSettings => (AiSettingsView, _aiSettingsShift),
        PanelPage.Ask => (AskView, _askShift),
        PanelPage.DesktopControl => (DesktopControlView, _desktopShift),
        PanelPage.Todos => (TodoView, _todoShift),
        PanelPage.Setup => (FirstRunView, _setupShift),
        _ => (HomePage, HomeShift),
    };

    /// <summary>Shows a page. Going to a page slides in from the right; going back Home slides the other way.</summary>
    private void ShowPage(PanelPage page, bool animate)
    {
        var (incoming, incomingShift) = Parts(page);
        if (page == _shown && incoming.Visibility == Visibility.Visible)
        {
            return;
        }

        var (outgoing, outgoingShift) = Parts(_shown);
        _shown = page;
        incoming.Visibility = Visibility.Visible;

        // Anything else (e.g. a page left mid-slide) is put away at once.
        foreach (var other in Enum.GetValues<PanelPage>())
        {
            var (element, shift) = Parts(other);
            if (element != incoming && element != outgoing)
            {
                Settle(element, shift, 0);
                element.Visibility = Visibility.Collapsed;
            }
        }

        if (!animate || outgoing == incoming)
        {
            Settle(incoming, incomingShift, 1);
            if (outgoing != incoming)
            {
                Settle(outgoing, outgoingShift, 0);
                outgoing.Visibility = Visibility.Collapsed;
            }

            return;
        }

        var direction = page == PanelPage.Home ? -1 : 1;
        Slide(incoming, incomingShift, fromX: SlideDistance * direction, toX: 0, fromOpacity: 0, toOpacity: 1, completed: null);
        Slide(outgoing, outgoingShift, fromX: 0, toX: -SlideDistance * direction, fromOpacity: outgoing.Opacity, toOpacity: 0, completed: () =>
        {
            // Only hide it if the user didn't come back to it mid-slide.
            if (Parts(_shown).Page != outgoing)
            {
                outgoing.Visibility = Visibility.Collapsed;
            }
        });
    }

    private static void Slide(UIElement page, TranslateTransform shift, double fromX, double toX, double fromOpacity, double toOpacity, Action? completed)
    {
        var fade = new DoubleAnimation(fromOpacity, toOpacity, PageSlide) { EasingFunction = PageEase };
        if (completed is not null)
        {
            // Attach before BeginAnimation: WPF freezes a copy of the timeline when it starts.
            fade.Completed += (_, _) => completed();
        }

        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, toX, PageSlide) { EasingFunction = PageEase });
        page.BeginAnimation(OpacityProperty, fade);
    }

    private static void Settle(UIElement page, TranslateTransform shift, double opacity)
    {
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = 0;
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = opacity;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
