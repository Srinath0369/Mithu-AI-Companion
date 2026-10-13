using Mithu.Core.Ai;

namespace Mithu.Tests;

public class FaceTagTests
{
    [Theory]
    [InlineData("[face:wink] Bold move.", "Bold move.", FaceReaction.Wink)]
    [InlineData("  [Face: Ouch ]\nRude!", "Rude!", FaceReaction.Ouch)]
    [InlineData("[face:dance] Ha.", "Ha.", null)]                 // unknown face: tag hidden, no reaction
    [InlineData("Sure! [face:wink]", "Sure! [face:wink]", null)]   // only at the start
    [InlineData("[1] First item", "[1] First item", null)]
    public void A_leading_tag_is_taken_out(string text, string expected, FaceReaction? reaction)
    {
        Assert.Equal(expected, FaceTags.Strip(text, out var face));
        Assert.Equal(reaction, face);
    }

    [Fact]
    public void A_tag_split_across_pieces_never_shows()
    {
        var filter = new FaceTagFilter();
        var shown = "";
        FaceReaction? reaction = null;
        foreach (var piece in new[] { "[fa", "ce:cel", "ebrate]", " You did ", "it!" })
        {
            var (text, face) = filter.Push(piece);
            shown += text;
            reaction ??= face;
            Assert.DoesNotContain("[", shown);
        }

        Assert.Equal("You did it!", shown + filter.Flush());
        Assert.Equal(FaceReaction.Celebrate, reaction);
    }

    [Fact]
    public void Ordinary_replies_pass_straight_through()
    {
        var filter = new FaceTagFilter();
        Assert.Equal("Sure", filter.Push("Sure").Text);       // not a tag: shown at once
        Assert.Equal("[face:wink]", filter.Push("[face:wink]").Text); // later on: just text

        var list = new FaceTagFilter();
        Assert.Equal(string.Empty, list.Push("[").Text);       // could be a tag: held a moment
        Assert.Equal("[1] milk", list.Push("1] milk").Text);   // it isn't: shown whole

        var cut = new FaceTagFilter();
        cut.Push("[face:wi");
        Assert.Equal("[face:wi", cut.Flush());                 // the reply ended mid-tag: show what came
    }

    [Fact]
    public void The_saved_message_has_no_tag()
    {
        var message = new AiMessage(AiRole.Assistant, [new AiText("[face:hmph] Excuse me?"), new AiToolCall("1", "list_tasks", "{}")]);
        var clean = FaceTags.Strip(message, out var face);
        Assert.Equal("Excuse me?", clean.Text);
        Assert.Single(clean.ToolCalls);
        Assert.Equal(FaceReaction.Hmph, face);
    }

    [Fact]
    public void The_ai_knows_its_faces_and_when_not_to_use_them()
    {
        var prompt = MithuPersona.SystemPrompt(DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc, canAct: true);
        foreach (var name in FaceTags.Names)
        {
            Assert.Contains($"[face:{name}]", prompt);
        }

        Assert.Contains("Most replies have no face", prompt);
        Assert.Contains("Serious, sad or health topics get no playful face", prompt);
    }
}
