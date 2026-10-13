using System.Windows;
using Mithu.Core.Notes;

namespace Mithu.App.Controls;

/// <summary>
/// Notes on the clipboard: Mithu's own format (note text, so pasting into another note keeps bullets, checklists,
/// headings and bold) plus clean readable text for other apps (no <c>**</c>; "• " bullets, "☐ / ☑" items).
/// </summary>
public static class NoteClipboard
{
    /// <summary>Clipboard format holding note text (the stored Markdown subset).</summary>
    public const string Format = "Mithu.Note";

    public static DataObject Create(string markdown)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, NoteMarkdown.ToPlainText(markdown));
        data.SetData(Format, markdown);
        return data;
    }

    /// <summary>Note text from the clipboard: Mithu's own format, or text from another app; null if there is none.</summary>
    public static (string Markdown, bool FromMithu)? Read(IDataObject data)
    {
        if (data.GetDataPresent(Format) && data.GetData(Format) is string markdown)
        {
            return (markdown, true);
        }

        return data.GetData(DataFormats.UnicodeText) is string text ? (NoteMarkdown.FromPastedText(text), false) : null;
    }
}
