using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Mithu.Core.Vision;

namespace Mithu.App.Vision;

/// <summary>
/// The screenshot picker: the frozen screen, dimmed, over the whole monitor. Drag to pick an area; hover to see the
/// window under the mouse and click to take it; Enter (or a click on the empty desktop) takes the whole screen;
/// Esc cancels. Works in physical pixels (per-monitor DPI), shown in device-independent units.
/// </summary>
public sealed class CaptureOverlay : Window
{
    private static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0x05, 0x08, 0x10)));
    private static readonly Brush Cyan = Frozen(new SolidColorBrush(Color.FromRgb(0x2D, 0xF3, 0xF3)));

    private readonly ScreenSnapshot _snapshot;
    private readonly IReadOnlyList<CaptureRect> _windows;   // relative to the monitor
    private readonly CaptureRect _screen;                   // 0,0,width,height
    private readonly TaskCompletionSource<(CaptureRect Rect, CaptureKind Kind)?> _result = new();
    private readonly Path _shade = new() { Fill = Dim, IsHitTestVisible = false };
    private readonly Rectangle _outline = new() { Stroke = Cyan, StrokeThickness = 2, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Border _sizeTag;
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 11 };
    private readonly Canvas _canvas = new();
    private Point? _start;

    public CaptureOverlay(ScreenSnapshot snapshot)
    {
        _snapshot = snapshot;
        _screen = new CaptureRect(0, 0, snapshot.Monitor.Width, snapshot.Monitor.Height);
        _windows = snapshot.Windows.Select(w => w.Offset(-snapshot.Monitor.X, -snapshot.Monitor.Y)).ToList();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Background = Brushes.Black;
        Title = "Mithu screenshot";

        _sizeTag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x15, 0x1C, 0x28)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _sizeText,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        var hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x15, 0x1C, 0x28)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 7, 16, 7),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "👀  Drag to pick an area  ·  Click a window  ·  Enter for the whole screen  ·  Esc to cancel",
                Foreground = Brushes.White,
                FontSize = 13,
                FontFamily = Themes.AppFonts.Ui,
            },
        };

        var root = new Grid();
        root.Children.Add(new Image { Source = snapshot.Image, Stretch = Stretch.Fill });
        root.Children.Add(_shade);
        _canvas.Children.Add(_outline);
        _canvas.Children.Add(_sizeTag);
        root.Children.Add(_canvas);
        root.Children.Add(hint);
        Content = root;
        System.Windows.Automation.AutomationProperties.SetName(this, "Pick what to capture");

        SourceInitialized += (_, _) => PlaceOnMonitor();
        Loaded += (_, _) =>
        {
            Activate();
            Keyboard.Focus(this);
            Show(_screen, highlightOnly: true);
        };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        KeyDown += OnKey;
        MouseRightButtonUp += (_, _) => Finish(null);
        Deactivated += (_, _) => Finish(null);
    }

    /// <summary>Shows the overlay and waits for the pick (relative to the monitor), or null if cancelled.</summary>
    public Task<(CaptureRect Rect, CaptureKind Kind)?> PickAsync()
    {
        Show();
        return _result.Task;
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>Covers the monitor exactly, in physical pixels.</summary>
    private void PlaceOnMonitor()
    {
        var handle = new WindowInteropHelper(this).Handle;

        // A still picture: drawn in software, so no full-screen graphics-card surfaces are created (WPF keeps those
        // around after the window closes, tens of MB).
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
        {
            target.RenderMode = RenderMode.SoftwareOnly;
        }

        var m = _snapshot.Monitor;
        Interop.NativeMethods.SetWindowPos(handle, new IntPtr(-1), m.X, m.Y, m.Width, m.Height, 0x0040 /* SWP_SHOWWINDOW */);
    }

    private (int X, int Y) ToPixels(Point p) => ((int)Math.Round(p.X * Scale), (int)Math.Round(p.Y * Scale));

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var (x, y) = ToPixels(e.GetPosition(this));
        if (_start is { } start && e.LeftButton == MouseButtonState.Pressed)
        {
            var (sx, sy) = ToPixels(start);
            if (CaptureGeometry.IsDrag(sx, sy, x, y))
            {
                Show(CaptureGeometry.FromDrag(sx, sy, x, y, _screen), highlightOnly: false);
            }

            return;
        }

        // Hovering: the window that a click would take.
        if (CaptureGeometry.WindowAt(x, y, _windows, _screen) is { } window)
        {
            Show(window, highlightOnly: false);
        }
        else
        {
            Show(_screen, highlightOnly: true);
        }
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_start is not { } start)
        {
            return;
        }

        var (sx, sy) = ToPixels(start);
        var (x, y) = ToPixels(e.GetPosition(this));
        _start = null;
        if (CaptureGeometry.IsDrag(sx, sy, x, y))
        {
            var region = CaptureGeometry.FromDrag(sx, sy, x, y, _screen);
            Finish(region.IsEmpty ? null : (region, CaptureKind.Region));
        }
        else
        {
            var window = CaptureGeometry.WindowAt(x, y, _windows, _screen);
            Finish(window is { } w ? (w, CaptureKind.Window) : (_screen, CaptureKind.WholeScreen));
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Finish(null);
        }
        else if (e.Key == Key.Enter)
        {
            Finish((_screen, CaptureKind.WholeScreen));
        }
    }

    /// <summary>Draws the pick: everything else dimmed, a cyan outline, and its size.</summary>
    private void Show(CaptureRect pick, bool highlightOnly)
    {
        var s = Scale;
        var full = new RectangleGeometry(new Rect(0, 0, _screen.Width / s, _screen.Height / s));
        var hole = new Rect(pick.X / s, pick.Y / s, pick.Width / s, pick.Height / s);
        _shade.Data = highlightOnly ? full : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole));

        _outline.Visibility = _sizeTag.Visibility = highlightOnly ? Visibility.Collapsed : Visibility.Visible;
        if (!highlightOnly)
        {
            Canvas.SetLeft(_outline, hole.X);
            Canvas.SetTop(_outline, hole.Y);
            _outline.Width = hole.Width;
            _outline.Height = hole.Height;
            _sizeText.Text = string.Create(CultureInfo.InvariantCulture, $"{pick.Width} × {pick.Height}");
            Canvas.SetLeft(_sizeTag, hole.X);
            Canvas.SetTop(_sizeTag, Math.Max(0, hole.Y - 24));
        }
    }

    /// <summary>
    /// Reports the pick. The caller closes the overlay after bringing Mithu forward: closing first would hand
    /// the keyboard to whatever app is underneath, and Mithu could no longer take it back.
    /// </summary>
    private void Finish((CaptureRect Rect, CaptureKind Kind)? result) => _result.TrySetResult(result);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
