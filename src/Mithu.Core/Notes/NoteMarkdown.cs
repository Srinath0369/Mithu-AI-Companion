using System.Text;
using System.Text.RegularExpressions;

namespace Mithu.Core.Notes;

public enum NoteBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    Bullet,
    Check,
    Blank,
}

/// <summary>A run of text with its style.</summary>
public readonly record struct NoteSpan(string Text, bool Bold, bool Italic);

/// <summary>One line of a note.</summary>
/// <param name="Line">0-based line in the content.</param>
public sealed record NoteBlock(NoteBlockKind Kind, IReadOnlyList<NoteSpan> Spans, int Line = 0, bool Checked = false)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));
}

/// <summary>
/// The small Markdown subset notes are stored in (the editor shows it formatted):
/// <c># / ## / ###</c> headings, <c>- </c> or <c>* </c> bullets, <c>- [ ] </c> / <c>- [x] </c> checklist items,
/// and <c>**bold**</c>, <c>*italic*</c> or <c>_italic_</c> inside any line.
/// <para>
/// <see cref="Write"/> turns blocks back into text so that <see cref="Parse"/> gives the same blocks: characters that
/// would otherwise read as formatting are escaped with a backslash (<c>\*</c>, <c>\_</c>, <c>\\</c>, and a leading
/// <c>\</c> for a plain line that looks like a heading or list) — but only when needed, so stored text stays readable.
/// </para>
/// Pure and unit-tested.
/// </summary>
public static partial class NoteMarkdown
{
    public static IReadOnlyList<NoteBlock> Parse(string content)
    {
        var blocks = new List<NoteBlock>();
        var lines = NoteText.Lines(content).ToArray();
        for (var i = 0; i < lines.Length; i++)
        {
            blocks.Add(ParseLine(lines[i], i));
        }

        // Trailing blank lines add nothing.
        while (blocks.Count > 0 && blocks[^1].Kind == NoteBlockKind.Blank)
        {
            blocks.RemoveAt(blocks.Count - 1);
        }

        return blocks;
    }

    /// <summary>
    /// Readable text for other apps (the clipboard): no <c>**</c> or <c>#</c>, bullets as "• ", checklist items as
    /// "☐ " / "☑ ".
    /// </summary>
    public static string ToPlainText(string markdown) =>
        string.Join(Environment.NewLine, Parse(markdown).Select(b => b.Kind switch
        {
            NoteBlockKind.Bullet => "• " + b.Text,
            NoteBlockKind.Check => (b.Checked ? "☑ " : "☐ ") + b.Text,
            _ => b.Text,
        }));

