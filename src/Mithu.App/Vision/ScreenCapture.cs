using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mithu.Core.Vision;
using static Mithu.App.Interop.CaptureNative;

namespace Mithu.App.Vision;

/// <summary>A frozen picture of one monitor, plus where its windows are (front to back), all in physical pixels.</summary>
public sealed record ScreenSnapshot(BitmapSource Image, CaptureRect Monitor, IReadOnlyList<CaptureRect> Windows);

/// <summary>
/// Takes screenshots: copies the monitor under the mouse (Mithu's own windows are excluded from the window list
/// and are hidden by the caller first), crops the user's pick, and encodes PNG — full size for keeping and OCR,
/// scaled to <see cref="CaptureGeometry.MaxSideForAi"/> for the AI.
/// </summary>
public static class ScreenCapture
{
    public static ScreenSnapshot SnapMonitorUnderCursor()
    {
        GetCursorPos(out var cursor);
        return Snap(MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST));
    }

    /// <summary>
    /// The whole monitor a window is on, as PNG, leaving that window out of the picture (Mithu looking at the
    /// screen it sits on). Falls back to briefly hiding the window where Windows can't exclude it.
    /// </summary>
    public static async Task<byte[]> SnapScreenBehindAsync(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var excluded = handle != IntPtr.Zero && window.IsVisible && SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);
        var hidden = !excluded && window.IsVisible;
        if (hidden)
        {
            window.Hide();
        }

        try
        {
            await Task.Delay(ExcludeDelay); // the change shows on the next composed frames
            var snapshot = Snap(MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST));
            return Encode(snapshot.Image);
        }
        finally
        {
            if (excluded)
            {
                SetWindowDisplayAffinity(handle, WDA_NONE);
            }
            else if (hidden)
            {
                window.Show();
            }
        }
    }

    /// <summary>Time for Windows to redraw the screen without the window.</summary>
    private static readonly TimeSpan ExcludeDelay = TimeSpan.FromMilliseconds(150);

    private static ScreenSnapshot Snap(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var bounds = new CaptureRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Width, info.Monitor.Height);
        return new ScreenSnapshot(Copy(bounds), bounds, VisibleWindows(bounds));
    }

    /// <summary>Copies a screen rectangle (physical pixels) into a frozen bitmap.</summary>
    private static BitmapSource Copy(CaptureRect rect)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, rect.Width, rect.Height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            BitBlt(memory, 0, 0, rect.Width, rect.Height, screen, rect.X, rect.Y, SRCCOPY | CAPTUREBLT);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Visible, uncloaked, non-minimised windows of other apps that touch the monitor, front to back.</summary>
    private static IReadOnlyList<CaptureRect> VisibleWindows(CaptureRect monitor)
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<CaptureRect>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var process);
            if (process == own || (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0))
            {
                return true;
            }

            // The visible frame (without the invisible resize borders Windows adds).
            if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
            {
                GetWindowRect(hWnd, out frame);
            }

            var rect = new CaptureRect(frame.Left, frame.Top, frame.Width, frame.Height);
            if (rect.Width >= 40 && rect.Height >= 40 && !rect.Intersect(monitor).IsEmpty)
            {
                list.Add(rect);
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The picked part of the snapshot (rect is relative to the monitor) as full-size PNG bytes.</summary>
    public static byte[] Crop(ScreenSnapshot snapshot, CaptureRect pick)
    {
        var area = pick.Intersect(new CaptureRect(0, 0, snapshot.Image.PixelWidth, snapshot.Image.PixelHeight));
        BitmapSource image = area.Width == snapshot.Image.PixelWidth && area.Height == snapshot.Image.PixelHeight
            ? snapshot.Image
            : new CroppedBitmap(snapshot.Image, new Int32Rect(area.X, area.Y, area.Width, area.Height));
        return Encode(image);
    }

    /// <summary>A copy no larger than the AI needs (or the same bytes when already small enough).</summary>
    public static byte[] ForAi(byte[] png)
    {
        var decoder = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var (width, height) = CaptureGeometry.FitForAi(frame.PixelWidth, frame.PixelHeight);
        if (width == frame.PixelWidth)
        {
            return png;
        }

        var scaled = new TransformedBitmap(frame, new ScaleTransform((double)width / frame.PixelWidth, (double)height / frame.PixelHeight));
        return Encode(scaled);
    }

    /// <summary>A small preview (for the attachment chip and chat bubbles).</summary>
    public static BitmapSource Thumbnail(byte[] png, int decodeHeight)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelHeight = decodeHeight;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Opens a saved screenshot in the user's image viewer.</summary>
    public static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static byte[] Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
