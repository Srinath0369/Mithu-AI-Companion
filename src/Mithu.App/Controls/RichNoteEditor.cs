using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Mithu.Core.Notes;

namespace Mithu.App.Controls;

/// <summary>
/// The note editor: formatting shows while you type (no separate edit/preview modes).
/// <list type="bullet">
/// <item>Toolbar / shortcuts: bold, italic (Ctrl+B / Ctrl+I), heading, bullets, checklist.</item>
/// <item>Enter continues a bullet or checklist; Enter on an empty item ends the list; Backspace at the start of a
///   heading, bullet or checklist item turns it back into plain text.</item>
/// <item>Typing <c>- </c>, <c>* </c>, <c># </c> or <c>[] </c> at the start of a line starts a bullet, heading or checklist.</item>
/// <item>Copy puts Mithu's note format (plus clean text for other apps) on the clipboard; paste turns lines back
///   into bullets, checklist items, headings and bold. Outside fonts and colours are dropped.</item>
/// </list>
/// <see cref="Markdown"/> is the stored text: it updates on every edit (two-way bindable) and rebuilds the document
/// when it is set from outside (another note opened).
/// </summary>
public sealed class RichNoteEditor : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(RichNoteEditor),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((RichNoteEditor)d).OnMarkdownChanged((string?)e.NewValue ?? string.Empty)));

    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.Register(
        nameof(IsEmpty), typeof(bool), typeof(RichNoteEditor), new PropertyMetadata(true));

    /// <summary>Formatting the stored text can't represent: ignored so nothing is silently lost on save.</summary>
    private static readonly RoutedUICommand[] Unsupported =
    [
        EditingCommands.ToggleUnderline, EditingCommands.AlignCenter, EditingCommands.AlignJustify, EditingCommands.AlignLeft,
        EditingCommands.AlignRight, EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize,
        EditingCommands.ToggleNumbering, EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript,
        EditingCommands.IncreaseIndentation, EditingCommands.DecreaseIndentation,
    ];

    private string _written = string.Empty; // the text the document currently shows
    private bool _building;
    private (Paragraph Line, NoteBlockKind Kind, string Shortcut)? _pendingShortcut;

    public RichNoteEditor()
    {
        IsDocumentEnabled = true; // checkboxes inside the text can be clicked
        AcceptsReturn = true;
        AcceptsTab = false;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        FocusVisualStyle = null;
        Document = NoteDocument.Create(string.Empty);

        TextChanged += (_, _) =>
        {
            if (_building)
            {
                return;
            }

            Push();

            // "- ", "# ", "[] " just typed at the start of a line: format that line right after this edit completes
            // (changing the document inside its own change notification is not safe). Fast typing may already have
            // added more text by then, so the line and the shortcut are remembered now.
            if (ShortcutAtCaret() is { } kind && CaretPosition.Paragraph is { } line)
            {
                _pendingShortcut = (line, kind, TextBefore(line));
                Dispatcher.BeginInvoke(ApplyPendingShortcut, System.Windows.Threading.DispatcherPriority.Normal);
            }
        };
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnCheckBoxClick));
        DataObject.AddPastingHandler(this, OnPaste);
        DataObject.AddCopyingHandler(this, OnCopy);
        foreach (var command in Unsupported)
        {
            CommandBindings.Add(new CommandBinding(command, (_, e) => e.Handled = true, (_, e) => { e.CanExecute = true; e.Handled = true; }));
        }
    }

    /// <summary>The note text in its stored form.</summary>
    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>Nothing written yet (shows the placeholder).</summary>
    public bool IsEmpty
    {
        get => (bool)GetValue(IsEmptyProperty);
        private set => SetValue(IsEmptyProperty, value);
    }

    /// <summary>A different note was opened: forget undo history from the previous one.</summary>
    public void ResetHistory()
    {
        IsUndoEnabled = false;
        IsUndoEnabled = true;
    }

    public void FocusAtEnd()
    {
        Focus();
        Keyboard.Focus(this);
        CaretPosition = Document.ContentEnd;
    }

    // ===== Toolbar ==========================================================

    public void ToggleBold() => Run(EditingCommands.ToggleBold);

    public void ToggleItalic() => Run(EditingCommands.ToggleItalic);

    /// <summary>Heading on/off for the selected lines.</summary>
    public void ToggleHeading()
    {
        var lines = SelectedParagraphs();
        var makeHeading = !lines.All(p => NoteDocument.IsHeading(NoteDocument.KindOf(p)));
        Unlist(lines);
        foreach (var paragraph in SelectedParagraphs())
        {
            NoteDocument.SetCheck(paragraph, isCheck: false);
            NoteDocument.SetHeading(paragraph, makeHeading ? NoteBlockKind.Heading1 : NoteBlockKind.Paragraph);
        }

        Focus();
        Changed();
    }

    /// <summary>Bullets on/off for the selected lines.</summary>
    public void ToggleBullets()
    {
        foreach (var paragraph in SelectedParagraphs())
        {
            NoteDocument.SetCheck(paragraph, isCheck: false);
            NoteDocument.SetHeading(paragraph, NoteBlockKind.Paragraph);
        }

        Run(EditingCommands.ToggleBullets);
    }

    /// <summary>Checklist on/off for the selected lines.</summary>
    public void ToggleChecklist()
    {
        var lines = SelectedParagraphs();
        var makeCheck = !lines.All(p => NoteDocument.KindOf(p) == NoteBlockKind.Check);
        Unlist(lines);
        foreach (var paragraph in SelectedParagraphs())
        {
            NoteDocument.SetCheck(paragraph, makeCheck);
        }

        Focus();
        Changed();
    }

    // ===== Keys ==============================================================

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            e.Handled = PressEnter();
        }
        else if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && Selection.IsEmpty)
        {
            e.Handled = PressBackspace();
        }
        else
        {
            ApplyPendingShortcut();
        }

        if (!e.Handled)
        {
            base.OnPreviewKeyDown(e);
        }
    }

    /// <summary>
    /// Enter: lists continue, empty items end them, headings don't run on, Shift+Enter is a normal new line.
    /// A shortcut typed just before ("- milk" then Enter) is applied first, so Enter sees a bullet. Returns true if handled.
    /// </summary>
    internal bool PressEnter()
    {
        ApplyPendingShortcut();
        if (CaretPosition.Paragraph is not { } paragraph)
        {
            return false;
        }

        var kind = NoteDocument.KindOf(paragraph);
        var empty = NoteDocument.TextOf(paragraph).Trim().Length == 0;

        if (kind == NoteBlockKind.Check && empty)
        {
            NoteDocument.SetCheck(paragraph, isCheck: false);
            Changed();
            return true;
        }

        if (kind == NoteBlockKind.Bullet && empty && paragraph.Parent is ListItem emptyItem)
        {
            EndListAt(emptyItem);
            return true;
        }

        if (!Selection.IsEmpty)
        {
            Selection.Text = string.Empty;
        }

        EditingCommands.EnterParagraphBreak.Execute(null, this);
        if (CaretPosition.Paragraph is { } next && next != paragraph)
        {
            NoteDocument.ApplyCheckedLook(next, isChecked: false);
            if (kind == NoteBlockKind.Check)
            {
                NoteDocument.SetCheck(next, isCheck: true);
                CaretPosition = next.ContentEnd;
            }
            else if (NoteDocument.IsHeading(kind))
            {
                NoteDocument.SetHeading(next, NoteBlockKind.Paragraph);
            }
        }

        Changed();
        return true;
    }

    /// <summary>Backspace at the very start of a heading, bullet or checklist item removes that style first. True if handled.</summary>
    internal bool PressBackspace()
    {
        ApplyPendingShortcut();
        if (CaretPosition.Paragraph is not { } paragraph || TextBefore(paragraph).Length > 0)
        {
            return false;
        }

        switch (NoteDocument.KindOf(paragraph))
        {
            case NoteBlockKind.Check:
                NoteDocument.SetCheck(paragraph, isCheck: false);
                Changed();
                return true;

            case NoteBlockKind.Bullet when paragraph.Parent is ListItem item:
                EndListAt(item);
                return true;

            case var kind when NoteDocument.IsHeading(kind):
                NoteDocument.SetHeading(paragraph, NoteBlockKind.Paragraph);
                Changed();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// The line style a just-typed shortcut asks for — "- " / "* " → bullet, "# " → heading, "[] " → checklist —
    /// when it is exactly what precedes the caret at the start of a plain line; otherwise null.
    /// </summary>
    private NoteBlockKind? ShortcutAtCaret()
    {
        if (!Selection.IsEmpty || CaretPosition.Paragraph is not { } paragraph ||
            NoteDocument.KindOf(paragraph) != NoteBlockKind.Paragraph)
        {
            return null;
        }

        return TextBefore(paragraph) switch
        {
            "- " or "* " => NoteBlockKind.Bullet,
            "# " => NoteBlockKind.Heading1,
            "[] " or "[ ] " => NoteBlockKind.Check,
            _ => null,
        };
    }

    /// <summary>
    /// Removes the shortcut characters from the start of <paramref name="paragraph"/> and applies the style —
    /// if the line still starts with them and is still a plain line in this document.
    /// </summary>
    private void ApplyPendingShortcut()
    {
        if (_pendingShortcut is { } pending)
        {
            _pendingShortcut = null;
            ApplyShortcut(pending.Line, pending.Kind, pending.Shortcut);
        }
    }

    private void ApplyShortcut(Paragraph paragraph, NoteBlockKind kind, string shortcut)
    {
        if (paragraph.Parent is null || NoteDocument.KindOf(paragraph) != NoteBlockKind.Paragraph ||
            !NoteDocument.TextOf(paragraph).StartsWith(shortcut, StringComparison.Ordinal))
        {
            return;
        }

        var end = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        for (var i = 0; i < shortcut.Length && end is not null; i++)
        {
            end = end.GetNextInsertionPosition(LogicalDirection.Forward);
        }

        if (end is null)
        {
            return;
        }

        new TextRange(paragraph.ContentStart, end).Text = string.Empty;
        switch (kind)
        {
            case NoteBlockKind.Bullet:
                // Bullet that line — not whichever line the caret has moved on to — and keep the caret in place.
                var caretLine = CaretPosition.Paragraph;
                var caretOffset = caretLine is null ? 0 : TextBefore(caretLine).Length;
                Selection.Select(paragraph.ContentStart, paragraph.ContentStart);
                EditingCommands.ToggleBullets.Execute(null, this);
                if (caretLine?.Parent is not null)
                {
                    CaretPosition = PositionAt(caretLine, caretOffset);
                }

                break;
            case NoteBlockKind.Check:
                NoteDocument.SetCheck(paragraph, isCheck: true);

                // Keep typing after the new checkbox, not in front of it.
                if (paragraph.Inlines.FirstInline is InlineUIContainer box && CaretPosition.CompareTo(box.ElementEnd) < 0)
                {
                    CaretPosition = box.ElementEnd;
                }

                break;
            default:
                NoteDocument.SetHeading(paragraph, kind);
                break;
        }

        Changed();
    }

    // ===== Plumbing ==========================================================

    private void OnMarkdownChanged(string markdown)
    {
        if (markdown == _written)
        {
            return;
        }

        _building = true;
        _pendingShortcut = null;
        try
        {
            Document = NoteDocument.Create(markdown);
            _written = markdown;
            IsEmpty = markdown.Trim().Length == 0;
        }
        finally
        {
            _building = false;
        }
    }

    /// <summary>Reads the document back into stored text and hands it to the binding (autosave takes it from there).</summary>
    private void Push()
    {
        var markdown = NoteDocument.ToMarkdown(Document);
        IsEmpty = markdown.Trim().Length == 0;
        if (markdown == _written)
        {
            return;
        }

        _written = markdown;
        SetCurrentValue(MarkdownProperty, markdown);
    }

    /// <summary>Structure changed in a way that may not raise TextChanged (e.g. only paragraph properties).</summary>
    private void Changed()
    {
        if (!_building)
        {
            Push();
        }
    }

    private void Run(RoutedUICommand command)
    {
        command.Execute(null, this);
        Focus();
        Changed();
    }

    private void OnCheckBoxClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is CheckBox { Parent: InlineUIContainer { Parent: Paragraph paragraph } } box)
        {
            NoteDocument.ApplyCheckedLook(paragraph, box.IsChecked == true);
            Changed();
        }
    }

    /// <summary>
    /// Copy / cut: Mithu's note format (so another note gets the same bullets, checklists and bold) plus clean text
    /// for other apps, instead of WPF's raw text ("•\t" markers, no checkboxes).
    /// </summary>
    private void OnCopy(object sender, DataObjectCopyingEventArgs e)
    {
        var markdown = SelectionMarkdown();
        if (markdown.Length == 0)
        {
            return;
        }

        e.DataObject.SetData(DataFormats.UnicodeText, NoteMarkdown.ToPlainText(markdown));
        e.DataObject.SetData(NoteClipboard.Format, markdown);
    }

    /// <summary>
    /// Paste: lines become real note lines (bullets, checklist items, headings, bold) with the note's own spacing;
    /// a few plain words paste as usual. Outside fonts, colours and sizes are never taken (they can't be stored).
    /// </summary>
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (NoteClipboard.Read(e.DataObject) is not { } pasted)
        {
            e.CancelCommand();
            return;
        }

        var blocks = NoteMarkdown.Parse(pasted.Markdown);
        var simple = blocks.Count <= 1 && blocks.All(b => b.Kind == NoteBlockKind.Paragraph && b.Spans.All(s => !s.Bold && !s.Italic));
        if (simple || e.IsDragDrop)
        {
            // Plain words (or a drag inside the note, which WPF moves itself): ordinary text.
            var text = pasted.FromMithu || blocks.Count != 1
                ? string.Join(Environment.NewLine, blocks.Select(b => b.Text))
                : e.DataObject.GetData(DataFormats.UnicodeText) as string ?? blocks[0].Text;
            var plain = new DataObject();
            plain.SetData(DataFormats.UnicodeText, text);
            e.DataObject = plain;
            return;
        }

        e.CancelCommand();
        InsertBlocks(blocks);
    }

    /// <summary>The selection as note text (whole lines keep their kind; part of one line is just its words).</summary>
    internal string SelectionMarkdown()
    {
        var start = Selection.Start;
        var end = Selection.End;
        if (Selection.IsEmpty)
        {
            return string.Empty;
        }

        var lines = Paragraphs(Document.Blocks)
            .Where(p => p.ContentEnd.CompareTo(start) >= 0 && p.ContentStart.CompareTo(end) <= 0)
            .ToList();
        if (lines.Count > 1 && start.CompareTo(lines[0].ContentEnd) >= 0)
        {
            lines.RemoveAt(0); // the selection starts after the end of this line
        }

        if (lines.Count > 1 && end.CompareTo(lines[^1].ContentStart) <= 0)
        {
            lines.RemoveAt(lines.Count - 1); // …or ends before this one starts
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        if (lines.Count == 1)
        {
            var line = lines[0];
            var whole = start.CompareTo(PositionAt(line, 0)) <= 0 && end.CompareTo(line.ContentEnd) >= 0;
            var part = NoteDocument.BlockOf(line, start, end);
            return NoteMarkdown.Write([whole ? part : new NoteBlock(NoteBlockKind.Paragraph, part.Spans)]);
        }

        return NoteMarkdown.Write(lines.Select((line, i) =>
            NoteDocument.BlockOf(line, i == 0 ? start : null, i == lines.Count - 1 ? end : null)));
    }

    /// <summary>
    /// Puts pasted lines at the caret: the first joins the text before it (an empty plain line takes the pasted
    /// line's kind), the last joins the text after it. Done as one change, so Ctrl+Z takes it back.
    /// </summary>
    internal void InsertBlocks(IReadOnlyList<NoteBlock> pasted)
    {
        if (pasted.Count == 0)
        {
            return;
        }

        int target;
        int characters;
        BeginChange();
        try
        {
            if (!Selection.IsEmpty)
            {
                Selection.Text = string.Empty;
            }

            var lines = Paragraphs(Document.Blocks).ToList();
            var caret = CaretPosition;
            var current = caret.Paragraph ?? lines.LastOrDefault();
            var index = current is null ? lines.Count : lines.IndexOf(current);
            var existing = lines.Select(p => NoteDocument.BlockOf(p)).ToList();
            var before = current is null ? Empty : NoteDocument.BlockOf(current, null, caret);
            var after = current is null ? Empty : NoteDocument.BlockOf(current, caret, null);
            var kind = current is null ? NoteBlockKind.Paragraph : NoteDocument.KindOf(current);
            var takePastedKind = kind == NoteBlockKind.Paragraph && before.Text.Trim().Length == 0;

            var first = pasted[0];
            var last = pasted[^1];
            var firstKind = takePastedKind ? first.Kind : kind;
            var firstChecked = takePastedKind ? first.Checked : before.Checked;
            var merged = new List<NoteBlock>();
            if (pasted.Count == 1)
            {
                merged.Add(Line(firstKind, [.. before.Spans, .. first.Spans, .. after.Spans], firstChecked));
                characters = before.Text.Length + first.Text.Length;
            }
            else
            {
                merged.Add(Line(firstKind, [.. before.Spans, .. first.Spans], firstChecked));
                merged.AddRange(pasted.Skip(1).Take(pasted.Count - 2));
                merged.Add(Line(last.Kind, [.. last.Spans, .. after.Spans], last.Checked));
                characters = last.Text.Length;
            }

            var all = existing.Take(Math.Max(index, 0)).Concat(merged).Concat(existing.Skip(index + 1));
            target = Math.Max(index, 0) + merged.Count - 1;

            // Rebuilt the way a note is opened (same spacing and checkboxes), inside this one undoable change.
            var fresh = NoteDocument.Create(NoteMarkdown.Write(all));
            var blocks = fresh.Blocks.ToList();
            fresh.Blocks.Clear();
            Document.Blocks.Clear();
            Document.Blocks.AddRange(blocks);
        }
        finally
        {
            EndChange();
        }

        var updated = Paragraphs(Document.Blocks).ToList();
        if (updated.Count > 0)
        {
            CaretPosition = PositionAt(updated[Math.Min(target, updated.Count - 1)], characters);
        }

        Focus();
        Changed();
    }

    private static readonly NoteBlock Empty = new(NoteBlockKind.Paragraph, []);

    /// <summary>A line; an empty "blank" line that now has words is a plain line.</summary>
    private static NoteBlock Line(NoteBlockKind kind, IReadOnlyList<NoteSpan> spans, bool isChecked) =>
        new(kind == NoteBlockKind.Blank && spans.Any(s => s.Text.Length > 0) ? NoteBlockKind.Paragraph : kind, spans, Checked: isChecked);

    /// <summary>The position <paramref name="characters"/> typed characters into a line (after any checkbox).</summary>
    private static TextPointer PositionAt(Paragraph paragraph, int characters)
    {
        var position = NoteDocument.CheckBoxOf(paragraph) is not null
            ? ((InlineUIContainer)paragraph.Inlines.FirstInline).ElementEnd
            : paragraph.ContentStart;
        position = position.GetInsertionPosition(LogicalDirection.Forward);
        for (var i = 0; i < characters; i++)
        {
            var next = position.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next is null || next.Paragraph != paragraph)
            {
                break;
            }

            position = next;
        }

        return position;
    }

    /// <summary>
    /// The typed text between the start of the line and the caret. Only text runs count: a TextRange would also
    /// include the bullet marker ("•\t") and the checkbox placeholder.
    /// </summary>
    private string TextBefore(Paragraph paragraph)
    {
        var text = new System.Text.StringBuilder();
        for (var position = paragraph.ContentStart;
             position is not null && position.CompareTo(CaretPosition) < 0;
             position = position.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var run = position.GetTextInRun(LogicalDirection.Forward);
                var available = position.GetOffsetToPosition(CaretPosition);
                text.Append(run.Length <= available ? run : run[..available]);
            }
        }

        return text.ToString();
    }

    /// <summary>Paragraphs touched by the selection (or the caret's), in document order.</summary>
    private List<Paragraph> SelectedParagraphs()
    {
        var all = Paragraphs(Document.Blocks).ToList();
        var first = Selection.Start.Paragraph ?? CaretPosition.Paragraph;
        var last = Selection.End.Paragraph ?? first;
        var start = first is null ? -1 : all.IndexOf(first);
        var end = last is null ? -1 : all.IndexOf(last);
        if (start < 0)
        {
            return all.Take(1).ToList();
        }

        return all.Skip(start).Take(Math.Max(end, start) - start + 1).ToList();
    }

    /// <summary>
    /// Turns one bullet into a plain line where it stands, splitting the list around it (the items above stay
    /// a list, the items below become a new list). The caret goes to the start of that line.
    /// </summary>
    private void EndListAt(ListItem item)
    {
        if (item.Parent is not List list || list.Parent is null)
        {
            return;
        }

        var siblings = list.SiblingBlocks;
        var following = new List<ListItem>();
        for (var next = item.NextListItem; next is not null; next = next.NextListItem)
        {
            following.Add(next);
        }

        var line = item.Blocks.FirstBlock as Paragraph ?? new Paragraph();
        item.Blocks.Remove(line);
        list.ListItems.Remove(item);
        foreach (var moved in following)
        {
            list.ListItems.Remove(moved);
        }

        siblings.InsertAfter(list, line);
        if (following.Count > 0)
        {
            var rest = NoteDocument.CreateList();
            foreach (var moved in following)
            {
                rest.ListItems.Add(moved);
            }

            siblings.InsertAfter(line, rest);
        }

        if (list.ListItems.Count == 0)
        {
            siblings.Remove(list);
        }

        NoteDocument.SetHeading(line, NoteBlockKind.Paragraph);
        CaretPosition = line.ContentStart;
        Changed();
    }

    /// <summary>Takes the given lines out of any bullet list (so they can become headings or checklist items).</summary>
    private void Unlist(List<Paragraph> lines)
    {
        // With a mix of bullet and plain lines the first toggle bullets everything; the second removes them all.
        for (var i = 0; i < 2 && (i == 0 ? lines : SelectedParagraphs()).Any(p => p.Parent is ListItem); i++)
        {
            EditingCommands.ToggleBullets.Execute(null, this);
        }
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                    {
                        foreach (var inner in Paragraphs(item.Blocks))
                        {
                            yield return inner;
                        }
                    }

                    break;
                case Section section:
                    foreach (var inner in Paragraphs(section.Blocks))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }
}