    /// <summary>
    /// Text pasted from elsewhere, as note text: "• item" becomes a bullet, "☐ / ☑ item" a checklist item (what
    /// <see cref="ToPlainText"/> writes, and what many apps copy); Markdown such as <c>**bold**</c> or <c>- [ ]</c>
    /// is kept, so it shows formatted.
    /// </summary>
    public static string FromPastedText(string text) =>
        string.Join("\n", NoteText.Lines(text).Select(line =>
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length >= 2 && char.IsWhiteSpace(trimmed[1]))
            {
                var rest = trimmed[2..].TrimStart();
                switch (trimmed[0])
                {
                    case '•' or '◦' or '▪' or '‣' or '●':
                        return "- " + rest;
                    case '☐' or '□':
                        return "- [ ] " + rest;
                    case '☑' or '☒' or '✅':
                        return "- [x] " + rest;
                }
            }

            return line;
        }));

    /// <summary>A line's text without its formatting (for titles, previews and the clipboard view).</summary>
    public static string PlainText(string line) => ParseLine(line, 0).Text;

    /// <summary>Splits a line's text into bold/italic runs (adjacent runs with the same style merged).</summary>
    public static IReadOnlyList<NoteSpan> ParseInline(string text)
    {
        var spans = new List<NoteSpan>();
        AddInline(text, bold: false, spans);
        return Merge(spans);
    }

    /// <summary>Blocks back to stored text, one line per block. <c>Parse(Write(blocks))</c> gives the same blocks.</summary>
    public static string Write(IEnumerable<NoteBlock> blocks)
    {
        var lines = blocks.Select(WriteLine).ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join("\n", lines);
    }

    private static string WriteLine(NoteBlock block)
    {
        var text = WriteInline(block.Spans);
        var line = block.Kind switch
        {
            NoteBlockKind.Blank => string.Empty,
            NoteBlockKind.Heading1 => "# " + text,
            NoteBlockKind.Heading2 => "## " + text,
            NoteBlockKind.Heading3 => "### " + text,
            NoteBlockKind.Bullet => "- " + text,
            NoteBlockKind.Check => (block.Checked ? "- [x] " : "- [ ] ") + text,
            _ => text,
        };

        // A plain line that would read as a heading or list item gets a leading backslash.
        if (block.Kind == NoteBlockKind.Paragraph && ParseLine(line, 0).Kind is not (NoteBlockKind.Paragraph or NoteBlockKind.Blank))
        {
            line = "\\" + line;
        }

        return line;
    }

    /// <summary>Writes runs with as little escaping as possible: plain first, fully escaped if that wouldn't read back the same.</summary>
    private static string WriteInline(IReadOnlyList<NoteSpan> spans)
    {
        var merged = Merge(spans);
        var plain = Compose(merged, escape: false);
        return ParseInline(plain).SequenceEqual(merged) ? plain : Compose(merged, escape: true);
    }

    private static string Compose(IReadOnlyList<NoteSpan> spans, bool escape)
    {
        var sb = new StringBuilder();
        foreach (var span in spans)
        {
            var core = span.Text.Trim();
            var text = escape ? Escape(core) : core;
            if (core.Length == 0 || !(span.Bold || span.Italic))
            {
                sb.Append(escape ? Escape(span.Text) : span.Text);
                continue;
            }

            // Markers hug the text; surrounding spaces stay outside ("a **b** c").
            var lead = span.Text[..(span.Text.Length - span.Text.TrimStart().Length)];
            var trail = span.Text[span.Text.TrimEnd().Length..];
            var styled = span.Bold && span.Italic ? $"**_{text}_**" : span.Bold ? $"**{text}**" : $"*{text}*";
            sb.Append(lead).Append(styled).Append(trail);
        }

        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static NoteBlock ParseLine(string line, int index)
    {
        if (line.Trim().Length == 0)
        {
            return new NoteBlock(NoteBlockKind.Blank, [], index);
        }

        // "\# not a heading": a plain line that was escaped when written.
        if (line.StartsWith('\\') && ParseLine(line[1..], index).Kind is not (NoteBlockKind.Paragraph or NoteBlockKind.Blank))
        {
            return new NoteBlock(NoteBlockKind.Paragraph, ParseInline(line[1..].TrimEnd()), index);
        }

        var heading = HeadingLine().Match(line);
        if (heading.Success)
        {
            var kind = heading.Groups["hashes"].Length switch
            {
                1 => NoteBlockKind.Heading1,
                2 => NoteBlockKind.Heading2,
                _ => NoteBlockKind.Heading3,
            };
            return new NoteBlock(kind, ParseInline(heading.Groups["text"].Value.TrimEnd()), index);
        }

        var check = CheckLine().Match(line);
        if (check.Success)
        {
            return new NoteBlock(NoteBlockKind.Check, ParseInline(check.Groups["text"].Value.TrimEnd()), index,
                Checked: check.Groups["box"].Value is "x" or "X");
        }

        var bullet = BulletLine().Match(line);
        if (bullet.Success)
        {
            return new NoteBlock(NoteBlockKind.Bullet, ParseInline(bullet.Groups["text"].Value.TrimEnd()), index);
        }

        return new NoteBlock(NoteBlockKind.Paragraph, ParseInline(line.TrimEnd()), index);
    }

    private static void AddInline(string text, bool bold, List<NoteSpan> spans)
    {
        var position = 0;
        foreach (Match m in (bold ? ItalicOnly() : BoldOrItalic()).Matches(text))
        {
            if (m.Index > position)
            {
                spans.Add(new NoteSpan(text[position..m.Index], bold, Italic: false));
            }

            if (m.Groups["esc"].Success)
            {
                spans.Add(new NoteSpan(m.Groups["esc"].Value, bold, Italic: false));
            }
            else if (m.Groups["bold"].Success)
            {
                AddInline(m.Groups["bold"].Value, bold: true, spans);
            }
            else
            {
                spans.Add(new NoteSpan(Unescape(m.Groups["italic"].Value), bold, Italic: true));
            }

            position = m.Index + m.Length;
        }

        if (position < text.Length)
        {
            spans.Add(new NoteSpan(text[position..], bold, Italic: false));
        }
    }

    private static string Unescape(string text) => EscapedChar().Replace(text, "$1");

    /// <summary>Joins neighbouring runs with the same style and drops empty ones.</summary>
    private static IReadOnlyList<NoteSpan> Merge(IEnumerable<NoteSpan> spans)
    {
        var merged = new List<NoteSpan>();
        foreach (var span in spans)
        {
            if (span.Text.Length == 0)
            {
                continue;
            }

            if (merged.Count > 0 && merged[^1].Bold == span.Bold && merged[^1].Italic == span.Italic)
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + span.Text };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    [GeneratedRegex(@"^(?<hashes>#{1,6})\s+(?<text>.*)$")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"^\s*[-*]\s+\[(?<box>[ xX])\]\s?(?<text>.*)$")]
    private static partial Regex CheckLine();

    [GeneratedRegex(@"^\s*[-*]\s+(?<text>.*)$")]
    private static partial Regex BulletLine();

    // Escapes first, then **bold**, then *italic* / _italic_ (markers must hug the text).
    [GeneratedRegex(@"\\(?<esc>[\\*_])|\*\*(?<bold>(?:\\.|[^\\])+?)\*\*|(?<![\w*\\])\*(?<italic>[^*\s](?:\\.|[^*\\])*?)\*(?![\w*])|(?<![\w\\])_(?<italic>[^_\s](?:\\.|[^_\\])*?)_(?!\w)")]
    private static partial Regex BoldOrItalic();

    [GeneratedRegex(@"\\(?<esc>[\\*_])|(?<![\w*\\])\*(?<italic>[^*\s](?:\\.|[^*\\])*?)\*(?![\w*])|(?<![\w\\])_(?<italic>[^_\s](?:\\.|[^_\\])*?)_(?!\w)")]
    private static partial Regex ItalicOnly();

    [GeneratedRegex(@"\\([\\*_])")]
    private static partial Regex EscapedChar();
}
