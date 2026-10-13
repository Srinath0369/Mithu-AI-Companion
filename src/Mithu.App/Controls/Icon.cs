using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Mithu.App.Controls;

/// <summary>
/// A crisp vector icon, drawn from path data (outline style on a 24×24 grid, round ends, in the spirit of Lucide).
/// Icons are named by the Segoe Fluent code they replace (<c>Glyph="&amp;#xE72B;"</c> is "back"), so a glyph
/// becomes an icon by swapping the element. Takes its colour from <see cref="Foreground"/> and its size from
/// <see cref="FontSize"/> (both inherited, like text). An unknown code falls back to the font glyph.
/// </summary>
public sealed class Icon : FrameworkElement
{
    /// <summary>Box size relative to the font size: the drawing fills ~5/6 of the box, matching a glyph's look.</summary>
    private const double BoxPerFontSize = 1.25;

    /// <summary>Stroke width on the 24-unit grid, and the thinnest a stroke may get on screen (pixels).</summary>
    private const double StrokeWidth = 2;
    private const double MinStrokePixels = 1.2;

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(Icon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits
            | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    static Icon()
    {
        // Decorative: the button or text next to it carries the name.
        FocusableProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(true));

        // Like the text glyph it replaces: sits at the left of its space (not centred across it) unless told otherwise.
        HorizontalAlignmentProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(HorizontalAlignment.Left));
    }

    /// <summary>The Segoe Fluent code this icon replaces (e.g. "\uE72B" for back).</summary>
    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    /// <summary>True when a vector shape exists for the code (otherwise the glyph is drawn from the font).</summary>
    public static bool HasShape(string? glyph) => glyph is not null && IconShapes.All.ContainsKey(glyph);

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Round(FontSize * BoxPerFontSize);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var glyph = Glyph;
        if (string.IsNullOrEmpty(glyph))
        {
            return;
        }

        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0)
        {
            return;
        }

        if (!IconShapes.All.TryGetValue(glyph, out var shape))
        {
            DrawFallback(dc, glyph);
            return;
        }

        var scale = side / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (shape.Fill is { } fill)
        {
            dc.DrawGeometry(Foreground, null, fill);
        }

        if (shape.Stroke is { } stroke)
        {
            var pen = new Pen(Foreground, Math.Max(StrokeWidth, MinStrokePixels / scale))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, stroke);
        }

        dc.Pop();
        dc.Pop();
    }

    /// <summary>A code without a vector shape: draw the font glyph, as before.</summary>
    private void DrawFallback(DrawingContext dc, string glyph)
    {
        var text = new FormattedText(
            glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI"), FontSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point((ActualWidth - text.Width) / 2, (ActualHeight - text.Height) / 2));
    }
}

/// <summary>Turns a glyph string in an icon button into an <see cref="Icon"/>; other content shows as usual.</summary>
public sealed class GlyphTemplateSelector : DataTemplateSelector
{
    private static readonly DataTemplate IconTemplate = Create();

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is string { Length: 1 } text && text[0] is >= '\uE000' and <= '\uF8FF' ? IconTemplate : null;

    private static DataTemplate Create()
    {
        var factory = new FrameworkElementFactory(typeof(Icon));
        factory.SetBinding(Icon.GlyphProperty, new System.Windows.Data.Binding());
        var template = new DataTemplate { VisualTree = factory };
        template.Seal();
        return template;
    }
}

/// <summary>The icon set: outline strokes and (for solid parts) fills, on a 24×24 grid.</summary>
internal static class IconShapes
{
    internal sealed record Shape(Geometry? Stroke, Geometry? Fill);

    private static Shape S(string stroke) => new(Parse(stroke), null);

    private static Shape F(string fill) => new(null, Parse(fill));

    /// <summary>Filled and outlined with round joins: solid shapes with softened corners.</summary>
    private static Shape Solid(string path) => new(Parse(path), Parse(path));

