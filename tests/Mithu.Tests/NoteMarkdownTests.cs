using Mithu.Core.Notes;

namespace Mithu.Tests;

public sealed class NoteMarkdownTests
{
    private static NoteSpan P(string text) => new(text, Bold: false, Italic: false);

    private static NoteSpan B(string text) => new(text, Bold: true, Italic: false);

    private static NoteSpan I(string text) => new(text, Bold: false, Italic: true);

    private static NoteSpan BI(string text) => new(text, Bold: true, Italic: true);

    private static NoteBlock Block(NoteBlockKind kind, params NoteSpan[] spans) => new(kind, spans);

    /// <summary>Writes the blocks, parses them back, and checks nothing changed (kind, check state, every run).</summary>
    private static string AssertRoundTrip(params NoteBlock[] blocks)
    {
        var text = NoteMarkdown.Write(blocks);
        var back = NoteMarkdown.Parse(text);

        Assert.Equal(blocks.Length, back.Count);
        for (var i = 0; i < blocks.Length; i++)
        {
            Assert.Equal(blocks[i].Kind, back[i].Kind);
            Assert.Equal(blocks[i].Checked, back[i].Checked);
            Assert.Equal(blocks[i].Spans, back[i].Spans);
        }

        return text;
    }

    [Fact]
    public void Parses_each_line_kind()
    {
        var blocks = NoteMarkdown.Parse("# Title\n## Sub\n### Small\n- item\n* star item\n- [ ] todo\n- [x] done\n\nplain");

        Assert.Equal(
            [NoteBlockKind.Heading1, NoteBlockKind.Heading2, NoteBlockKind.Heading3, NoteBlockKind.Bullet, NoteBlockKind.Bullet,
             NoteBlockKind.Check, NoteBlockKind.Check, NoteBlockKind.Blank, NoteBlockKind.Paragraph],
            blocks.Select(b => b.Kind));
        Assert.False(blocks[5].Checked);
        Assert.True(blocks[6].Checked);
        Assert.Equal("todo", blocks[5].Text);
        Assert.Equal(8, blocks[8].Line);
    }

    [Fact]
    public void Trailing_blank_lines_are_dropped()
    {
        Assert.Single(NoteMarkdown.Parse("text\n\n\n"));
    }

    [Fact]
    public void Inline_bold_and_italic()
    {
        Assert.Equal([P("a "), B("bold"), P(" b "), I("it"), P(" c "), I("it2")], NoteMarkdown.ParseInline("a **bold** b *it* c _it2_"));
    }

    [Fact]
    public void Italic_inside_bold()
    {
        Assert.Equal([B("very "), BI("much"), B(" so")], NoteMarkdown.ParseInline("**very *much* so**"));
    }

    [Theory]
    [InlineData("2 * 3 * 4")]
    [InlineData("snake_case_name")]
    [InlineData("**unclosed")]
    public void Lone_markers_stay_plain(string text)
    {
        Assert.Equal([P(text)], NoteMarkdown.ParseInline(text));
    }

    [Fact]
    public void Escaped_markers_are_plain_characters()
    {
        Assert.Equal([P("*not italic* and \\ and _x_")], NoteMarkdown.ParseInline(@"\*not italic\* and \\ and \_x\_"));
    }

    [Fact]
    public void Escaped_line_start_is_a_plain_line()
    {
        var block = Assert.Single(NoteMarkdown.Parse(@"\# not a heading"));

        Assert.Equal(NoteBlockKind.Paragraph, block.Kind);
        Assert.Equal("# not a heading", block.Text);
    }

    [Fact]
    public void Plain_text_of_a_line()
    {
        Assert.Equal("done thing", NoteMarkdown.PlainText("- [x] _done_ thing"));
        Assert.Equal("# hash", NoteMarkdown.PlainText(@"\# hash"));
    }

    // ===== Writing (editor → stored text) =====================================

    [Fact]
    public void Writes_every_block_kind()
    {
        var text = AssertRoundTrip(
            Block(NoteBlockKind.Heading1, P("Goals")),
            Block(NoteBlockKind.Heading2, P("Sub")),
            Block(NoteBlockKind.Heading3, P("Small")),
            Block(NoteBlockKind.Bullet, P("item")),
            new NoteBlock(NoteBlockKind.Check, [P("todo")]),
            new NoteBlock(NoteBlockKind.Check, [P("done")], Checked: true),
            Block(NoteBlockKind.Blank),
            Block(NoteBlockKind.Paragraph, P("plain")));

        Assert.Equal("# Goals\n## Sub\n### Small\n- item\n- [ ] todo\n- [x] done\n\nplain", text);
    }

    [Fact]
    public void Writes_styles_readably()
    {
        var text = AssertRoundTrip(Block(NoteBlockKind.Paragraph, P("a "), B("bold"), P(" b "), I("it"), P(" c "), BI("both")));

        Assert.Equal("a **bold** b *it* c **_both_**", text);
    }

    [Fact]
    public void Spaces_stay_outside_the_markers()
    {
        var text = NoteMarkdown.Write([Block(NoteBlockKind.Paragraph, P("a"), B(" bold "), P("b"))]);

        // Bold spaces look the same as plain ones, so they move outside the markers.
        Assert.Equal("a **bold** b", text);
        Assert.Equal([P("a "), B("bold"), P(" b")], NoteMarkdown.Parse(text)[0].Spans);
    }

    [Fact]
    public void Ordinary_symbols_are_not_escaped()
    {
        var text = AssertRoundTrip(Block(NoteBlockKind.Paragraph, P("2 * 3 * 4 = snake_case")));

        Assert.Equal("2 * 3 * 4 = snake_case", text);
    }

    [Fact]
    public void Text_that_would_read_as_formatting_is_escaped()
    {
        var text = AssertRoundTrip(Block(NoteBlockKind.Paragraph, P("*stars* and _under_ and C:\\path")));

        Assert.Contains(@"\*", text);
    }

    [Theory]
    [InlineData("# not a heading")]
    [InlineData("- not a bullet")]
    [InlineData("- [ ] not a task")]
    [InlineData("  * indented star")]
    public void Plain_lines_that_look_like_lists_are_escaped(string line)
    {
        AssertRoundTrip(Block(NoteBlockKind.Paragraph, P(line)));
    }

    [Fact]
    public void Styled_text_with_markers_inside_round_trips()
    {
        AssertRoundTrip(Block(NoteBlockKind.Bullet, B("a*b"), P(" "), I("c_d")));
    }

    [Fact]
    public void Empty_items_round_trip()
    {
        AssertRoundTrip(Block(NoteBlockKind.Bullet), new NoteBlock(NoteBlockKind.Check, []), Block(NoteBlockKind.Heading1));
    }

    [Fact]
    public void Trailing_blank_lines_are_not_written()
    {
        Assert.Equal("text", NoteMarkdown.Write([Block(NoteBlockKind.Paragraph, P("text")), Block(NoteBlockKind.Blank), Block(NoteBlockKind.Blank)]));
    }

    [Fact]
    public void Existing_notes_read_back_the_same_after_writing()
    {
        const string stored = "# Goals\n- [ ] Finish **report**\n- [x] Email *team*\n- Review notes\n\nPlain line with _style_.";

        var once = NoteMarkdown.Write(NoteMarkdown.Parse(stored));

        Assert.Equal(NoteMarkdown.Parse(stored).Select(b => (b.Kind, b.Checked, b.Text)), NoteMarkdown.Parse(once).Select(b => (b.Kind, b.Checked, b.Text)));
        Assert.Equal(once, NoteMarkdown.Write(NoteMarkdown.Parse(once))); // stable
    }
}
