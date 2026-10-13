namespace Mithu.Core.Layout;

/// <summary>Everything that decides whether the open companion may close back to Hidden right now.</summary>
public readonly record struct CollapseContext(
    bool Enabled,
    bool Visible,
    bool AlreadyHidden,
    bool Dragging,
    bool MenuOpen,
    bool PeekShowing);

/// <summary>
/// Click to open, click away to close (like Spotlight or the Start menu).
/// <list type="bullet">
/// <item>Clicking the notch / face bubble opens the full panel.</item>
/// <item>Clicking anywhere outside Mithu closes it back to Hidden. The draft text is kept.</item>
/// <item>Safety net: if Mithu was opened without ever getting focus (e.g. at launch) a click elsewhere
///   can't be detected, so it closes after <see cref="UnfocusedTimeout"/> once the pointer is away.</item>
/// </list>
/// Hover never opens anything; it only gives visual feedback.
/// </summary>
public static class ClickAwayPolicy
{
    /// <summary>How long an open but never-focused companion waits (pointer away) before closing.</summary>
    public static readonly TimeSpan UnfocusedTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The user clicked another window: close unless something is in progress.</summary>
    public static bool ShouldCloseOnClickAway(in CollapseContext c) =>
        c.Enabled &&
        c.Visible &&
        !c.AlreadyHidden &&
        !c.Dragging &&
        !c.MenuOpen &&
        !c.PeekShowing;

    /// <summary>Safety net for an open companion that never received focus.</summary>
    public static bool ShouldCloseUnfocused(in CollapseContext c, bool isActive, bool pointerOver) =>
        ShouldCloseOnClickAway(c) && !isActive && !pointerOver;
}
