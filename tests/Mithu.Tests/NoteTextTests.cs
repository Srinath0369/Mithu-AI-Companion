using Mithu.Core.Notes;

namespace Mithu.Tests;

public sealed class NoteTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 15, 0, 0, TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData("Shopping", "milk", "Shopping")]
    [InlineData("  ", "# Groceries\n- milk", "Groceries")]
    [InlineData("", "\n\n- [ ] **call** mom", "call mom")]
    [InlineData("", "", "Untitled note")]
    public void Display_title_falls_back_to_the_first_line(string title, string content, string expected)
    {
        Assert.Equal(expected, NoteText.DisplayTitle(title, content));
    }

    [Fact]
    public void Preview_skips_the_line_used_as_title()
    {
        Assert.Equal("milk · bread", NoteText.Preview("", "# Groceries\n- milk\n- bread"));
        Assert.Equal("Groceries · milk", NoteText.Preview("List", "# Groceries\n- milk"));
    }

    [Theory]
    [InlineData("**bold** and *italic*", "bold and italic")]
    [InlineData("- [x] _done_ thing", "done thing")]
    [InlineData("## Heading", "Heading")]
    [InlineData("2 * 3 * 4", "2 * 3 * 4")]
    public void Strips_formatting(string line, string expected)
    {
        Assert.Equal(expected, NoteText.StripMarkdown(line));
    }

    [Fact]
    public void Tags_are_cleaned_and_deduplicated()
    {
        var tags = NoteText.ParseTags("Work, #ideas  work; Home-Office  !!  ");

        Assert.Equal(["work", "ideas", "home-office"], tags);
    }

    [Fact]
    public void Tags_are_limited()
    {
        var many = Enumerable.Range(1, 20).Select(i => $"tag{i}");

        Assert.Equal(NoteText.MaxTags, NoteText.NormalizeTags(many).Count);
        Assert.Equal(NoteText.MaxTagLength, NoteText.NormalizeTags([new string('a', 50)])[0].Length);
    }

    [Fact]
    public void Hashtags_are_found_in_text()
    {
        Assert.Equal(["errands", "home"], NoteText.Hashtags("buy milk #errands and #Home, not mail#x or &#39;"));
    }

    [Fact]
    public void Formats_tags_for_editing()
    {
        Assert.Equal("work, ideas", NoteText.FormatTags(["work", "ideas"]));
    }

    [Theory]
    [InlineData(0.2, "just now")]
    [InlineData(5, "5 min ago")]
    [InlineData(150, "2 h ago")]
    public void Recent_times_are_relative(double minutesAgo, string expected)
    {
        Assert.Equal(expected, NoteText.Ago(Now.AddMinutes(-minutesAgo), Now));
    }

    [Fact]
    public void Older_times_use_days_and_dates()
    {
        Assert.Equal("Yesterday", NoteText.Ago(Now.AddDays(-1), Now));
        Assert.Equal("Sat", NoteText.Ago(Now.AddDays(-2), Now));
        Assert.Equal("1 Oct", NoteText.Ago(new DateTimeOffset(2026, 10, 1, 9, 0, 0, Now.Offset), Now));
        Assert.Equal("1 Oct 2025", NoteText.Ago(new DateTimeOffset(2025, 10, 1, 9, 0, 0, Now.Offset), Now));
    }

    [Fact]
    public void Duplicate_is_a_fresh_unpinned_copy()
    {
        var created = Now.AddDays(-3);
        var note = new Note("a", "Plan", "body", ["work"], IsPinned: true, created, created);

        var copy = note.Duplicate(Now);

        Assert.NotEqual(note.Id, copy.Id);
        Assert.Equal("Plan (copy)", copy.Title);
        Assert.Equal("body", copy.Content);
        Assert.Equal(["work"], copy.Tags);
        Assert.False(copy.IsPinned);
        Assert.Equal(Now, copy.CreatedUtc);
    }

    [Fact]
    public void Clipboard_text_has_title_then_content()
    {
        var note = Note.Create(Now, "Plan", "- one\n- two");

        Assert.Equal($"Plan{Environment.NewLine}{Environment.NewLine}- one\n- two", note.ToClipboardText());
        Assert.Equal("just text", Note.Create(Now, "", "just text").ToClipboardText());
    }

    [Fact]
    public void Empty_notes_are_recognised()
    {
        Assert.True(Note.Create(Now, " ", "\n").IsEmpty);
        Assert.False(Note.Create(Now, "", "x").IsEmpty);
    }
}
