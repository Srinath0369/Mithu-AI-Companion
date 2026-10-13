using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Mithu.App.Animation;
using Mithu.App.Services;
using Mithu.App.ViewModels;
using Mithu.Core.Layout;
using Mithu.Core.Models;
using Mithu.Core.Motion;
using static Mithu.App.Interop.NativeMethods;

namespace Mithu.App.Views;

/// <summary>
/// The companion "island". One surface morphs between three sizes:
/// <list type="bullet">
/// <item><b>Hidden</b> — docked: slim notch with peeking eyes (click to open); floating: face bubble.</item>
/// <item><b>Compact</b> — face, greeting/status, ⋯ ▲ ▼.</item>
/// <item><b>Expanded</b> — compact header + panel.</item>
/// </list>
/// Motion: <see cref="IslandChoreography"/> (springs + staged content), played by <see cref="IslandAnimator"/>
/// on the render loop. The OS window stays a fixed size while open (no resize flicker during motion) and only
/// shrinks to the notch after the close has settled.
/// </summary>
public partial class FloatingWindow : Window
{
    /// <summary>Extra transparent space so the soft shadow and the docked "ears" are never cut off.</summary>
    private const double ShadowPad = 14;

    private const double HeaderHeight = 60;
    private const double BasePanelHeight = 296;
    private const double BaseExpandedWidth = 420;

    /// <summary>Panel size chosen in ⋯ → Panel size (1, 1.25 or 1.5): the open panel is that much wider and taller.</summary>
    private static double _panelScale = 1;

    private static double PanelHeight => Math.Round(BasePanelHeight * _panelScale);

    private static double ExpandedWidth => Math.Round(BaseExpandedWidth * _panelScale);

    /// <summary>The bar (Compact size) and the header in every size: grows with the panel so everything lines up.</summary>
    private static double CompactWidth => Math.Round(372 * _panelScale);
    private const double FloatingTopInset = 0;

    /// <summary>How long a peek stays out when ignored (paused while the pointer is over it).</summary>
    private static readonly TimeSpan PeekDuration = TimeSpan.FromSeconds(12);

    private static readonly TimeSpan ReminderPeekDuration = TimeSpan.FromSeconds(30);

    private readonly MainViewModel _viewModel;
    private readonly FaceAnimationCoordinator _face;
    private readonly WindowPlacementService _placement;
    private readonly DispatcherTimer _unfocusedTimer;
    private readonly DispatcherTimer _peekTimer;
    private readonly IslandAnimator _animator;

    private Point? _facePressPoint;

    // Docked slide state, shared by the notch, the face and the header (screen pixels: the window moves
    // under the pointer, so window coordinates drift during a drag).
    private Point? _slidePressScreen;
    private Core.Layout.PixelRect _slideStartRect;
    private bool _sliding;
    private UIElement? _slideCaptureTarget;
    private Action? _slideClick;

    private bool _reducedMotion;
    private bool _docked = true;
    private bool _closeOnClickAway = true;
    private bool _dragging;
    private bool _menuOpen;
    private bool _hovering;
    private bool _windowIsLarge;
    private WindowMode _appliedMode = (WindowMode)(-1);

    public FloatingWindow(MainViewModel viewModel, FaceAnimationCoordinator face, WindowPlacementService placement)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _face = face;
        _placement = placement;
        DataContext = viewModel;

        _animator = new IslandAnimator(ApplyFrame, new IslandChoreography(PoseFor(WindowMode.Compact), PoseFor(WindowMode.Compact)).Final);

        // Safety net only: closes an open companion that never received focus (e.g. shown at launch).
        _unfocusedTimer = new DispatcherTimer { Interval = ClickAwayPolicy.UnfocusedTimeout };
        _unfocusedTimer.Tick += (_, _) =>
        {
            _unfocusedTimer.Stop();
            if (ClickAwayPolicy.ShouldCloseUnfocused(CollapseState(), IsActive, IsMouseOver))
            {
                _viewModel.Mode = _viewModel.TuckedMode;
            }
        };

