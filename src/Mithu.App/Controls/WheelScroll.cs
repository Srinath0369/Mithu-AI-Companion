using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Mithu.App.Controls;

/// <summary>
/// Gentler mouse-wheel scrolling for Mithu's small panel: <see cref="StepProperty"/> pixels per wheel notch
/// (WPF's default is ~48 px, which jumps half a page here), eased over a few frames. Precision touchpads scroll in
/// proportion to their small deltas. At the top or bottom the wheel is left alone, so a list inside a scrolling
/// page hands over to the page. Item-based (virtualizing) scrolling keeps WPF's own behaviour.
/// Set on every ScrollViewer by the implicit style in Controls.xaml.
/// </summary>
public static class WheelScroll
{
    /// <summary>Pixels per wheel notch; 0 turns the behaviour off.</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.RegisterAttached(
        "Step", typeof(double), typeof(WheelScroll), new PropertyMetadata(0.0, OnStepChanged));

    /// <summary>Where an eased scroll is heading (per ScrollViewer).</summary>
    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double), typeof(WheelScroll), new PropertyMetadata(double.NaN));

    /// <summary>Share of the remaining distance covered each frame (≈ 120 ms to settle at 60 fps).</summary>
    private const double EasePerFrame = 0.3;

    private static readonly HashSet<ScrollViewer> Moving = [];

    public static double GetStep(DependencyObject element) => (double)element.GetValue(StepProperty);

    public static void SetStep(DependencyObject element, double value) => element.SetValue(StepProperty, value);

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer)
        {
            return;
        }

        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((double)e.NewValue > 0)
        {
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (e.Handled || viewer.CanContentScroll || viewer.ScrollableHeight <= 0)
        {
            return; // nothing to scroll here (or item-based scrolling): let WPF / an outer list handle it
        }

        // The innermost scrollable list under the pointer takes the wheel; outer ones wait for it to reach its end.
        if (e.OriginalSource is DependencyObject source && InnerScrollable(source, viewer, down: e.Delta < 0) is { } inner && inner != viewer)
        {
            return;
        }

        var current = double.IsNaN((double)viewer.GetValue(TargetProperty)) ? viewer.VerticalOffset : (double)viewer.GetValue(TargetProperty);
        var target = Math.Clamp(current - (e.Delta / 120.0 * GetStep(viewer)), 0, viewer.ScrollableHeight);
        if (Math.Abs(target - current) < 0.5)
        {
            return; // already at the top/bottom: let the page around it scroll instead
        }

        e.Handled = true;
        viewer.SetValue(TargetProperty, target);
        if (Moving.Add(viewer))
        {
            if (Moving.Count == 1)
            {
                CompositionTarget.Rendering += OnRendering;
            }
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        foreach (var viewer in Moving.ToList())
        {
            var target = (double)viewer.GetValue(TargetProperty);
            var remaining = target - viewer.VerticalOffset;
            if (double.IsNaN(target) || Math.Abs(remaining) < 0.5 || !viewer.IsLoaded)
            {
                if (!double.IsNaN(target) && viewer.IsLoaded)
                {
                    viewer.ScrollToVerticalOffset(target);
                }

                viewer.ClearValue(TargetProperty);
                Moving.Remove(viewer);
                continue;
            }

            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + (remaining * EasePerFrame));
        }

        if (Moving.Count == 0)
        {
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    /// <summary>
    /// The nearest ScrollViewer between <paramref name="source"/> and <paramref name="outer"/> that can still move
    /// the way the wheel turned (wheel events reach the outer list first, so it must leave those to the inner one).
    /// </summary>
    private static ScrollViewer? InnerScrollable(DependencyObject source, ScrollViewer outer, bool down)
    {
        for (var node = source; node is not null && node != outer; node = Parent(node))
        {
            if (node is ScrollViewer { ScrollableHeight: > 0, CanContentScroll: false } inner && GetStep(inner) > 0)
            {
                var offset = double.IsNaN((double)inner.GetValue(TargetProperty)) ? inner.VerticalOffset : (double)inner.GetValue(TargetProperty);
                var canMove = down ? offset < inner.ScrollableHeight - 0.5 : offset > 0.5;
                if (canMove)
                {
                    return inner;
                }
            }
        }

        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
