using System.IO;
using System.Text.RegularExpressions;
using Mithu.App.Controls;

namespace Mithu.Tests;

public class IconTests
{
    [Fact]
    public void Every_icon_draws_inside_its_24_unit_grid()
    {
        foreach (var (glyph, shape) in IconShapes.All)
        {
            var bounds = (shape.Stroke ?? shape.Fill)!.Bounds;
            Assert.False(bounds.IsEmpty, $"U+{(int)glyph[0]:X4} is empty");
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24,
                $"U+{(int)glyph[0]:X4} spills out: {bounds}");
        }
    }

    [Fact]
    public void Every_icon_code_used_in_the_app_has_a_vector_shape()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Mithu.App");
        var used = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), "&#x([EF][0-9A-F]{3});").Select(m => (char)Convert.ToInt32(m.Groups[1].Value, 16)))
            .Concat(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .SelectMany(f => File.ReadAllText(f).Where(c => c is >= '' and <= '')))
            .Select(c => c.ToString())
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);
        Assert.All(used, glyph => Assert.True(Icon.HasShape(glyph), $"U+{(int)glyph[0]:X4} has no vector icon"));
    }

    [Fact]
    public void Icon_buttons_turn_codes_into_icons_and_leave_text_alone()
    {
        var selector = new GlyphTemplateSelector();
        Assert.NotNull(selector.SelectTemplate("", null!));
        Assert.Null(selector.SelectTemplate("12", null!));   // calendar days
        Assert.Null(selector.SelectTemplate("New", null!));
    }
}
