using System.Runtime.ExceptionServices;
using System.Windows.Documents;
using System.Windows.Threading;
using Mithu.App.Controls;
using Mithu.Core.Notes;

namespace Mithu.Tests;

/// <summary>The live editor's typing behaviour (run on an STA thread with a dispatcher).</summary>
public sealed class RichNoteEditorTests
{
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

    /// <summary>Lets queued work (like the shortcut formatting) run.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Types text at the caret the way keyboard input does (one change per character).</summary>
    private static void Type(RichNoteEditor editor, string text)
    {
        foreach (var c in text)
        {
            editor.CaretPosition.InsertTextInRun(c.ToString());
            editor.CaretPosition = editor.CaretPosition.GetPositionAtOffset(1, LogicalDirection.Forward) ?? editor.CaretPosition;
            Pump();
        }
    }

    [Theory]
    [InlineData("# ", "# Plan")]
    [InlineData("- ", "- Plan")]
    [InlineData("* ", "- Plan")]
    [InlineData("[] ", "- [ ] Plan")]
    public void Shortcut_at_line_start_formats_the_line(string shortcut, string expected)
    {
        Sta(() =>
        {
            var editor = new RichNoteEditor();
            editor.CaretPosition = ((Paragraph)editor.Document.Blocks.FirstBlock).ContentStart;

            Type(editor, shortcut);
            Type(editor, "Plan");

            Assert.Equal(expected, editor.Markdown);
        });
    }

    /// <summary>Fast typing: the rest of the line arrives before the shortcut is applied.</summary>
    [Theory]
    [InlineData("# Plan", "# Plan")]
    [InlineData("- milk", "- milk")]
    [InlineData("[] call mom", "- [ ] call mom")]
    public void Shortcut_still_applies_when_typing_runs_ahead(string typed, string expected)
    {
        Sta(() =>
        {
            var editor = new RichNoteEditor();
            editor.CaretPosition = ((Paragraph)editor.Document.Blocks.FirstBlock).ContentStart;

            foreach (var c in typed)
            {
                editor.CaretPosition.InsertTextInRun(c.ToString());
                editor.CaretPosition = editor.CaretPosition.GetPositionAtOffset(1, LogicalDirection.Forward) ?? editor.CaretPosition;
            }

            Pump();

            Assert.Equal(expected, editor.Markdown);
        });
    }

    [Fact]
    public void Bullet_shortcut_applies_to_its_own_line_even_after_the_caret_moved_on()
    {
        Sta(() =>
        {
            var editor = NewEditor("x\ny");
            CaretToLineStart(editor, 0);
            foreach (var c in "- ")
            {
                editor.CaretPosition.InsertTextInRun(c.ToString());
                editor.CaretPosition = editor.CaretPosition.GetPositionAtOffset(1, LogicalDirection.Forward) ?? editor.CaretPosition;
            }

            CaretToLineStart(editor, 1); // caret moves to the next line before the shortcut runs
            Pump();

            Assert.Equal("- x\ny", editor.Markdown);
        });
    }

    [Fact]
    public void Shortcut_characters_mid_line_stay_text()
    {
        Sta(() =>
        {
            var editor = new RichNoteEditor();
            editor.CaretPosition = ((Paragraph)editor.Document.Blocks.FirstBlock).ContentStart;

            Type(editor, "a - b");

            Assert.Equal("a - b", editor.Markdown);
        });
    }

    /// <summary>An editor hosted in an off-screen window (editing commands need a live, focused control).</summary>
    private static RichNoteEditor NewEditor(string markdown = "")
    {
        var editor = new RichNoteEditor { Markdown = markdown, Width = 300, Height = 200 };
        var window = new System.Windows.Window
        {
            Content = editor,
            Left = -10000,
            Top = -10000,
            Width = 320,
            Height = 240,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = System.Windows.WindowStyle.None,
        };
        window.Show();
        editor.Focus();
        editor.CaretPosition = editor.Document.ContentEnd;
        Pump();
        return editor;
    }

