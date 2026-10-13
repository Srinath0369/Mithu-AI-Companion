using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mithu.App.Services;

/// <summary>
/// Renders Mithu's face into a tray icon, so no binary .ico asset is needed.
/// </summary>
public static class TrayIconFactory
{
    /// <summary>Creates a Win32 icon (PNG-in-ICO) for the notification area.</summary>
    public static System.Drawing.Icon CreateIcon(int size = 32)
    {
        var png = RenderPng(size);
        var ico = H.NotifyIcon.PngToIcoConverter.ConvertPngToIco(png);
        using var stream = new MemoryStream(ico);
        return new System.Drawing.Icon(stream);
    }

    private static byte[] RenderPng(int size)
    {
        var midnight = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x16));
        var cyan = new SolidColorBrush(Color.FromRgb(0x2D, 0xF3, 0xF3));
        midnight.Freeze();
        cyan.Freeze();

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var s = size / 32.0;
            dc.DrawRoundedRectangle(midnight, null, new Rect(0, 3 * s, 32 * s, 26 * s), 11 * s, 11 * s);
            dc.DrawEllipse(cyan, null, new Point(10.5 * s, 13 * s), 3 * s, 4.2 * s);
            dc.DrawEllipse(cyan, null, new Point(21.5 * s, 13 * s), 3 * s, 4.2 * s);

            var smile = Geometry.Parse($"M {12 * s},{20 * s} Q {16 * s},{24 * s} {20 * s},{20 * s}");
            var pen = new Pen(cyan, 2 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, smile);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
