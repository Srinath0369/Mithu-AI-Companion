using System.Text;
using System.Text.RegularExpressions;

namespace Mithu.Core.Notes;

/// <summary>Plain-text helpers for notes: titles, previews, tags and friendly times (pure, unit-tested).</summary>
public static partial class NoteText
{
    public const int MaxTagLength = 24;
    public const int MaxTags = 10;

    private const int TitleLength = 60;
    private const int PreviewLength = 120;

    /// <summary>The title, else the first non-empty content line (formatting removed), else "Untitled note".</summary>
    public static string DisplayTitle(string title, string content)
    {
        if (title.Trim().Length > 0)
        {
            return title.Trim();
        }

        var first = Lines(content).Select(StripMarkdown).FirstOrDefault(l => l.Length > 0);
        return first is null ? "Untitled note" : Shorten(first, TitleLength);
    }

    /// <summary>
    /// One line of plain text from the content for the note list. When the title is empty the first line is
    /// already used as the title, so the preview starts after it.
    /// </summary>
    public static string Preview(string title, string content)
    {
        var lines = Lines(content).Select(StripMarkdown).Where(l => l.Length > 0);
        if (title.Trim().Length == 0)
        {
            lines = lines.Skip(1);
        }

        return Shorten(string.Join(" · ", lines), PreviewLength);
    }

    /// <summary>One line without its formatting (list/heading/checklist prefix, bold/italic markers, escapes).</summary>
    public static string StripMarkdown(string line) =>
        WhiteSpace().Replace(NoteMarkdown.PlainText(line), " ").Trim();

    /// <summary>
    /// Cleans tags: trims, drops a leading '#', lowercases, keeps letters/digits/-/_, max <see cref="MaxTagLength"/>
    /// characters and <see cref="MaxTags"/> tags, no duplicates. Order is kept.
    /// </summary>
    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags)
    {
        var result = new List<string>();
        foreach (var raw in tags)
        {
            var sb = new StringBuilder();
            foreach (var c in raw.Trim().TrimStart('#').ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_')
                {
                    sb.Append(c);
                }
            }

            var tag = sb.Length > MaxTagLength ? sb.ToString(0, MaxTagLength) : sb.ToString();
            if (tag.Length > 0 && !result.Contains(tag) && result.Count < MaxTags)
            {
                result.Add(tag);
            }
        }

        return result;
    }

    /// <summary>Tags typed as "work, ideas #home" (commas or spaces).</summary>
    public static IReadOnlyList<string> ParseTags(string text) =>
        NormalizeTags(text.Split([',', ' ', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Tags shown for editing: "work, ideas".</summary>
    public static string FormatTags(IEnumerable<string> tags) => string.Join(", ", tags);

    /// <summary>Hashtags written inside text ("buy milk #errands") — used by Quick Note.</summary>
    public static IReadOnlyList<string> Hashtags(string text) =>
        NormalizeTags(Hashtag().Matches(text).Select(m => m.Groups[1].Value));

    /// <summary>"just now", "5 min ago", "3 h ago", "Yesterday", "Mon", "12 Oct", "12 Oct 2025".</summary>
    /// <param name="now">Current time in the user's local offset (day boundaries use it).</param>
    public static string Ago(DateTimeOffset then, DateTimeOffset now)
    {
        var span = now - then;
        if (span < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return $"{(int)span.TotalMinutes} min ago";
        }

        var local = then.ToOffset(now.Offset);
        var days = (now.Date - local.Date).Days;
        if (days == 0)
        {
            return $"{(int)span.TotalHours} h ago";
        }

        if (days == 1)
        {
            return "Yesterday";
        }

        if (days < 7)
        {
            return local.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture);
        }

        return local.ToString(local.Year == now.Year ? "d MMM" : "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static IEnumerable<string> Lines(string content) => content.ReplaceLineEndings("\n").Split('\n');

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"(?<![\w&])#([\p{L}\p{N}_-]+)")]
    private static partial Regex Hashtag();
}
