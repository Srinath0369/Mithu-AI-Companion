using Mithu.Core.Input;

namespace Mithu.Tests;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Shift+Space", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x20, "Ctrl+Shift+Space")]
    [InlineData("ctrl + alt + m", HotkeyModifiers.Control | HotkeyModifiers.Alt, 'M', "Ctrl+Alt+M")]
    [InlineData("Win+Shift+7", HotkeyModifiers.Windows | HotkeyModifiers.Shift, '7', "Shift+Win+7")]
    [InlineData("Alt+F2", HotkeyModifiers.Alt, 0x71, "Alt+F2")]
    [InlineData("Control+F24", HotkeyModifiers.Control, 0x87, "Ctrl+F24")]
    public void Parses_valid_gestures(string text, HotkeyModifiers modifiers, int vk, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(modifiers, gesture.Modifiers);
        Assert.Equal(vk, gesture.VirtualKey);
        Assert.Equal(canonical, gesture.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Space")]            // no modifier
    [InlineData("Shift+A")]          // Shift alone would block typing capitals
    [InlineData("Ctrl+Ctrl+A")]      // duplicate modifier
    [InlineData("Ctrl+Hyper+A")]     // unknown modifier
    [InlineData("Ctrl+F25")]         // out of range
    [InlineData("Ctrl+Enter")]       // unsupported key
    [InlineData("Ctrl+")]
    public void Rejects_invalid_gestures(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
    }

    [Fact]
    public void Default_round_trips()
    {
        Assert.True(HotkeyGesture.TryParse(HotkeyGesture.Default.ToString(), out var parsed));
        Assert.Equal(HotkeyGesture.Default, parsed);
    }
}