    /// <summary>Puts the caret at the start of the <paramref name="index"/>th line (lists included).</summary>
    private static void CaretToLineStart(RichNoteEditor editor, int index)
    {
        var lines = new List<Paragraph>();
        foreach (var block in editor.Document.Blocks)
        {
            if (block is Paragraph p)
            {
                lines.Add(p);
            }
            else if (block is List list)
            {
                lines.AddRange(list.ListItems.Select(i => (Paragraph)i.Blocks.FirstBlock));
            }
        }

        var line = lines[index];
        editor.CaretPosition = NoteDocument.CheckBoxOf(line) is not null
            ? ((InlineUIContainer)line.Inlines.FirstInline).ElementEnd
            : line.ContentStart;
    }

    [Fact]
    public void Enter_continues_bullets_and_twice_ends_the_list()
    {
        Sta(() =>
        {
            var editor = NewEditor();
            Type(editor, "- milk");
            editor.PressEnter();
            Type(editor, "bread");
            editor.PressEnter();
            editor.PressEnter();
            Type(editor, "after");

            Assert.Equal("- milk\n- bread\nafter", editor.Markdown);
        });
    }

    [Fact]
    public void Enter_right_after_a_typed_shortcut_still_continues_the_list()
    {
        Sta(() =>
        {
            var editor = NewEditor();
            foreach (var c in "- milk")
            {
                editor.CaretPosition.InsertTextInRun(c.ToString());
                editor.CaretPosition = editor.CaretPosition.GetPositionAtOffset(1, LogicalDirection.Forward) ?? editor.CaretPosition;
            }

            editor.PressEnter(); // before the deferred shortcut had a chance to run
            Type(editor, "bread");

            Assert.Equal("- milk\n- bread", editor.Markdown);
        });
    }

