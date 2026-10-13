using System.Runtime.InteropServices.WindowsRuntime;
using Mithu.Core.Interfaces;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Mithu.Infrastructure.Vision;

/// <summary>
/// Text recognition with Windows' built-in OCR (<c>Windows.Media.Ocr</c>), in the languages installed for the user.
/// Runs entirely on this PC: nothing is sent anywhere, and it works with AI off.
/// </summary>
public sealed class WindowsTextRecognizer : ITextRecognizer
{
    public async Task<string> RecognizeAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("No text-recognition language is installed in Windows.");

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);

        // Very large images are scaled down to what the engine accepts.
        var max = OcrEngine.MaxImageDimension;
        var scale = Math.Min(1.0, (double)max / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }
}
