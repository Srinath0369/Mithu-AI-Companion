namespace Mithu.App.Services;

/// <summary>Reading and writing the clipboard's text (an interface so tests can supply their own).</summary>
public interface IClipboardAccess
{
    /// <summary>The copied text, or null when the clipboard holds no text (or can't be read).</summary>
    string? ReadText();

    bool WriteText(string text);
}

/// <summary>The real clipboard (on the UI thread).</summary>
public sealed class WindowsClipboard : IClipboardAccess
{
    public string? ReadText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // another app has it open
        }
    }

    public bool WriteText(string text) => ClipboardText.TrySet(text);
}