        // An ignored peek slides back by itself; hovering it keeps it out.
        _peekTimer = new DispatcherTimer { Interval = PeekDuration };
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();

            // Choosing a snooze length: wait for it.
            if (_menuOpen)
            {
                _peekTimer.Start();
                return;
            }

            // An urgent reminder stays out until it's answered.
            if (!IsMouseOver && !_viewModel.Reminders.AlertIsUrgent)
            {
                _viewModel.DismissPeek();
            }
        };

        // Floating bubble during a focus session: hovering it shows the time left ("18:42 · Write report").
        var focusTip = new ToolTip { DataContext = viewModel };
        focusTip.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("Focus.HeaderText"));
        FaceHost.ToolTip = focusTip;
        FaceHost.ToolTipOpening += (_, e) =>
        {
            if (_docked || !_viewModel.IsHidden || !_viewModel.Focus.IsActive)
            {
                e.Handled = true; // only on the bubble, only during a session
                return;
            }

            _viewModel.Focus.Refresh(); // the bubble ticks slowly: read the clock now
        };

        ApplyContentSize();
        _face.Attach(Face);
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        MouseEnter += OnWindowMouseEnter;
        MouseLeave += OnWindowMouseLeave;
        Activated += (_, _) => _unfocusedTimer.Stop();
        Deactivated += OnDeactivated;
    }

    /// <summary>When true, Tray "Exit" is in progress and closing is allowed.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Lets the shell build the ⋯ options menu (shared with the tray).</summary>
    public Func<ContextMenu>? OptionsMenuFactory { get; set; }

    /// <summary>Island body size (DIPs) for a size and dock style.</summary>
    public static Size BodySizeFor(WindowMode mode, bool docked) => mode switch
    {
        WindowMode.Hidden => docked ? new Size(132, 24) : new Size(76, HeaderHeight),
        WindowMode.Expanded => new Size(ExpandedWidth, HeaderHeight + PanelHeight),

        // A peek carries a message and its buttons ("⏰ Call Mom · Snooze · Done"): as wide as the panel.
        WindowMode.Peek => new Size(ExpandedWidth, HeaderHeight),
        _ => new Size(CompactWidth, HeaderHeight),
    };

    /// <summary>Corner radius of the island's bottom (docked) or all corners (floating).</summary>
    public static double RadiusFor(WindowMode mode, bool docked) => mode switch
    {
        WindowMode.Hidden => docked ? 12 : 30,
        WindowMode.Expanded => 26,
        _ => docked ? 24 : 30,
    };

    /// <summary>OS window size (DIPs) needed to show a body of the given size plus shadow/ears.</summary>
    public static Size WindowSizeFor(Size body) => new(body.Width + (2 * ShadowPad), body.Height + ShadowPad);

    /// <summary>The window size used whenever Mithu is open or moving: big enough for every pose.</summary>
    private static Size LargeWindowSize => WindowSizeFor(new Size(ExpandedWidth, HeaderHeight + PanelHeight));

    /// <summary>Hover swell of the notch (DIPs). The hidden window is sized to fit it, so hover never resizes the window.</summary>
    private static readonly Size HoverSwell = new(16, 3);

    /// <summary>
    /// Window size while tucked away: <b>as wide as the open window</b>, only as tall as the notch (plus room for
    /// the hover swell). Keeping the width means the window's left edge never moves when it grows or shrinks, so the
    /// frame or two Windows shows before Mithu repaints lines up exactly (a narrower window made the notch jump
    /// ~170 px sideways on open and flash on close). The extra area is fully transparent, and Windows passes
    /// clicks on fully transparent pixels of a layered window straight through.
    /// </summary>
    private Size SmallWindowSize
    {
        get
        {
            var body = BodySizeFor(WindowMode.Hidden, _docked);
            var height = WindowSizeFor(new Size(0, _docked ? body.Height + HoverSwell.Height : body.Height)).Height;
            return new Size(LargeWindowSize.Width, height);
        }
    }

    /// <summary>Border lines on (thin outline round the island, rim round the face) or off (one clean shape).</summary>
    public void SetBorders(bool show)
    {
        Island.Rim = show ? (Brush)FindResource("IslandRimBrush") : null;
        Face.ShowRim = show;
    }

    public void SetReducedMotion(bool reduced)
    {
        _reducedMotion = reduced;
        Face.ReducedMotion = reduced;
        if (PanelHost.Content is ExpandedPanel panel)
        {
            panel.ReducedMotion = reduced;
        }
    }

    /// <summary>Applies the placement options from settings.</summary>
    public void SetDockOptions(bool docked, bool closeOnClickAway)
    {
        var changed = docked != _docked;
        _docked = docked;
        _closeOnClickAway = closeOnClickAway;

        Island.Docked = docked;
        Island.EarRadius = docked ? 10 : 0;
        Island.TopInset = docked ? 0 : FloatingTopInset;

        if (changed && IsLoaded)
        {
            ApplyMode(animate: false);
        }

        UpdateFacePause();
        ArmUnfocusedTimer();
    }

    /// <summary>
    /// Makes the open panel bigger (1.25×, 1.5×) or back to normal: more room for chats, notes and lists, same text
    /// size. Never larger than fits the screen (it steps down to what fits). Returns the scale used.
    /// </summary>
    public double SetPanelScale(double scale)
    {
        var work = SystemParameters.WorkArea;
        var fits = Math.Min(
            (work.Height - ShadowPad - 24) / (HeaderHeight + BasePanelHeight),
            (work.Width - (2 * ShadowPad) - 24) / BaseExpandedWidth);
        var applied = Math.Clamp(Math.Min(scale, fits), 1, 1.5);
        if (Math.Abs(applied - _panelScale) < 0.001)
        {
            return applied;
        }

        _panelScale = applied;
        ApplyContentSize();
        if (IsLoaded && _appliedMode != (WindowMode)(-1))
        {
            ApplyMode(animate: false); // the new size at once
        }

        return applied;
    }

    /// <summary>Sizes the island's content for the panel size: the area, the header (centred), the face at its left, the panel.</summary>
    private void ApplyContentSize()
    {
        Island.ContentWidth = ExpandedWidth;
        Island.ContentHeight = HeaderHeight + PanelHeight;
        IslandContent.Width = ExpandedWidth;
        IslandContent.Height = HeaderHeight + PanelHeight;
        Header.Width = CompactWidth;
        FaceHost.Margin = new Thickness(((ExpandedWidth - CompactWidth) / 2) + 10, 7, 0, 0);
        PanelHost.Height = PanelHeight;
        if (PanelHost.Content is ExpandedPanel panel)
        {
            panel.Height = PanelHeight;
        }
    }

    /// <summary>Re-applies size/position (after display changes or a placement reset).</summary>
    public void RefreshPlacement() => SetWindowSize(large: _windowIsLarge || !_viewModel.IsHidden);

    /// <summary>Moves keyboard focus into the panel input (used by the global hotkey).</summary>
    public void FocusInput()
    {
        Activate();
        if (PanelHost.Content is ExpandedPanel panel)
        {
            panel.FocusInput();
        }
        else
        {
            FaceHost.Focus();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Tool window: hidden from Alt+Tab and the taskbar.
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)exStyle);

        ApplyMode(animate: false);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides to tray; only tray "Exit" really closes.
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }

        _animator.Stop();
        base.OnClosing(e);
    }

    // ===== Poses and frames ================================================

    private IslandPose PoseFor(WindowMode mode)
    {
        var body = BodySizeFor(mode, _docked);
        var notch = mode == WindowMode.Hidden && _docked;
        var bubble = mode == WindowMode.Hidden && !_docked;
        return new IslandPose(
            body.Width,
            body.Height,
            RadiusFor(mode, _docked),
            Header: notch || bubble ? 0 : 1,
            Panel: mode == WindowMode.Expanded ? 1 : 0,
            NotchEyes: notch ? 1 : 0,
            Bubble: bubble ? 1 : 0);
    }

    /// <summary>How far the face moves right to sit centred in the floating bubble (76 DIP wide).</summary>
    private static double BubbleFaceShift => (CompactWidth / 2) - 10 - 28;

    /// <summary>Applies one animation frame to every visual. Hot path: no layout, no allocations beyond the clip.</summary>
    private void ApplyFrame(IslandFrame f)
    {
        Island.SetBody(f.Width, f.Height, f.Radius);

        Header.Opacity = f.HeaderOpacity;
        HeaderShift.Y = f.HeaderOffsetY;

        PanelHost.Opacity = f.PanelOpacity;
        PanelShift.Y = f.PanelOffsetY;

        HiddenLayer.Opacity = f.NotchEyesOpacity;

        // Docked: the face rides with the header (tucked small into the notch, out when open).
        // Floating: the face is always shown at full size — it *is* the bubble — and glides between the bubble's
        // centre and its place in the bar as the island changes width (f.Bubble), never jumping or vanishing.
        var faceVisible = _docked ? f.HeaderOpacity : 1;
        FaceHost.Opacity = faceVisible;
        FaceShift.Y = _docked ? f.FaceOffsetY : 0;
        FaceScale.ScaleX = FaceScale.ScaleY = _docked ? f.FaceScale : 1;
        FaceShift.X = _docked ? 0 : f.Bubble * BubbleFaceShift;

        // Focus progress around the floating bubble: shown as Mithu tucks into the bubble, gone as it opens.
        BubbleProgress.Opacity = _docked ? 0 : f.Bubble;

        // Hidden layers must not catch clicks or keyboard focus.
        Header.IsHitTestVisible = f.HeaderOpacity > 0.5;
        FaceHost.IsHitTestVisible = faceVisible > 0.5;
        PanelHost.IsHitTestVisible = f.PanelOpacity > 0.5;
        PanelHost.Visibility = f.PanelOpacity <= 0.001 ? Visibility.Hidden : Visibility.Visible;
        HiddenLayer.IsHitTestVisible = f.NotchEyesOpacity > 0.5;
    }

    // ===== Changing size =====================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Mode))
        {
            ApplyMode(animate: true);
        }
    }

    private void ApplyMode(bool animate)
    {
        var mode = _viewModel.Mode;
        if (mode == _appliedMode && animate)
        {
            return;
        }

        var first = _appliedMode == (WindowMode)(-1);
        _appliedMode = mode;

        if (mode == WindowMode.Expanded)
        {
            PanelHost.Content ??= new ExpandedPanel { ReducedMotion = _reducedMotion, Height = PanelHeight }; // Lazy: created on first open only.
        }

        _unfocusedTimer.Stop();
        _hovering = false;
        ResetHoverFeedback();
        UpdateFacePause();

        _peekTimer.Stop();
        if (mode == WindowMode.Peek)
        {
            // A reminder stays out longer than a focus message (its Windows popup was skipped for it).
            _peekTimer.Interval = _viewModel.Reminders.HasAlert ? ReminderPeekDuration : PeekDuration;
            _peekTimer.Start();
        }

        var target = PoseFor(mode);
        var closingToNotch = mode == WindowMode.Hidden;

        if (!animate || _reducedMotion || first)
        {
            _animator.SnapTo(target);
            SetWindowSize(large: !closingToNotch);
            ArmUnfocusedTimer();
            return;
        }

        // Grow the OS window once, before any motion (never during it).
        SetWindowSize(large: true);

        _animator.AnimateTo(target, completed: () =>
        {
            // Shrink only after the notch has fully settled, so the user never sees the window resize.
            if (_viewModel.Mode == WindowMode.Hidden)
            {
                SetWindowSize(large: false);
            }

            ArmUnfocusedTimer();
        });
    }

    private void SetWindowSize(bool large)
    {
        _windowIsLarge = large;
        _placement.Apply(this, large ? LargeWindowSize : SmallWindowSize);
    }

    // ===== Click to open, click away to close ==============================

    private CollapseContext CollapseState() => new(
        Enabled: _closeOnClickAway,
        Visible: IsVisible,
        AlreadyHidden: _viewModel.IsHidden,
        Dragging: _dragging,
        MenuOpen: _menuOpen,
        PeekShowing: _viewModel.Mode == WindowMode.Peek);

    /// <summary>Windows reports focus moving to another app = the user clicked outside Mithu.</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (ClickAwayPolicy.ShouldCloseOnClickAway(CollapseState()))
        {
            _viewModel.Mode = _viewModel.TuckedMode;
        }
    }

    /// <summary>
    /// Mithu doesn't take focus when it appears (it never steals your typing), so a click inside activates it.
    /// After that, clicking anywhere else deactivates it and closes it.
    /// </summary>
    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive)
        {
            Activate();
        }
    }

    /// <summary>Open but never focused (e.g. at launch): close after a while once the pointer is away.</summary>
    private void ArmUnfocusedTimer()
    {
        _unfocusedTimer.Stop();
        if (ClickAwayPolicy.ShouldCloseUnfocused(CollapseState(), IsActive, IsMouseOver))
        {
            _unfocusedTimer.Start();
        }
    }

    private void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        _unfocusedTimer.Stop();
        _peekTimer.Stop();
        ShowHoverFeedback(true);
    }

    private void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        ShowHoverFeedback(false);
        ArmUnfocusedTimer();
        if (_viewModel.Mode == WindowMode.Peek)
        {
            _peekTimer.Start();
        }
    }

    /// <summary>Hover only signals "clickable": the notch swells gently and the eyes grow. It never opens anything.</summary>
    private void ShowHoverFeedback(bool on)
    {
        if (_reducedMotion || !_viewModel.IsHidden || !_docked || on == _hovering)
        {
            return;
        }

        // Don't interrupt the close animation that is still settling into the notch.
        if (_animator.IsRunning && !_hovering)
        {
            return;
        }

        _hovering = on;
        var rest = PoseFor(WindowMode.Hidden);
        var target = on ? rest with { Width = rest.Width + HoverSwell.Width, Height = rest.Height + HoverSwell.Height } : rest;

        // Never resize the window here: resizing under the cursor makes Windows re-send mouse leave/enter,
        // which restarts the hover and swallows clicks. The small window already has room for the swell.
        _animator.AnimateTo(target, spring: Spring.Snappy);

        var scale = on ? 1.15 : 1.0;
        NotchEyesScale.ScaleX = NotchEyesScale.ScaleY = scale;
    }

    private void ResetHoverFeedback() => NotchEyesScale.ScaleX = NotchEyesScale.ScaleY = 1;

    // ===== Docked slide: left/right only, for every size =====================
    //
    // While stuck to the top, Mithu may only slide horizontally along the edge, whatever is being dragged
    // (notch, face or header). A press that never moves sideways past the drag threshold is a click instead.

    /// <summary>Starts tracking a press that may become a horizontal slide. Returns false when floating.</summary>
    /// <param name="captureTarget">Element that captures the mouse for the duration of the press.</param>
    /// <param name="onClick">Runs if the press ends without sliding (null = no click action).</param>
    private bool BeginSlidePress(UIElement captureTarget, MouseButtonEventArgs e, Action? onClick)
    {
        if (!_docked || _placement.GetRect(this) is not { } rect)
        {
            return false;
        }

        _slidePressScreen = PointToScreen(e.GetPosition(this));
        _slideStartRect = rect;
        _sliding = false;
        _slideClick = onClick;
        _slideCaptureTarget = captureTarget;
        captureTarget.CaptureMouse();
        return true;
    }

    /// <summary>Moves the window horizontally once the pointer has clearly moved sideways.</summary>
    private void UpdateSlide(MouseEventArgs e)
    {
        if (_slidePressScreen is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var dx = PointToScreen(e.GetPosition(this)).X - start.X;

        // Only horizontal intent starts a slide; small jitters (or vertical moves) still count as a click.
        if (!_sliding)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * DpiScale())
            {
                return;
            }

            _sliding = true;
            _dragging = true;
            _unfocusedTimer.Stop();
            Cursor = Cursors.SizeWE;
        }

        _placement.SlideAlongTop(this, _slideStartRect, (int)Math.Round(dx));
    }

    /// <summary>Finishes a press: saves the slid position, or runs the click action. False if no press was active.</summary>
    /// <param name="release">The button-up event; its position is applied so the drop point is exact even if
    /// Windows coalesced the last mouse-move before the release.</param>
    private bool EndSlidePress(MouseButtonEventArgs release)
    {
        if (_slidePressScreen is not { } start)
        {
            return false;
        }

        if (_sliding)
        {
            var dx = PointToScreen(release.GetPosition(this)).X - start.X;
            _placement.SlideAlongTop(this, _slideStartRect, (int)Math.Round(dx));
        }

        var slid = _sliding;
        var click = _slideClick;
        ResetSlide();

        if (slid)
        {
            // Save where it was dropped (stays stuck to the top) and re-apply to land on whole pixels.
            if (_placement.GetRect(this) is { } rect)
            {
                _placement.SaveDocked(rect);
            }

            RefreshPlacement();
        }
        else
        {
            click?.Invoke();
        }

        return true;
    }

    /// <summary>Capture lost mid-press (e.g. Alt+Tab): keep the position reached so far, no click.</summary>
    private void CancelSlidePress()
    {
        if (_slidePressScreen is null)
        {
            return;
        }

        var slid = _sliding;
        ResetSlide();
        if (slid && _placement.GetRect(this) is { } rect)
        {
            _placement.SaveDocked(rect);
        }
    }

    private void ResetSlide()
    {
        var target = _slideCaptureTarget;
        _slidePressScreen = null;
        _sliding = false;
        _dragging = false;
        _slideClick = null;
        _slideCaptureTarget = null;
        ClearValue(CursorProperty);
        if (target is { IsMouseCaptured: true })
        {
            target.ReleaseMouseCapture();
        }
    }

    // ----- Notch: click opens, drag sideways slides along the top edge -----

    private void HiddenLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsHidden && BeginSlidePress(HiddenLayer, e, onClick: () =>
            {
                if (_viewModel.IsHidden && _docked)
                {
                    _viewModel.Reveal();
                }
            }))
        {
            e.Handled = true;
        }
    }

    private void HiddenLayer_MouseMove(object sender, MouseEventArgs e) => UpdateSlide(e);

    private void HiddenLayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (EndSlidePress(e))
        {
            e.Handled = true;
        }
    }

    private void HiddenLayer_LostMouseCapture(object sender, MouseEventArgs e) => CancelSlidePress();

    private double DpiScale() => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateFacePause();
        if (IsVisible)
        {
            ArmUnfocusedTimer();
        }
        else
        {
            _unfocusedTimer.Stop();
            _peekTimer.Stop();
            _animator.Stop();
        }
    }

    /// <summary>
    /// Pauses decorative face animation when it can't be seen, and tells the focus timer what is on screen
    /// (countdown text in bar/panel/peek; only the progress line in the notch; nothing in the floating bubble).
    /// </summary>
    private void UpdateFacePause()
    {
        _face.SetPaused(!IsVisible || (_viewModel.IsHidden && _docked));

        // Tucked away, the notch line or the bubble's ring shows progress (slow ticks); open, the time text (per second).
        _viewModel.Focus.SetVisibility(
            windowVisible: IsVisible,
            textVisible: IsVisible && !_viewModel.IsHidden);
    }

    // ===== Reminder alert: snooze choices =====================================

    private static readonly int[] SnoozeChoices = [5, 10, 15, 30, 60];

    /// <summary>Snooze on the reminder alert: 5 / 10 / 15 / 30 min, 1 hour, or Custom… (type any length).</summary>
    private void AlertSnooze_Click(object sender, RoutedEventArgs e)
    {
        var reminders = _viewModel.Reminders;
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("IslandMenu"),
            PlacementTarget = AlertSnoozeButton,
            Placement = PlacementMode.Bottom,
        };

        foreach (var minutes in SnoozeChoices)
        {
            var delay = TimeSpan.FromMinutes(minutes);
            var item = new MenuItem { Header = RemindersViewModel.DescribeDelay(delay), Style = (Style)FindResource("IslandMenuItem") };
            item.Click += (_, _) => reminders.SnoozeAlert(delay);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("IslandMenuSeparator") });
        var custom = new MenuItem { Header = "Custom…", Style = (Style)FindResource("IslandMenuItem") };
        custom.Click += (_, _) => Dispatcher.BeginInvoke(ShowCustomSnooze, DispatcherPriority.Input);
        menu.Items.Add(custom);

        TrackMenu(menu);
        menu.IsOpen = true;
    }

    /// <summary>A small box under Snooze: type "20", "45 min" or "2h", Enter to snooze (Esc or a click away closes it).</summary>
    private void ShowCustomSnooze()
    {
        var reminders = _viewModel.Reminders;
        var box = new TextBox
        {
            Style = (Style)FindResource("IslandInput"),
            FontSize = 13,
            MinWidth = 96,
            MaxLength = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Snooze for how long, for example 20 minutes or 2 hours");
        var hint = new TextBlock
        {
            Text = "Minutes or hours: 20, 45 min, 2h",
            FontSize = 11,
            Margin = new Thickness(4, 6, 0, 0),
            Foreground = (Brush)FindResource("SubtleTextBrush"),
        };
        var go = new Button { Style = (Style)FindResource("IslandPillButton"), Content = "Snooze", Margin = new Thickness(8, 0, 0, 0) };

        var input = new Border
        {
            Background = (Brush)FindResource("InputBrush"),
            BorderBrush = (Brush)FindResource("CardRimBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            Child = box,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(input);
        row.Children.Add(go);
        var content = new StackPanel();
        content.Children.Add(row);
        content.Children.Add(hint);

        var popup = new Popup
        {
            PlacementTarget = AlertSnoozeButton,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 6,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)FindResource("MenuSurfaceBrush"),
                BorderBrush = (Brush)FindResource("IslandRimBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10),
                Child = content,
            },
        };

        void Submit()
        {
            if (Core.Reminders.ReminderParser.TryParseDuration(box.Text, out var delay))
            {
                popup.IsOpen = false;
                reminders.SnoozeAlert(delay);
            }
            else
            {
                hint.Text = "Try 20, 45 min or 2h (up to 24 h)";
                hint.Foreground = (Brush)FindResource("WarningBrush");
            }
        }

        go.Click += (_, _) => Submit();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Submit();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };

        TrackPopup(popup);
        popup.IsOpen = true;
        Dispatcher.BeginInvoke(() => Keyboard.Focus(box), DispatcherPriority.Input);
    }

    // ===== Options menu =====================================================

    private void OptionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (OptionsMenuFactory is null)
        {
            return;
        }

        var menu = OptionsMenuFactory();
        menu.PlacementTarget = OptionsButton;
        menu.Placement = PlacementMode.Bottom;
        TrackMenu(menu);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Any menu Mithu opens (⚙, note actions, sort) is a separate popup. While it is open Mithu must not close
    /// on "click away"; when it closes, close Mithu too if the user clicked outside both.
    /// </summary>
    public void TrackMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => OnMenuClosed();
    }

    /// <summary>Same as <see cref="TrackMenu"/> for other popups (e.g. the reminder date picker).</summary>
    public void TrackPopup(Popup popup)
    {
        popup.Opened += (_, _) => _menuOpen = true;
        popup.Closed += (_, _) => OnMenuClosed();
    }

    /// <summary>A dialog Mithu opens (e.g. the folder picker) is open / closed: Mithu stays open meanwhile.</summary>
    public void TrackDialog(bool open)
    {
        if (open)
        {
            _menuOpen = true;
        }
        else
        {
            _menuOpen = false;
            Activate(); // back from the dialog: Mithu has the focus again
        }
    }

    private void OnMenuClosed()
    {
        _menuOpen = false;
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsActive && ClickAwayPolicy.ShouldCloseOnClickAway(CollapseState()))
            {
                _viewModel.Mode = _viewModel.TuckedMode;
            }
        }, DispatcherPriority.Input);
    }

    // ===== Keyboard and mouse wheel =========================================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // A popup is open (e.g. the date picker): Esc closes just that, handled by the page.
        if (e.Key == Key.Escape && _menuOpen)
        {
            return;
        }

        // Mithu is writing a reply: Esc stops it (a second Esc goes back as usual).
        if (e.Key == Key.Escape && _viewModel.IsExpanded && _viewModel.Page == PanelPage.Ask && _viewModel.Ask.IsBusy)
        {
            _viewModel.Ask.StopCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // On a page (Focus, Notes, an open note), Esc is "back" first; on Home it makes Mithu smaller as usual.
        if (e.Key == Key.Escape && _viewModel.IsExpanded && _viewModel.Page != PanelPage.Home)
        {
            _viewModel.GoBack();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape || (ctrl && e.Key == Key.Up))
        {
            if (_viewModel.SmallerCommand.CanExecute(null))
            {
                _viewModel.SmallerCommand.Execute(null);
            }

            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Down)
        {
            if (_viewModel.BiggerCommand.CanExecute(null))
            {
                _viewModel.BiggerCommand.Execute(null);
            }

            e.Handled = true;
        }
    }

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Don't steal scrolling from text the user is reading/typing, or from lists (notes).
        if (e.OriginalSource is DependencyObject source && (IsInside<TextBox>(source) || IsInside<ScrollViewer>(source)))
        {
            return;
        }

        // One wheel notch = one step; ignore extra notches while a transition is still playing.
        if (_animator.IsRunning)
        {
            e.Handled = true;
            return;
        }

        var command = e.Delta > 0 ? _viewModel.SmallerCommand : _viewModel.BiggerCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }

    private static bool IsInside<T>(DependencyObject element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    // ===== Face: click vs. drag =============================================

    private void Face_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        // Stuck to the top: the face slides left/right only; a press without sideways movement is a click.
        if (BeginSlidePress(FaceHost, e, onClick: () => _viewModel.FaceClickedCommand.Execute(null)))
        {
            return;
        }

        // Floating: free drag (any direction) once the pointer moves; otherwise a click.
        _facePressPoint = e.GetPosition(this);
        FaceHost.CaptureMouse();
    }

    private void Face_MouseMove(object sender, MouseEventArgs e)
    {
        if (_slidePressScreen is not null)
        {
            UpdateSlide(e);
            return;
        }

        if (_facePressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            _facePressPoint = null;
            FaceHost.ReleaseMouseCapture();
            BeginDrag();
        }
    }

    private void Face_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (EndSlidePress(e))
        {
            e.Handled = true;
            return;
        }

        if (_facePressPoint is null)
        {
            return;
        }

        _facePressPoint = null;
        FaceHost.ReleaseMouseCapture();
        _viewModel.FaceClickedCommand.Execute(null);
        e.Handled = true;
    }

    private void Face_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            _viewModel.FaceClickedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Face_MouseEnter(object sender, MouseEventArgs e) => _face.SetHover(true);

    private void Face_MouseLeave(object sender, MouseEventArgs e) => _face.SetHover(false);

    // ===== Header: drag area ================================================

    private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInside<ButtonBase>(source))
        {
            return;
        }

        // Stuck to the top: the header slides left/right only (no click action).
        if (BeginSlidePress(Header, e, onClick: null))
        {
            e.Handled = true;
            return;
        }

        BeginDrag();
    }

    private void Header_MouseMove(object sender, MouseEventArgs e) => UpdateSlide(e);

    private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (EndSlidePress(e))
        {
            e.Handled = true;
        }
    }

    private void SlideTarget_LostMouseCapture(object sender, MouseEventArgs e) => CancelSlidePress();

    /// <summary>Free drag in any direction. Only used while floating (not stuck to the top).</summary>
    private void BeginDrag()
    {
        if (_docked || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _unfocusedTimer.Stop();
        _dragging = true;
        try
        {
            DragMove(); // Blocks until the mouse button is released.
        }
        catch (InvalidOperationException)
        {
            return;
        }
        finally
        {
            _dragging = false;
        }

        // Floating: dropping it at the top edge sticks it to the top.
        _placement.SaveCurrent(this);
        RefreshPlacement();
    }
}
