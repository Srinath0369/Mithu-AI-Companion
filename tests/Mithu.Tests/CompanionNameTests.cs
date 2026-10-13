using Mithu.Core.Ai;
using Mithu.Core.Models;

namespace Mithu.Tests;

public class CompanionNameTests
{
    [Theory]
    [InlineData(null, "Mithu")]
    [InlineData("", "Mithu")]
    [InlineData("   ", "Mithu")]
    [InlineData("  Pixel  ", "Pixel")]
    [InlineData("Captain   Byte", "Captain Byte")]
    [InlineData("Line\nbreak\t", "Linebreak")]
    [InlineData("A very long companion name indeed", "A very long companio")]
    [InlineData("मिठू", "मिठू")]
    public void Names_are_tidied(string? typed, string expected) => Assert.Equal(expected, CompanionNames.Clean(typed));

    [Fact]
    public void A_long_name_never_cuts_an_emoji_in_half()
    {
        var name = CompanionNames.Clean(new string('a', 19) + "🐦🐦");
        Assert.Equal(new string('a', 19) + "🐦", name);
    }

    [Fact]
    public void The_ai_introduces_itself_by_the_chosen_name()
    {
        var now = new DateTimeOffset(2026, 10, 13, 18, 0, 0, TimeSpan.FromHours(5.5));
        var named = MithuPersona.SystemPrompt(now, TimeZoneInfo.Utc, name: "Pixel");
        Assert.StartsWith("You are Pixel,", named.TrimStart());
        Assert.Contains("Pixel is the name the user chose", named);

        var plain = MithuPersona.SystemPrompt(now, TimeZoneInfo.Utc);
        Assert.StartsWith("You are Mithu,", plain.TrimStart());
        Assert.DoesNotContain("chose for you", plain);
    }

    [Fact]
    public void People_upgrading_never_see_the_setup()
    {
        // Settings saved before the setup existed have no "setupDone": it reads as done.
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"version":3,"dockToTop":true}""",
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.True(old.SetupDone);
        Assert.Equal("Mithu", old.CompanionName);
    }
}
