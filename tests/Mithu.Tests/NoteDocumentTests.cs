using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Documents;
using Mithu.App.Controls;
using Mithu.Core.Notes;

namespace Mithu.Tests;

/// <summary>
/// The live editor's document ↔ stored text. These conversions decide what is saved, so a note must come back
/// exactly as written, whatever the user typed.
/// </summary>
public sealed class NoteDocumentTests
{
    /// <summary>WPF documents need an STA thread.</summary>
    private static void Sta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static string RoundTrip(string markdown) => NoteDocument.ToMarkdown(NoteDocument.Create(markdown));

    [Theory]
    [InlineData("# Goals\n- [ ] Finish **report**\n- [x] Email *team*\n- Review notes\n\nPlain line with *style*.")]
    [InlineData("## Sub\n### Small\nplain")]
    [InlineData("a **b** *c* **_d_** e")]
    [InlineData("- one\n- two\n\n- three")]
    [InlineData("\\# not a heading\n2 * 3 = 6 and snake_case")]
    [InlineData("- [ ] \n- \n# ")]
    public void Stored_text_survives_the_editor_unchanged(string markdown)
    {
        Sta(() => Assert.Equal(markdown, RoundTrip(markdown)));
    }

    [Fact]
    public void Old_style_text_is_tidied_but_keeps_its_meaning()
    {
        Sta(() =>
        {
            var tidied = RoundTrip("* star bullet\n- [X] done\nsome _italic_ text\n\n\n");

            Assert.Equal("- star bullet\n- [x] done\nsome *italic* text", tidied);
        });
    }

    [Fact]
    public void Each_line_becomes_the_right_kind_of_block()
    {
        Sta(() =>
        {
            var doc = NoteDocument.Create("# H\n- b1\n- b2\n- [x] c\n\np");
            var blocks = doc.Blocks.ToList();

            Assert.IsType<Paragraph>(blocks[0]);
            Assert.Equal(NoteBlockKind.Heading1, NoteDocument.KindOf((Paragraph)blocks[0]));
            Assert.Equal(2, Assert.IsType<List>(blocks[1]).ListItems.Count);
            Assert.True(NoteDocument.CheckBoxOf((Paragraph)blocks[2])!.IsChecked);
            Assert.Equal(TextDecorations.Strikethrough, ((Paragraph)blocks[2]).Inlines.OfType<Run>().Single().TextDecorations);
            Assert.Equal(string.Empty, NoteDocument.TextOf((Paragraph)blocks[3]));
            Assert.Equal("p", NoteDocument.TextOf((Paragraph)blocks[4]));
        });
    }

    [Fact]
    public void Typed_formatting_is_saved()
    {
        Sta(() =>
        {
            var doc = NoteDocument.Create("x");
            var paragraph = (Paragraph)doc.Blocks.FirstBlock;
            paragraph.Inlines.Clear();
            paragraph.Inlines.Add(new Run("plain "));
            paragraph.Inlines.Add(new Run("bold") { FontWeight = FontWeights.Bold });
            paragraph.Inlines.Add(new Run(" "));
            paragraph.Inlines.Add(new Italic(new Run("slanted")));

            Assert.Equal("plain **bold** *slanted*", NoteDocument.ToMarkdown(doc));
        });
    }

    [Fact]
    public void Literal_markdown_typed_as_text_stays_text()
    {
        Sta(() =>
        {
            var doc = NoteDocument.Create("x");
            ((Paragraph)doc.Blocks.FirstBlock).Inlines.Clear();
            ((Paragraph)doc.Blocks.FirstBlock).Inlines.Add(new Run("# *not* a heading"));

            var saved = NoteDocument.ToMarkdown(doc);
            var back = NoteDocument.Create(saved);

            Assert.Equal(NoteBlockKind.Paragraph, NoteDocument.KindOf((Paragraph)back.Blocks.FirstBlock));
            Assert.Equal("# *not* a heading", NoteDocument.TextOf((Paragraph)back.Blocks.FirstBlock));
        });
    }

    [Fact]
    public void Heading_weight_is_not_saved_as_bold()
    {
        Sta(() => Assert.Equal("# Title", RoundTrip("# Title")));
    }

    [Fact]
    public void Ticking_and_unticking_a_checkbox_is_saved()
    {
        Sta(() =>
        {
            var doc = NoteDocument.Create("- [ ] task");
            var paragraph = (Paragraph)doc.Blocks.FirstBlock;

            NoteDocument.CheckBoxOf(paragraph)!.IsChecked = true;
            Assert.Equal("- [x] task", NoteDocument.ToMarkdown(doc));

            NoteDocument.SetCheck(paragraph, isCheck: false);
            Assert.Equal("task", NoteDocument.ToMarkdown(doc));
        });
    }

    [Fact]
    public void Plain_line_can_become_a_heading_or_checklist_item()
    {
        Sta(() =>
        {
            var doc = NoteDocument.Create("one\ntwo");
            var first = (Paragraph)doc.Blocks.FirstBlock;
            var second = (Paragraph)doc.Blocks.LastBlock;

            NoteDocument.SetHeading(first, NoteBlockKind.Heading1);
            NoteDocument.SetCheck(second, isCheck: true);

            Assert.Equal("# one\n- [ ] two", NoteDocument.ToMarkdown(doc));
        });
    }

    [Fact]
    public void Empty_note_gives_one_paragraph_to_type_in_and_saves_as_empty()
    {
        Sta(() =>
        {
            var doc = NoteDocument.Create(string.Empty);

            Assert.Single(doc.Blocks);
            Assert.Equal(string.Empty, NoteDocument.ToMarkdown(doc));
        });
    }
}