    private static Geometry Parse(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static string Dot(double x, double y, double r) =>
        string.Create(CultureInfo.InvariantCulture, $"M{x + r},{y} A{r},{r} 0 1 1 {x - r},{y} A{r},{r} 0 1 1 {x + r},{y} Z ");

    private const string PinBody =
        "M9,10.76 A2,2 0 0 1 7.89,12.55 L6.11,13.45 A2,2 0 0 0 5,15.24 V16 A1,1 0 0 0 6,17 H18 A1,1 0 0 0 19,16 V15.24 "
        + "A2,2 0 0 0 17.89,13.45 L16.11,12.55 A2,2 0 0 1 15,10.76 V7 A1,1 0 0 1 16,6 A2,2 0 0 0 16,2 H8 A2,2 0 0 0 8,6 A1,1 0 0 1 9,7 Z";

    /// <summary>By Segoe Fluent code.</summary>
    public static readonly IReadOnlyDictionary<string, Shape> All = new Dictionary<string, Shape>
    {
        // Navigation
        ["\uE72B"] = S("M19,12 H5 M12,19 L5,12 L12,5"),                                   // back
        ["\uE70D"] = S("M6,9 L12,15 L18,9"),                                                // chevron down
        ["\uE70E"] = S("M18,15 L12,9 L6,15"),                                               // chevron up
        ["\uE76B"] = S("M15,18 L9,12 L15,6"),                                               // chevron left
        ["\uE76C"] = S("M9,18 L15,12 L9,6"),                                                // chevron right
        ["\uE711"] = S("M18,6 L6,18 M6,6 L18,18"),                                         // close
        ["\uE712"] = F(Dot(5, 12, 1.7) + Dot(12, 12, 1.7) + Dot(19, 12, 1.7)),             // more

        // Actions
        ["\uE710"] = S("M5,12 H19 M12,5 V19"),                                             // add
        ["\uE738"] = S("M5,12 H19"),                                                        // remove
        ["\uE73E"] = S("M20,6 L9,17 L4,12"),                                                // check
        ["\uE8C8"] = S("M10,8 H20 A2,2 0 0 1 22,10 V20 A2,2 0 0 1 20,22 H10 A2,2 0 0 1 8,20 V10 A2,2 0 0 1 10,8 Z "
            + "M4,16 A2,2 0 0 1 2,14 V4 A2,2 0 0 1 4,2 H14 A2,2 0 0 1 16,4"),                // copy
        ["\uE724"] = S("M14.536,21.686 A0.5,0.5 0 0 0 15.473,21.662 L21.973,2.662 A0.496,0.496 0 0 0 21.338,2.027 "
            + "L2.338,8.527 A0.5,0.5 0 0 0 2.314,9.464 L10.244,12.644 A2,2 0 0 1 11.356,13.754 Z M21.854,2.147 L10.914,13.086"), // send
        ["\uE721"] = S("M19,11 A8,8 0 1 1 3,11 A8,8 0 1 1 19,11 Z M21,21 L16.7,16.7"),      // search
        ["\uE71C"] = S("M3,4 H21 L14,12.5 V19 L10,21 V12.5 Z"),                            // filter
        ["\uE8CB"] = S("M21,16 L17,20 L13,16 M17,20 V4 M3,8 L7,4 L11,8 M7,4 V20"),         // sort
        ["\uE7A7"] = S("M9,14 L4,9 L9,4 M4,9 H14.5 A5.5,5.5 0 0 1 14.5,20 H11"),           // undo
        ["\uE7B8"] = S("M3,3 H21 A1,1 0 0 1 22,4 V7 A1,1 0 0 1 21,8 H3 A1,1 0 0 1 2,7 V4 A1,1 0 0 1 3,3 Z "
            + "M4,8 V19 A2,2 0 0 0 6,21 H18 A2,2 0 0 0 20,19 V8 M10,12 H14"),                // archive
        ["\uE718"] = S("M12,17 V22 " + PinBody),                                            // pin
        ["\uE840"] = new(Parse("M12,17 V22 " + PinBody), Parse(PinBody)),                   // pinned
        ["\uE8B7"] = S("M4,3 H7.93 A2,2 0 0 1 9.6,3.9 L10.41,5.1 A2,2 0 0 0 12.1,6 H20 A2,2 0 0 1 22,8 V18 A2,2 0 0 1 20,20 H4 A2,2 0 0 1 2,18 V5 A2,2 0 0 1 4,3 Z"), // folder
        ["\uE9D5"] = S("M5,3 H19 A2,2 0 0 1 21,5 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z M8,12 L11,15 L16,9"), // to-do (ticked box)
        ["\uE734"] = S("M12,2 L14.65,8.36 L21.51,8.91 L16.28,13.39 L17.88,20.09 L12,16.5 L6.12,20.09 L7.72,13.39 L2.49,8.91 L9.35,8.36 Z"), // star
        ["\uE735"] = Solid("M12,2 L14.65,8.36 L21.51,8.91 L16.28,13.39 L17.88,20.09 L12,16.5 L6.12,20.09 L7.72,13.39 L2.49,8.91 L9.35,8.36 Z"), // star (filled)
        ["\uF16A"] = S("M21,12 A9,9 0 1 1 12,3"),                                           // working (an arc; spun while running)
        ["\uE783"] = S("M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 Z M12,8 V12 M12,16 H12.01"), // problem
        ["\uE9CE"] = S("M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 Z M9.09,9 A3,3 0 0 1 14.92,10 C14.92,12 11.92,13 11.92,13 M12,17 H12.01"), // question
        ["\uEDA2"] =new(Parse("M4,13 H20 A2,2 0 0 1 22,15 V18 A2,2 0 0 1 20,20 H4 A2,2 0 0 1 2,18 V15 A2,2 0 0 1 4,13 Z M2,15 L5.45,5.11 A2,2 0 0 1 7.24,4 H16.76 A2,2 0 0 1 18.55,5.11 L22,15"),
            Parse("M6,15.5 A1,1 0 1 1 6,17.5 A1,1 0 1 1 6,15.5 Z")), // disk
        ["\uE7F4"] = S("M4,4 H20 A2,2 0 0 1 22,6 V15 A2,2 0 0 1 20,17 H4 A2,2 0 0 1 2,15 V6 A2,2 0 0 1 4,4 Z M8,21 H16 M12,17 V21"), // desktop (monitor)
        ["\uE8EE"] = S("M17,2 L21,6 L17,10 M3,11 V10 A4,4 0 0 1 7,6 H21 M7,22 L3,18 L7,14 M21,13 V14 A4,4 0 0 1 17,18 H3"), // repeat

        // Media
        ["\uE768"] = Solid("M7,4.8 L19,12 L7,19.2 Z"),                                     // play
        ["\uE769"] = Solid("M7,5 H9.5 V19 H7 Z M14.5,5 H17 V19 H14.5 Z"),                  // pause
        ["\uE71A"] = Solid("M8,7 H16 A1,1 0 0 1 17,8 V16 A1,1 0 0 1 16,17 H8 A1,1 0 0 1 7,16 V8 A1,1 0 0 1 8,7 Z"), // stop

        // Features
        ["\uE8BD"] = S("M7.9,20 A9,9 0 1 0 4,16.1 L2,22 Z"),                                // chat
        ["\uEA8F"] = S("M10.268,21 A2,2 0 0 0 13.732,21 M3.262,15.326 A1,1 0 0 0 4,17 H20 A1,1 0 0 0 20.74,15.327 "
            + "C19.41,13.956 18,12.499 18,8 A6,6 0 0 0 6,8 C6,12.499 4.589,13.956 3.262,15.326 Z"), // bell
        ["\uE70B"] = S("M16,3 H5 A2,2 0 0 0 3,5 V19 A2,2 0 0 0 5,21 H19 A2,2 0 0 0 21,19 V8 Z M15,3 V7 A2,2 0 0 0 17,9 H21"), // note
        ["\uE916"] = S("M10,2 H14 M12,14 L15,11 M20,14 A8,8 0 1 1 4,14 A8,8 0 1 1 20,14 Z"),  // timer
        ["\uE787"] = S("M8,2 V6 M16,2 V6 M5,4 H19 A2,2 0 0 1 21,6 V20 A2,2 0 0 1 19,22 H5 A2,2 0 0 1 3,20 V6 A2,2 0 0 1 5,4 Z M3,10 H21"), // calendar
        ["\uE823"] = S("M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 Z M12,6 V12 L16,14"),   // clock
        ["\uE701"] = S("M12,20 H12.01 M2,8.82 A15,15 0 0 1 22,8.82 M5,12.859 A10,10 0 0 1 19,12.859 M8.5,16.429 A5,5 0 0 1 15.5,16.429"), // connection
        ["\uE722"] = S("M3,7 V5 A2,2 0 0 1 5,3 H7 M17,3 H19 A2,2 0 0 1 21,5 V7 M21,17 V19 A2,2 0 0 1 19,21 H17 "
            + "M7,21 H5 A2,2 0 0 1 3,19 V17 M8,12 H16"),                                                    // screen snip
        ["\uE8D7"] = S("M4,15 A4,4 0 1 0 12,15 A4,4 0 1 0 4,15 Z M10.8,12.2 L20,3 M16,7 L19,10 M13.5,9.5 L15.5,11.5"), // key
        ["\uE83D"] = S("M12,22 C7,20.5 4,18 4,13 V6 C6,6 9,5 12,2.5 C15,5 18,6 20,6 V13 C20,18 17,20.5 12,22 Z M9,12 L11,14 L15,10"), // shield
        ["\uE9F9"] = S("M11,3 L12.8,9.2 L19,11 L12.8,12.8 L11,19 L9.2,12.8 L3,11 L9.2,9.2 Z M19,3 V7 M17,5 H21"),     // sparkle (AI)
        ["\uE945"] = S("M4,14 A1,1 0 0 1 3.22,12.37 L13.12,2.17 A0.5,0.5 0 0 1 13.98,2.63 L12.06,8.65 A1,1 0 0 0 13,10 H20 "
            + "A1,1 0 0 1 20.78,11.63 L10.88,21.83 A0.5,0.5 0 0 1 10.02,21.37 L11.94,15.35 A1,1 0 0 0 11,14 Z"), // power (AI on)

        // Text formatting (notes)
        ["\uE8DD"] = S("M6,12 H15 A4,4 0 0 1 15,20 H7 A1,1 0 0 1 6,19 V5 A1,1 0 0 1 7,4 H14 A4,4 0 0 1 14,12"), // bold
        ["\uE8DB"] = S("M19,4 H10 M14,20 H5 M15,4 L9,20"),                                 // italic
        ["\uE8FD"] = new(Parse("M8,6 H21 M8,12 H21 M8,18 H21"), Parse(Dot(3.5, 6, 1.4) + Dot(3.5, 12, 1.4) + Dot(3.5, 18, 1.4))), // bullets
        ["\uE73A"] = S("M3,17 L5,19 L9,15 M3,7 L5,9 L9,5 M13,6 H21 M13,12 H21 M13,18 H21"), // checklist
    };
}