    [Fact]
    public void Enter_continues_checklists_unticked_and_twice_ends_them()
    {
        Sta(() =>
        {
            var editor = NewEditor("- [x] done");
            editor.PressEnter();
            Type(editor, "next");
            editor.PressEnter();
            editor.PressEnter();
            Type(editor, "after");

            Assert.Equal("- [x] done\n- [ ] next\nafter", editor.Markdown);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Checklist_made_with_the_shortcut_continues_on_enter(bool pauseWhileTyping)
    {
        Sta(() =>
        {
            var editor = NewEditor();
            foreach (var c in "[] call mom")
            {
                editor.CaretPosition.InsertTextInRun(c.ToString());
                editor.CaretPosition = editor.CaretPosition.GetPositionAtOffset(1, LogicalDirection.Forward) ?? editor.CaretPosition;
                if (pauseWhileTyping)
                {
                    Pump();
                }
            }

            editor.PressEnter();
            Type(editor, "book table");

            Assert.Equal("- [ ] call mom\n- [ ] book table", editor.Markdown);
        });
    }

    [Fact]
    public void Checklist_right_after_ending_a_bullet_list_continues_on_enter()
    {
        Sta(() =>
        {
            var editor = NewEditor();
            Type(editor, "- milk");
            editor.PressEnter();
            Type(editor, "bread");
            editor.PressEnter();
            editor.PressEnter();
            Type(editor, "[] call mom");
            editor.PressEnter();
            Type(editor, "book table");

            Assert.Equal("- milk\n- bread\n- [ ] call mom\n- [ ] book table", editor.Markdown);
        });
    }

    [Fact]
    public void Enter_after_a_heading_starts_plain_text()
    {
        Sta(() =>
        {
            var editor = NewEditor("# Title");
            editor.PressEnter();
            Type(editor, "body");

            Assert.Equal("# Title\nbody", editor.Markdown);
        });
    }

    [Fact]
    public void Backspace_at_the_start_of_a_middle_bullet_splits_the_list()
    {
        Sta(() =>
        {
            var editor = NewEditor("- a\n- b\n- c");
            CaretToLineStart(editor, 1);

            Assert.True(editor.PressBackspace());
            Assert.Equal("- a\nb\n- c", editor.Markdown);
        });
    }

    [Fact]
    public void Backspace_at_the_start_of_a_checklist_item_or_heading_removes_the_style()
    {
        Sta(() =>
        {
            var editor = NewEditor("- [ ] task\n# Head");
            CaretToLineStart(editor, 0);
            Assert.True(editor.PressBackspace());
            CaretToLineStart(editor, 1);
            Assert.True(editor.PressBackspace());

            Assert.Equal("task\nHead", editor.Markdown);
        });
    }

    [Fact]
    public void Toolbar_checklist_and_heading_toggle_on_and_off()
    {
        Sta(() =>
        {
            var editor = NewEditor("buy milk");
            CaretToLineStart(editor, 0);

            editor.ToggleChecklist();
            Assert.Equal("- [ ] buy milk", editor.Markdown);
            editor.ToggleHeading();
            Assert.Equal("# buy milk", editor.Markdown);
            editor.ToggleHeading();
            Assert.Equal("buy milk", editor.Markdown);
        });
    }

    [Fact]
    public void Loading_a_note_shows_its_formatting()
    {
        Sta(() =>
        {
            var editor = new RichNoteEditor { Markdown = "# Title\n- [x] done" };

            Assert.Equal(NoteBlockKind.Heading1, NoteDocument.KindOf((Paragraph)editor.Document.Blocks.FirstBlock));
            Assert.False(editor.IsEmpty);
        });
    }

    [Fact]
    public void Copying_whole_lines_keeps_checklists_bullets_headings_and_bold()
    {
        Sta(() =>
        {
            var editor = NewEditor("# Plan\n- [x] call mom\n- [ ] buy **milk**\n- bread\nplain *words*");
            editor.SelectAll();
            Assert.Equal("# Plan\n- [x] call mom\n- [ ] buy **milk**\n- bread\nplain *words*", editor.SelectionMarkdown());
        });
    }

    [Fact]
    public void Copying_part_of_a_line_takes_just_those_words()
    {
        Sta(() =>
        {
            var editor = NewEditor("- [ ] buy **milk** today");
            var line = (Paragraph)editor.Document.Blocks.FirstBlock;
            // "buy milk" without the checkbox or "today"
            var from = FindText(line, "buy");
            var to = FindText(line, " today");
            editor.Selection.Select(from, to);
            Assert.Equal("buy **milk**", editor.SelectionMarkdown());
        });
    }

    [Fact]
    public void Pasting_lines_into_an_empty_line_makes_real_checklist_items_with_normal_spacing()
    {
        Sta(() =>
        {
            var editor = NewEditor("Shopping\n\nafter");
            CaretToLineStart(editor, 1);
            editor.InsertBlocks(NoteMarkdown.Parse("- [x] eggs\n- [ ] **milk**"));
            Assert.Equal("Shopping\n- [x] eggs\n- [ ] **milk**\nafter", editor.Markdown);
            foreach (Paragraph p in editor.Document.Blocks.OfType<Paragraph>())
            {
                Assert.True(p.Margin.Top <= 5 && p.Margin.Bottom <= 2, $"spacing {p.Margin}");
            }
        });
    }

    [Fact]
    public void Pasting_in_the_middle_of_a_line_splits_it_around_the_new_lines()
    {
        Sta(() =>
        {
            var editor = NewEditor("ab");
            var line = (Paragraph)editor.Document.Blocks.FirstBlock;
            editor.CaretPosition = FindText(line, "b");
            editor.InsertBlocks(NoteMarkdown.Parse("x\n- y"));
            Assert.Equal("ax\n- yb", editor.Markdown);
        });
    }

    [Fact]
    public void Text_from_other_apps_pastes_as_bullets_and_checklists()
    {
        Assert.Equal("- one\n- [ ] two\n- [x] three\nplain", NoteMarkdown.FromPastedText("• one\r\n☐ two\r\n☑ three\r\nplain"));
        var markdown = "## Title\n\n- [x] done\n- [ ] **todo**\n- bullet";
        Assert.Equal($"Title{Environment.NewLine}{Environment.NewLine}☑ done{Environment.NewLine}☐ todo{Environment.NewLine}• bullet", NoteMarkdown.ToPlainText(markdown));
    }

    /// <summary>The position just before <paramref name="text"/> in a line.</summary>
    private static TextPointer FindText(Paragraph line, string text)
    {
        for (var p = line.ContentStart; p is not null && p.CompareTo(line.ContentEnd) < 0; p = p.GetNextInsertionPosition(LogicalDirection.Forward))
        {
            var end = p;
            var ok = true;
            foreach (var c in text)
            {
                var next = end.GetNextInsertionPosition(LogicalDirection.Forward);
                if (next is null || new TextRange(end, next).Text != c.ToString())
                {
                    ok = false;
                    break;
                }

                end = next;
            }

            if (ok)
            {
                return p;
            }
        }

        throw new InvalidOperationException($"'{text}' not found");
    }
}