using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Mithu.App.Controls;

/// <summary>
/// Shows an AI reply's light Markdown in a <see cref="TextBlock"/>: headings (bold), bullet and numbered lists,
/// **bold**, *italic*, `code`, ``` code blocks ```, and [links](url) as their text. Anything else stays as written.
/// Set <c>ChatMarkdown.Text</c> instead of <c>Text</c>.
/// </summary>
public static partial class ChatMarkdown
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(ChatMarkdown), new PropertyMetadata(null, OnTextChanged));

    /// <summary>The reply is still being written: show a cursor and hide a half-typed "**" or "`" until it closes.</summary>
    public static readonly DependencyProperty CaretProperty = DependencyProperty.RegisterAttached(
        "Caret", typeof(bool), typeof(ChatMarkdown), new PropertyMetadata(false, OnTextChanged));

    public static bool GetCaret(DependencyObject element) => (bool)element.GetValue(CaretProperty);

    public static void SetCaret(DependencyObject element, bool value) => element.SetValue(CaretProperty, value);

    private static readonly Brush CaretBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2D, 0xF3, 0xF3)));

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Brush CodeBackground = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    [GeneratedRegex(@"\*\*(.+?)\*\*|__(.+?)__|(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])|(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?![\w_])|`([^`]+)`|\[([^\]]+)\]\((?:[^)]+)\)")]
    private static partial Regex InlinePattern();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^(\s*)(\d+)[.)]\s+(.*)$")]
    private static partial Regex NumberPattern();

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        var caret = GetCaret(block);
        var text = GetText(block) ?? string.Empty;
        block.Inlines.Clear();
        foreach (var inline in Render(caret ? HideOpenMarkers(text) : text))
        {
            block.Inlines.Add(inline);
        }

        if (caret)
        {
            block.Inlines.Add(new Run(" ▍") { Foreground = CaretBrush, FontSize = 12 });
        }
    }

    /// <summary>While typing, a "**" or "`" whose partner hasn't arrived yet is left out (no flicker of symbols).</summary>
    internal static string HideOpenMarkers(string text)
    {
        if (CountOf(text, "```") % 2 == 1)
        {
            return text; // inside a code block: shown as typed
        }

        if (CountOf(text, "**") % 2 == 1)
        {
            var at = text.LastIndexOf("**", StringComparison.Ordinal);
            text = text.Remove(at, 2);
        }

        if (text.Count(c => c == '`') % 2 == 1)
        {
            var at = text.LastIndexOf('`');
            text = text.Remove(at, 1);
        }

        return text;
    }

    private static int CountOf(string text, string marker)
    {
        var count = 0;
        for (var i = text.IndexOf(marker, StringComparison.Ordinal); i >= 0; i = text.IndexOf(marker, i + marker.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The reply as inlines (public for tests).</summary>
    public static IReadOnlyList<Inline> Render(string markdown)
    {
        var result = new List<Inline>();
        var lines = markdown.ReplaceLineEndings("\n").Trim('\n').Split('\n');
        var inCode = false;
        var pendingBreaks = 0;

        void Break(int count)
        {
            if (result.Count > 0)
            {
                pendingBreaks = Math.Max(pendingBreaks, count);
            }
        }

        void Emit(IEnumerable<Inline> inlines)
        {
            for (var i = 0; i < pendingBreaks; i++)
            {
                result.Add(new LineBreak());
            }

            pendingBreaks = 0;
            result.AddRange(inlines);
        }

        foreach (var raw in lines)
        {
            if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                Break(1);
                Emit([new Run(raw.Length == 0 ? " " : raw) { FontFamily = Mono, FontSize = 12, Background = CodeBackground }]);
                continue;
            }

            if (raw.Trim().Length == 0)
            {
                Break(2); // a blank line: paragraph gap
                continue;
            }

            Break(1);
            if (HeadingPattern().Match(raw) is { Success: true } heading)
            {
                var bold = new Bold();
                bold.Inlines.AddRange(Inlines(heading.Groups[2].Value));
                Emit([bold]);
            }
            else if (BulletPattern().Match(raw) is { Success: true } bullet)
            {
                var indent = new string(' ', Math.Min(bullet.Groups[1].Value.Length, 6));
                Emit([new Run($"{indent}•  "), .. Inlines(bullet.Groups[2].Value)]);
            }
            else if (NumberPattern().Match(raw) is { Success: true } number)
            {
                var indent = new string(' ', Math.Min(number.Groups[1].Value.Length, 6));
                Emit([new Run($"{indent}{number.Groups[2].Value}.  "), .. Inlines(number.Groups[3].Value)]);
            }
            else
            {
                Emit(Inlines(raw));
            }
        }

        return result;
    }

    private static IEnumerable<Inline> Inlines(string text)
    {
        var last = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > last)
            {
                foreach (var inline in EmojiText.Inlines(text[last..match.Index]))
                {
                    yield return inline;
                }
            }

            if (match.Groups[1].Success || match.Groups[2].Success)
            {
                // Bold can hold code or emoji (**`Program.cs`**): its inside is formatted too.
                var bold = new Bold();
                bold.Inlines.AddRange(Inlines(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value));
                yield return bold;
            }
            else if (match.Groups[3].Success || match.Groups[4].Success)
            {
                var italic = new Italic();
                italic.Inlines.AddRange(Inlines(match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value));
                yield return italic;
            }
            else if (match.Groups[5].Success)
            {
                yield return new Run(match.Groups[5].Value) { FontFamily = Mono, FontSize = 12, Background = CodeBackground };
            }
            else
            {
                yield return new Run(match.Groups[6].Value) { TextDecorations = TextDecorations.Underline };
            }

            last = match.Index + match.Length;
        }

        if (last < text.Length)
        {
            foreach (var inline in EmojiText.Inlines(text[last..]))
            {
                yield return inline;
            }
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
