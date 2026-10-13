using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Mithu.Core.Notes;

namespace Mithu.App.Controls;

/// <summary>
/// Converts between a note's stored text (<see cref="NoteMarkdown"/>) and the live editor's <see cref="FlowDocument"/>.
/// One stored line = one paragraph: headings are paragraphs with a larger font, bullets are <see cref="List"/> items,
/// checklist items are paragraphs that start with a <see cref="CheckBox"/>, blank lines are empty paragraphs.
/// Everything is read back from what is on screen (font size, weight, style, the checkbox), so undo/redo — which
/// rebuilds elements — can never lose formatting.
/// </summary>
public static class NoteDocument
{
    public const double BodySize = 13;
    public const double Heading1Size = 19;
    public const double Heading2Size = 16;
    public const double Heading3Size = 14.5;

    /// <summary>
    /// Every line has the same height (text, bullets, checklist rows alike), so a note reads evenly; headings get
    /// their own line height and a little room above.
    /// </summary>
    public const double BodyLineHeight = 22;

    private static readonly Thickness BodyMargin = new(0);
    private static readonly Thickness HeadingMargin = new(0, 8, 0, 2);
    private static readonly Thickness CheckBoxMargin = new(0, 0, 7, 0);

    /// <summary>Where a checklist item's words start (checkbox 15 + gap 7): bullet text starts there too.</summary>
    private const double TextIndent = 22;

    /// <summary>A formatted document for the stored text (always at least one paragraph to type in).</summary>
    public static FlowDocument Create(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = Themes.AppFonts.Ui,
            FontSize = BodySize,
            Foreground = Brush("TextBrush", Brushes.White),
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            LineHeight = BodyLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };

        // Any paragraph that arrives without Mithu's spacing (WPF's default is a large gap above and below) gets it.
        var paragraphs = new Style(typeof(Paragraph));
        paragraphs.Setters.Add(new Setter(Block.MarginProperty, BodyMargin));
        document.Resources[typeof(Paragraph)] = paragraphs;

        List? list = null;
        foreach (var block in NoteMarkdown.Parse(markdown))
        {
            if (block.Kind == NoteBlockKind.Bullet)
            {
                if (list is null)
                {
                    list = CreateList();
                    document.Blocks.Add(list);
                }

                list.ListItems.Add(new ListItem(NewParagraph(block)));
                continue;
            }

            list = null;
            document.Blocks.Add(NewParagraph(block));
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(NewParagraph(new NoteBlock(NoteBlockKind.Paragraph, [])));
        }

