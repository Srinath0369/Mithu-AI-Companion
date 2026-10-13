using Mithu.Core.Layout;
using Mithu.Core.Models;

namespace Mithu.Tests;

public sealed class ModeNavigatorTests
{
    [Theory]
    [InlineData(WindowMode.Hidden, WindowMode.Compact)]
    [InlineData(WindowMode.Compact, WindowMode.Expanded)]
    [InlineData(WindowMode.Expanded, WindowMode.Expanded)] // already biggest
    [InlineData(WindowMode.Peek, WindowMode.Expanded)]
    public void Down_arrow_goes_one_size_bigger(WindowMode from, WindowMode expected) =>
        Assert.Equal(expected, ModeNavigator.Bigger(from));

    [Theory]
    [InlineData(WindowMode.Expanded, WindowMode.Compact)]
    [InlineData(WindowMode.Compact, WindowMode.Hidden)]
    [InlineData(WindowMode.Hidden, WindowMode.Hidden)] // already smallest
    [InlineData(WindowMode.Peek, WindowMode.Compact)]
    public void Up_arrow_goes_one_size_smaller(WindowMode from, WindowMode expected) =>
        Assert.Equal(expected, ModeNavigator.Smaller(from));

    [Fact]
    public void Full_round_trip_with_arrows()
    {
        var mode = WindowMode.Hidden;
        mode = ModeNavigator.Bigger(mode);
        Assert.Equal(WindowMode.Compact, mode);
        mode = ModeNavigator.Bigger(mode);
        Assert.Equal(WindowMode.Expanded, mode);
        mode = ModeNavigator.Smaller(mode);
        Assert.Equal(WindowMode.Compact, mode);
        mode = ModeNavigator.Smaller(mode);
        Assert.Equal(WindowMode.Hidden, mode);
    }

    [Theory]
    [InlineData(WindowMode.Expanded, WindowMode.Expanded)]
    [InlineData(WindowMode.Compact, WindowMode.Compact)]
    [InlineData(WindowMode.Hidden, WindowMode.Expanded)] // never "reveal" into hidden
    [InlineData(WindowMode.Peek, WindowMode.Expanded)]
    public void Hover_reveal_returns_to_last_open_size(WindowMode lastVisible, WindowMode expected) =>
        Assert.Equal(expected, ModeNavigator.RevealTarget(lastVisible));
}
