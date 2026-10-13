using System.Runtime.InteropServices;
using System.Windows;

namespace Mithu.App.Services;

/// <summary>Puts text on the clipboard, retrying briefly when another app has it open.</summary>
public static class ClipboardText
{
    public static bool TrySet(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }

        return false;
    }

    /// <summary>
    /// Puts a PNG on the clipboard as an image: as "PNG" (what browsers paste, e.g. into Google Lens) and as a
    /// bitmap (what most other apps paste).
    /// </summary>
    public static bool TrySetImage(byte[] png)
    {
        var image = new System.Windows.Media.Imaging.PngBitmapDecoder(
            new System.IO.MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetData("PNG", new System.IO.MemoryStream(png), autoConvert: false);
                data.SetImage(image);
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }

        return false;
    }
}