        return document;
    }

    /// <summary>The stored text for what the document shows.</summary>
    public static string ToMarkdown(FlowDocument document) => NoteMarkdown.Write(ReadBlocks(document.Blocks));

    /// <summary>What kind of line a paragraph is, judged from how it looks.</summary>
    public static NoteBlockKind KindOf(Paragraph paragraph)
    {
        if (CheckBoxOf(paragraph) is not null)
        {
            return NoteBlockKind.Check;
        }

        if (paragraph.Parent is ListItem)
        {
            return NoteBlockKind.Bullet;
        }

        var size = paragraph.FontSize;
        return size >= Heading1Size - 0.5 ? NoteBlockKind.Heading1
            : size >= Heading2Size - 0.5 ? NoteBlockKind.Heading2
            : size >= Heading3Size - 0.25 ? NoteBlockKind.Heading3
            : NoteBlockKind.Paragraph;
    }

    public static bool IsHeading(NoteBlockKind kind) => kind is NoteBlockKind.Heading1 or NoteBlockKind.Heading2 or NoteBlockKind.Heading3;

    /// <summary>Makes a (non-list, non-checklist) paragraph a heading or plain text.</summary>
    public static void SetHeading(Paragraph paragraph, NoteBlockKind kind)
    {
        if (IsHeading(kind))
        {
            paragraph.FontSize = kind switch
            {
                NoteBlockKind.Heading1 => Heading1Size,
                NoteBlockKind.Heading2 => Heading2Size,
                _ => Heading3Size,
            };
            paragraph.FontWeight = FontWeights.SemiBold;
            paragraph.Margin = HeadingMargin;
            paragraph.LineHeight = Math.Round(paragraph.FontSize * 1.35);
        }
        else
        {
            paragraph.ClearValue(TextElement.FontSizeProperty);
            paragraph.ClearValue(TextElement.FontWeightProperty);
            paragraph.ClearValue(Block.LineHeightProperty);
            paragraph.Margin = BodyMargin;
        }
    }

    /// <summary>The checkbox at the start of a checklist paragraph, or null.</summary>
    public static CheckBox? CheckBoxOf(Paragraph paragraph) =>
        paragraph.Inlines.FirstInline is InlineUIContainer { Child: CheckBox box } ? box : null;

    /// <summary>Turns a paragraph into a checklist item (checkbox first) — or back into plain text.</summary>
    public static void SetCheck(Paragraph paragraph, bool isCheck, bool isChecked = false)
    {
        var existing = paragraph.Inlines.FirstInline as InlineUIContainer;
        if (isCheck && existing?.Child is not CheckBox)
        {
            SetHeading(paragraph, NoteBlockKind.Paragraph);
            var box = new InlineUIContainer(NewCheckBox(isChecked)) { BaselineAlignment = BaselineAlignment.Center };
            if (paragraph.Inlines.FirstInline is { } first)
            {
                paragraph.Inlines.InsertBefore(first, box);
            }
            else
            {
                paragraph.Inlines.Add(box);
            }
        }
        else if (!isCheck && existing?.Child is CheckBox)
        {
            paragraph.Inlines.Remove(existing);
            isChecked = false;
        }

        ApplyCheckedLook(paragraph, isCheck && isChecked);
    }

    /// <summary>
    /// Ticked items are dimmed and their words struck through (on the text runs, so the line doesn't run under
    /// the gap after the checkbox).
    /// </summary>
    public static void ApplyCheckedLook(Paragraph paragraph, bool isChecked)
    {
        paragraph.ClearValue(Paragraph.TextDecorationsProperty);
        if (isChecked)
        {
            paragraph.Foreground = Brush("MutedBrush", Brushes.Gray);
        }
        else
        {
            paragraph.ClearValue(TextElement.ForegroundProperty);
        }

        foreach (var run in Runs(paragraph.Inlines))
        {
            if (isChecked)
            {
                run.TextDecorations = TextDecorations.Strikethrough;
            }
            else
            {
                run.ClearValue(Inline.TextDecorationsProperty);
            }
        }
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run)
            {
                yield return run;
            }
            else if (inline is Span span)
            {
                foreach (var inner in Runs(span.Inlines))
                {
                    yield return inner;
                }
            }
        }
    }

    /// <summary>
    /// A paragraph as a stored line, optionally only the part between <paramref name="from"/> and
    /// <paramref name="to"/> (null = from the start / to the end): what copying a selection takes.
    /// </summary>
    public static NoteBlock BlockOf(Paragraph paragraph, TextPointer? from = null, TextPointer? to = null)
    {
        var block = ReadParagraph(paragraph);
        if (from is null && to is null)
        {
            return block;
        }

        var spans = SpansBetween(paragraph.Inlines, from, to).ToList();
        var kind = block.Kind == NoteBlockKind.Blank && spans.Count > 0 ? NoteBlockKind.Paragraph : block.Kind;
        return new NoteBlock(kind, spans, Checked: block.Checked);
    }

    private static IEnumerable<NoteSpan> SpansBetween(InlineCollection inlines, TextPointer? from, TextPointer? to)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    var start = run.ContentStart;
                    var end = run.ContentEnd;
                    var a = from is null || from.CompareTo(start) <= 0 ? 0
                        : from.CompareTo(end) >= 0 ? run.Text.Length
                        : start.GetOffsetToPosition(from);
                    var b = to is null || to.CompareTo(end) >= 0 ? run.Text.Length
                        : to.CompareTo(start) <= 0 ? 0
                        : start.GetOffsetToPosition(to);
                    if (b > a)
                    {
                        yield return new NoteSpan(
                            run.Text[a..b].ReplaceLineEndings(" "),
                            Bold: run.FontWeight.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight(),
                            Italic: run.FontStyle == FontStyles.Italic);
                    }

                    break;

                case Span span:
                    foreach (var inner in SpansBetween(span.Inlines, from, to))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    /// <summary>Text the user typed in a paragraph (the checkbox doesn't count).</summary>
    public static string TextOf(Paragraph paragraph) =>
        string.Concat(Spans(paragraph.Inlines).Select(s => s.Text));

    private static Paragraph NewParagraph(NoteBlock block)
    {
        var paragraph = new Paragraph { Margin = BodyMargin };
        foreach (var span in block.Spans)
        {
            var run = new Run(span.Text);
            if (span.Bold)
            {
                run.FontWeight = FontWeights.Bold;
            }

            if (span.Italic)
            {
                run.FontStyle = FontStyles.Italic;
            }

            paragraph.Inlines.Add(run);
        }

        if (IsHeading(block.Kind))
        {
            SetHeading(paragraph, block.Kind);
        }
        else if (block.Kind == NoteBlockKind.Check)
        {
            SetCheck(paragraph, isCheck: true, block.Checked);
        }

        return paragraph;
    }

    /// <summary>An empty bullet list styled like the others.</summary>
    public static List CreateList() => new()
    {
        MarkerStyle = TextMarkerStyle.Disc,
        Margin = new Thickness(0),
        Padding = new Thickness(TextIndent, 0, 0, 0),
    };

    private static CheckBox NewCheckBox(bool isChecked)
    {
        var box = new CheckBox { IsChecked = isChecked, Margin = CheckBoxMargin, Focusable = false, Cursor = System.Windows.Input.Cursors.Hand };
        if (Application.Current?.TryFindResource("IslandCheckBox") is Style style)
        {
            box.Style = style;
        }

        System.Windows.Automation.AutomationProperties.SetName(box, "Checklist item");
        return box;
    }

    private static IEnumerable<NoteBlock> ReadBlocks(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return ReadParagraph(paragraph);
                    break;

                case List list:
                    foreach (var item in list.ListItems)
                    {
                        foreach (var inner in ReadBlocks(item.Blocks))
                        {
                            // A paragraph inside a list item is a bullet (unless it is a checklist item).
                            yield return inner.Kind is NoteBlockKind.Check or NoteBlockKind.Bullet
                                ? inner
                                : inner with { Kind = NoteBlockKind.Bullet };
                        }
                    }

                    break;

                case Section section:
                    foreach (var inner in ReadBlocks(section.Blocks))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    private static NoteBlock ReadParagraph(Paragraph paragraph)
    {
        var kind = KindOf(paragraph);
        var spans = Spans(paragraph.Inlines).ToList();
        if (kind == NoteBlockKind.Paragraph && spans.All(s => s.Text.Trim().Length == 0))
        {
            return new NoteBlock(NoteBlockKind.Blank, []);
        }

        return new NoteBlock(kind, spans, Checked: CheckBoxOf(paragraph)?.IsChecked == true);
    }

    /// <summary>Runs with their effective (inherited) bold/italic. Heading weight (semibold) doesn't count as bold.</summary>
    private static IEnumerable<NoteSpan> Spans(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    yield return new NoteSpan(
                        run.Text.ReplaceLineEndings(" "),
                        Bold: run.FontWeight.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight(),
                        Italic: run.FontStyle == FontStyles.Italic);
                    break;

                case LineBreak:
                    yield return new NoteSpan(" ", false, false);
                    break;

                case Span span:
                    foreach (var inner in Spans(span.Inlines))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    private static Brush Brush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
