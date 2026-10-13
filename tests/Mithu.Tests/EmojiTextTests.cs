using Mithu.App.Controls;

namespace Mithu.Tests;

public class EmojiTextTests
{
    private static string[] Emoji(string text) => EmojiText.Split(text).Where(p => p.IsEmoji).Select(p => p.Text).ToArray();

    [Theory]
    [InlineData("Hi 👀", "👀")]
    [InlineData("party 🎉!", "🎉")]
    [InlineData("love ❤️ it", "❤️")]           // a symbol made emoji by ️
    [InlineData("👍🏽 nice", "👍🏽")]           // skin tone stays with its hand
    [InlineData("family 👨‍👩‍👧", "👨‍👩‍👧")]   // joined by ZWJ: one picture
    [InlineData("🇮🇳 flag", "🇮🇳")]
    [InlineData("press 1️⃣", "1️⃣")]
    [InlineData("⏰ Call Mom", "⏰")]           // shown as emoji by default
    [InlineData("🏳️‍🌈", "🏳️‍🌈")]
    public void Finds_whole_emoji(string text, string emoji) => Assert.Equal([emoji], Emoji(text));

    [Theory]
    [InlineData("AI is on ✓")]
    [InlineData("Back → next")]
    [InlineData("Room 101, #3 * 2")]
    [InlineData("© 2026")]                      // text style unless ️ follows
    [InlineData("•  bullet")]
    [InlineData("")]
    public void Leaves_ordinary_symbols_as_text(string text)
    {
        Assert.Empty(Emoji(text));
        Assert.False(EmojiText.HasEmoji(text));
    }

    [Fact]
    public void Keeps_the_text_around_emoji_in_order()
    {
        var parts = EmojiText.Split("Done 🎉🎉 yay 👍🏽").ToList();
        Assert.Equal(["Done ", "🎉", "🎉", " yay ", "👍🏽"], parts.Select(p => p.Text));
        Assert.Equal([false, true, true, false, true], parts.Select(p => p.IsEmoji));
        Assert.Equal("Done 🎉🎉 yay 👍🏽", string.Concat(parts.Select(p => p.Text)));
    }
}
