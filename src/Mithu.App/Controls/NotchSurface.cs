using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mithu.App.Controls;

/// <summary>
/// The "island" surface: one shape that morphs between the notch, compact bar and expanded panel.
/// <para>
/// Docked: flat top flush with the screen edge, concave "ears" flaring into the edge (like a hardware notch),
/// rounded bottom corners. Floating: a rounded pill/card.
/// </para>
/// <para>
/// Performance: the child is laid out <b>once</b> at a fixed <see cref="ContentWidth"/> × <see cref="ContentHeight"/>
/// and centered; animating the body only changes the clip and redraws the outline. No per-frame layout,
/// no text reflow. The shadow is a few translucent strokes (no blur effect).
/// </para>
/// Hit-testing is limited to the visible outline, so the shadow never "catches" clicks meant for other apps.
/// </summary>
public sealed class NotchSurface : Decorator
{
    public static readonly DependencyProperty ContentWidthProperty = DependencyProperty.Register(
        nameof(ContentWidth), typeof(double), typeof(NotchSurface),
        new FrameworkPropertyMetadata(420.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ContentHeightProperty = DependencyProperty.Register(
        nameof(ContentHeight), typeof(double), typeof(NotchSurface),
        new FrameworkPropertyMetadata(356.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty TopInsetProperty = DependencyProperty.Register(
        nameof(TopInset), typeof(double), typeof(NotchSurface),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DockedProperty = DependencyProperty.Register(
        nameof(Docked), typeof(bool), typeof(NotchSurface),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((NotchSurface)d).UpdateClip()));

    public static readonly DependencyProperty EarRadiusProperty = DependencyProperty.Register(
        nameof(EarRadius), typeof(double), typeof(NotchSurface),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(NotchSurface),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RimProperty = DependencyProperty.Register(
        nameof(Rim), typeof(Brush), typeof(NotchSurface),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((NotchSurface)d)._rimPen = null));

    private static readonly Pen[] ShadowPens = CreateShadowPens();
    private static readonly TranslateTransform ShadowOffset = Freeze(new TranslateTransform(0, 2));

    private Pen? _rimPen;
    private Geometry? _outline;
    private double _bodyWidth = 420;
    private double _bodyHeight = 60;
    private double _bottomRadius = 24;

    public double ContentWidth
    {
        get => (double)GetValue(ContentWidthProperty);
        set => SetValue(ContentWidthProperty, value);
    }

    public double ContentHeight
    {
        get => (double)GetValue(ContentHeightProperty);
        set => SetValue(ContentHeightProperty, value);
    }

    /// <summary>Gap above the body (0 when docked so it touches the screen edge).</summary>
    public double TopInset
    {
        get => (double)GetValue(TopInsetProperty);
        set => SetValue(TopInsetProperty, value);
    }

    public bool Docked
    {
        get => (bool)GetValue(DockedProperty);
        set => SetValue(DockedProperty, value);
    }

    public double EarRadius
    {
        get => (double)GetValue(EarRadiusProperty);
        set => SetValue(EarRadiusProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Rim
    {
        get => (Brush?)GetValue(RimProperty);
        set => SetValue(RimProperty, value);
    }

    public double BodyWidth => _bodyWidth;

    public double BodyHeight => _bodyHeight;

    public double BottomRadius => _bottomRadius;

    /// <summary>
    /// Sets the animated body in one call (hot path, called every frame during a morph):
    /// one clip rebuild and one redraw, no layout.
    /// </summary>
    public void SetBody(double width, double height, double radius)
    {
        if (width == _bodyWidth && height == _bodyHeight && radius == _bottomRadius)
        {
            return;
        }

        _bodyWidth = Math.Max(1, width);
        _bodyHeight = Math.Max(1, height);
        _bottomRadius = Math.Max(0, radius);
        UpdateClip();
        InvalidateVisual();
    }

    /// <summary>Builds the island outline. Pure geometry; ears only apply when docked.</summary>
    public static Geometry BuildGeometry(double x, double y, double w, double h, double radius, double ear, bool docked)
    {
        w = Math.Max(w, 1);
        h = Math.Max(h, 1);

        if (!docked)
        {
            var r = Math.Min(radius, Math.Min(w, h) / 2);
            var rect = new RectangleGeometry(new Rect(x, y, w, h), r, r);
            rect.Freeze();
            return rect;
        }

        var br = Math.Max(0, Math.Min(radius, Math.Min(w / 2, h)));
        var e = Math.Max(0, Math.Min(ear, h - br));

        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(x - e, y), isFilled: true, isClosed: true);
            if (e > 0)
            {
                // Concave ear: the shape flares outward into the screen edge.
                c.ArcTo(new Point(x, y + e), new Size(e, e), 0, false, SweepDirection.Clockwise, true, false);
            }

            c.LineTo(new Point(x, y + h - br), true, false);
            c.ArcTo(new Point(x + br, y + h), new Size(br, br), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(x + w - br, y + h), true, false);
            c.ArcTo(new Point(x + w, y + h - br), new Size(br, br), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(x + w, y + e), true, false);
            if (e > 0)
            {
                c.ArcTo(new Point(x + w + e, y), new Size(e, e), 0, false, SweepDirection.Clockwise, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(new Size(ContentWidth, ContentHeight));
        return new Size(
            double.IsInfinity(constraint.Width) ? ContentWidth : constraint.Width,
            double.IsInfinity(constraint.Height) ? TopInset + ContentHeight : constraint.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        // Fixed layout: the content never re-flows during a morph.
        Child?.Arrange(new Rect((arrangeSize.Width - ContentWidth) / 2, TopInset, ContentWidth, ContentHeight));
        UpdateClip();
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var x = (ActualWidth - _bodyWidth) / 2;
        _outline = BuildGeometry(x, TopInset, _bodyWidth, _bodyHeight, _bottomRadius, Docked ? EarRadius : 0, Docked);

        // Soft shadow: translucent strokes, offset slightly down.
        dc.PushTransform(ShadowOffset);
        foreach (var pen in ShadowPens)
        {
            dc.DrawGeometry(null, pen, _outline);
        }

        dc.Pop();

        _rimPen ??= Rim is null ? null : Freeze(new Pen(Rim, 1));
        dc.DrawGeometry(Fill, _rimPen, _outline);
    }

    /// <summary>Only the visible island is clickable; the shadow and empty window area are not.</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        _outline is not null && _outline.FillContains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;

    /// <summary>Clips the content (laid out at ContentWidth, centered) to the current body, revealing from the center.</summary>
    private void UpdateClip()
    {
        if (Child is not UIElement child)
        {
            return;
        }

        var x = (ContentWidth - _bodyWidth) / 2;
        child.Clip = BuildGeometry(x, 0, _bodyWidth, _bodyHeight, _bottomRadius, 0, Docked);
    }

    private static Pen[] CreateShadowPens()
    {
        static Pen P(double thickness, byte alpha) =>
            Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0))), thickness) { LineJoin = PenLineJoin.Round });

        return [P(16, 0x0C), P(9, 0x14), P(4, 0x22)];
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
