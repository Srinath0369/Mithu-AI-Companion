using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.WIC;

namespace Mithu.App.Controls;

/// <summary>
/// Colour emoji for WPF, which on its own draws emoji as one-colour outlines. Windows' DirectWrite draws each emoji
/// (gradients, skin tones, families, keycaps) once into a small bitmap at the exact pixel size; bitmaps are cached.
/// If DirectWrite isn't available the emoji stays as normal text.
/// </summary>
internal static class EmojiRenderer
{
    /// <summary>Cached bitmaps (each a few KB); cleared when full.</summary>
    private const int MaxCached = 300;

    /// <summary>Glyph size relative to the bitmap: Segoe UI Emoji draws slightly larger than its em box.</summary>
    private const float GlyphPerPixel = 0.74f;

    private static readonly Dictionary<(string Emoji, int Pixels), BitmapSource?> Cache = [];
    private static readonly Dictionary<int, IDWriteTextFormat> Formats = [];
    private static ID2D1Factory? _d2d;
    private static IDWriteFactory? _dwrite;
    private static IWICImagingFactory? _wic;
    private static bool _failed;

    /// <summary>The emoji drawn in colour on a transparent square of <paramref name="pixels"/>, or null.</summary>
    public static BitmapSource? Render(string emoji, int pixels)
    {
        pixels = Math.Clamp(pixels, 8, 256);
        if (_failed)
        {
            return null;
        }

        if (Cache.TryGetValue((emoji, pixels), out var cached))
        {
            return cached;
        }

        if (Cache.Count >= MaxCached)
        {
            Cache.Clear();
        }

        BitmapSource? result;
        try
        {
            result = Draw(emoji, pixels);
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException
            or DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _failed = true; // no DirectWrite colour support: plain text from now on
            return null;
        }

        return Cache[(emoji, pixels)] = result;
    }

