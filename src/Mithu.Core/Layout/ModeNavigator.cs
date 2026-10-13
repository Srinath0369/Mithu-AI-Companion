using Mithu.Core.Models;

namespace Mithu.Core.Layout;

/// <summary>
/// Arrow navigation between the three sizes: Hidden (notch) ⇄ Compact (bar) ⇄ Expanded (panel).
/// </summary>
public static class ModeNavigator
{
    /// <summary>▼ / Ctrl+Down / scroll down: one size bigger.</summary>
    public static WindowMode Bigger(WindowMode mode) => mode switch
    {
        WindowMode.Hidden => WindowMode.Compact,
        _ => WindowMode.Expanded,
    };

    /// <summary>▲ / Esc / Ctrl+Up / scroll up: one size smaller.</summary>
    public static WindowMode Smaller(WindowMode mode) => mode switch
    {
        WindowMode.Expanded or WindowMode.Peek => WindowMode.Compact,
        _ => WindowMode.Hidden,
    };

    /// <summary>Hovering the notch returns to the size the user last chose (Expanded by default).</summary>
    public static WindowMode RevealTarget(WindowMode lastVisible) =>
        IsVisibleSize(lastVisible) ? lastVisible : WindowMode.Expanded;

    /// <summary>True for the sizes a user can pick and come back to.</summary>
    public static bool IsVisibleSize(WindowMode mode) => mode is WindowMode.Compact or WindowMode.Expanded;
}