    private static BitmapSource Draw(string emoji, int pixels)
    {
        _d2d ??= D2D1.D2D1CreateFactory<ID2D1Factory>();
        _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>();
        _wic ??= new IWICImagingFactory();
        if (!Formats.TryGetValue(pixels, out var format))
        {
            format = _dwrite.CreateTextFormat("Segoe UI Emoji", Vortice.DirectWrite.FontWeight.Normal,
                Vortice.DirectWrite.FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, pixels * GlyphPerPixel);
            format.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
            format.ParagraphAlignment = ParagraphAlignment.Center;
            format.WordWrapping = WordWrapping.NoWrap;
            Formats[pixels] = format;
        }

        using var bitmap = _wic.CreateBitmap((uint)pixels, (uint)pixels, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        using var target = _d2d.CreateWicBitmapRenderTarget(bitmap, new RenderTargetProperties(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
        using var brush = target.CreateSolidColorBrush(new Vortice.Mathematics.Color4(1, 1, 1, 1));
        target.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; // transparent background: no ClearType fringes
        target.BeginDraw();
        target.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        target.DrawText(emoji, format, new Vortice.Mathematics.Rect(0, 0, pixels, pixels), brush, DrawTextOptions.EnableColorFont);
        target.EndDraw();

        var stride = pixels * 4;
        var data = new byte[stride * pixels];
        bitmap.CopyPixels((uint)stride, data);
        var source = BitmapSource.Create(pixels, pixels, 96, 96, PixelFormats.Pbgra32, null, data, stride);
        source.Freeze();
        return source;
    }
}

/// <summary>One emoji in a line of text: a colour picture the size of the text around it.</summary>
public sealed class EmojiImage : FrameworkElement
{
    /// <summary>The emoji box relative to the font size (emoji sit a little larger than letters).</summary>
    private const double BoxPerFontSize = 1.3;

    public static readonly DependencyProperty EmojiProperty = DependencyProperty.Register(
        nameof(Emoji), typeof(string), typeof(EmojiImage),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(EmojiImage), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.Inherits
            | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(EmojiImage), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Emoji
    {
        get => (string)GetValue(EmojiProperty);
        set => SetValue(EmojiProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private double Box => Math.Round(FontSize * BoxPerFontSize);

    protected override Size MeasureOverride(Size availableSize) => new(Box, Box);

    protected override void OnRender(DrawingContext dc)
    {
        var box = Box;
        var dpi = VisualTreeHelper.GetDpi(this);
        var image = EmojiRenderer.Render(Emoji, (int)Math.Round(box * dpi.DpiScaleX));
        if (image is not null)
        {
            dc.DrawImage(image, new Rect(0, 0, box, box));
            return;
        }

        // No colour support: the emoji as text.
        var text = new FormattedText(Emoji, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI Emoji"), FontSize, Foreground, dpi.PixelsPerDip);
        dc.DrawText(text, new Point((box - text.Width) / 2, (box - text.Height) / 2));
    }
}

/// <summary>
/// Splits text into plain parts and emoji (with their skin tones, ZWJ joins, keycaps and flags), and builds inlines
/// that show the emoji in colour. <see cref="TextProperty"/> does it for a whole TextBlock.
/// </summary>
public static class EmojiText
{
    /// <summary>Use instead of TextBlock.Text: same text, emoji in colour.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(EmojiText), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var text = e.NewValue as string ?? string.Empty;
        if (!HasEmoji(text))
        {
            block.Text = text; // the common case: nothing special
            return;
        }

        block.Inlines.Clear();
        block.Inlines.AddRange(Inlines(text));
    }

    /// <summary>Plain text as runs, emoji as colour pictures.</summary>
    public static IEnumerable<Inline> Inlines(string text)
    {
        foreach (var (part, isEmoji) in Split(text))
        {
            yield return isEmoji
                ? new InlineUIContainer(new EmojiImage { Emoji = part }) { BaselineAlignment = BaselineAlignment.Center }
                : new Run(part);
        }
    }

    public static bool HasEmoji(string text) => Split(text).Any(p => p.IsEmoji);

    /// <summary>The text in order: plain parts and whole emoji (one entry per emoji, however many code points).</summary>
    public static IEnumerable<(string Text, bool IsEmoji)> Split(string text)
    {
        var plain = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var length = EmojiLength(text, i);
            if (length == 0)
            {
                plain.Append(text[i]);
                i++;
                continue;
            }

            if (plain.Length > 0)
            {
                yield return (plain.ToString(), false);
                plain.Clear();
            }

            yield return (text.Substring(i, length), true);
            i += length;
        }

        if (plain.Length > 0)
        {
            yield return (plain.ToString(), false);
        }
    }

    private const int Zwj = 0x200D;
    private const int Vs16 = 0xFE0F;
    private const int Keycap = 0x20E3;

    /// <summary>Length (in chars) of the emoji starting at <paramref name="start"/>, or 0 if none starts there.</summary>
    internal static int EmojiLength(string text, int start)
    {
        var first = CodePoint(text, start, out var size);

        // Keycaps: 1️⃣ #️⃣ *️⃣
        if (first is >= '0' and <= '9' or '#' or '*')
        {
            var at = start + size;
            if (At(text, at) == Vs16)
            {
                at++;
            }

            return At(text, at) == Keycap ? at + 1 - start : 0;
        }

        // Flags: two regional indicators.
        if (IsRegional(first))
        {
            var second = CodePoint(text, start + size, out var size2);
            return IsRegional(second) ? size + size2 : 0;
        }

        if (!StartsEmoji(first, text, start + size))
        {
            return 0;
        }

        var end = start + size;
        while (true)
        {
            end = AfterModifiers(text, end);
            if (At(text, end) != Zwj)
            {
                break;
            }

            var next = CodePoint(text, end + 1, out var nextSize);
            if (next < 0 || !IsEmojiCodePoint(next))
            {
                break;
            }

            end += 1 + nextSize; // 👨‍👩‍👧: joined into one
        }

        return end - start;
    }

    /// <summary>Skips the variation selector, skin tone and tag characters that belong to an emoji.</summary>
    private static int AfterModifiers(string text, int at)
    {
        while (true)
        {
            var cp = CodePoint(text, at, out var size);
            if (cp == Vs16 || cp is >= 0x1F3FB and <= 0x1F3FF || cp is >= 0xE0020 and <= 0xE007F)
            {
                at += size;
                continue;
            }

            return at;
        }
    }

    /// <summary>An emoji starts here: a picture character, or a symbol shown as emoji (by default or with ️).</summary>
    private static bool StartsEmoji(int cp, string text, int after) =>
        IsPictograph(cp) || (IsSymbol(cp) && (DefaultEmoji(cp) || At(text, after) == Vs16));

    private static bool IsEmojiCodePoint(int cp) => IsPictograph(cp) || IsSymbol(cp);

    /// <summary>Emoji planes (faces, objects, animals, food…), excluding skin tones and regional letters.</summary>
    private static bool IsPictograph(int cp) =>
        cp is >= 0x1F300 and <= 0x1FAFF && cp is not (>= 0x1F3FB and <= 0x1F3FF)
        || cp is >= 0x1F004 and <= 0x1F251 && !IsRegional(cp);

    /// <summary>Older symbols that can be emoji (❤ ☀ ✈ ⏰ ⭐ …).</summary>
    private static bool IsSymbol(int cp) =>
        cp is >= 0x2300 and <= 0x23FF or >= 0x2600 and <= 0x27BF or >= 0x2B00 and <= 0x2BFF
            or 0x203C or 0x2049 or 0x2122 or 0x2139 or >= 0x2194 and <= 0x21AA or 0x24C2 or >= 0x25AA and <= 0x25FE
            or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x00A9 or 0x00AE;

    /// <summary>Symbols that show as emoji even without ️ (Unicode "Emoji_Presentation" in the BMP).</summary>
    private static bool DefaultEmoji(int cp) => cp switch
    {
        0x231A or 0x231B or >= 0x23E9 and <= 0x23EC or 0x23F0 or 0x23F3 or 0x25FD or 0x25FE or 0x2614 or 0x2615
            or >= 0x2648 and <= 0x2653 or 0x267F or 0x2693 or 0x26A1 or 0x26AA or 0x26AB or 0x26BD or 0x26BE
            or 0x26C4 or 0x26C5 or 0x26CE or 0x26D4 or 0x26EA or 0x26F2 or 0x26F3 or 0x26F5 or 0x26FA or 0x26FD
            or 0x2705 or 0x270A or 0x270B or 0x2728 or 0x274C or 0x274E or >= 0x2753 and <= 0x2755 or 0x2757
            or >= 0x2795 and <= 0x2797 or 0x27B0 or 0x27BF or 0x2B1B or 0x2B1C or 0x2B50 or 0x2B55 => true,
        _ => false,
    };

    private static bool IsRegional(int cp) => cp is >= 0x1F1E6 and <= 0x1F1FF;

    private static int At(string text, int index) => index < text.Length ? text[index] : -1;

    private static int CodePoint(string text, int index, out int size)
    {
        size = 0;
        if (index >= text.Length)
        {
            return -1;
        }

        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            size = 2;
            return char.ConvertToUtf32(text[index], text[index + 1]);
        }

        size = 1;
        return text[index];
    }
}
