using Mithu.Core.Layout;

namespace Mithu.Core.Models;

/// <summary>
/// User preferences persisted between launches. Immutable: change with <c>with</c> expressions.
/// Bump <see cref="CurrentVersion"/> and extend <see cref="SettingsMigrator"/> when the shape changes.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 3;

    public int Version { get; init; } = CurrentVersion;

    public bool ReducedMotion { get; init; }

    public bool AlwaysOnTop { get; init; } = true;

    public bool QuietMode { get; init; }

    public bool AutoHideInFullscreen { get; init; } = true;

    public string Hotkey { get; init; } = "Ctrl+Shift+Space";

    /// <summary>Size shown when Mithu last ran (restored on launch).</summary>
    public WindowMode LastMode { get; init; } = WindowMode.Compact;

    /// <summary>Last open size the user chose; hovering the notch returns to it. Defaults to the full panel.</summary>
    public WindowMode LastVisibleMode { get; init; } = WindowMode.Expanded;

    /// <summary>Stick flush to the top edge of the screen, like a notch.</summary>
    public bool DockToTop { get; init; } = true;

    /// <summary>Clicking anywhere outside Mithu closes it back to Hidden (notch or face bubble).</summary>
    public bool CloseOnClickAway { get; init; } = true;

    /// <summary>How big the open panel is: 1 (normal), 1.25 or 1.5 (more room, same text size).</summary>
    public double PanelScale { get; init; } = 1;

    /// <summary>Thin border lines round the island and the face (off: one clean dark shape, the default).</summary>
    public bool ShowBorders { get; init; }

    /// <summary>Saved position; <c>null</c> means default top-center of the primary monitor.</summary>
    public WindowPlacement? Placement { get; init; }

    /// <summary>AI assistant choices (off until the user sets it up). The key is kept separately, encrypted.</summary>
    public Ai.AiSettings Ai { get; init; } = new();

    /// <summary>What Mithu's AI may do on the PC (off until the user turns it on).</summary>
    public Desktop.DesktopSettings Desktop { get; init; } = new();

    /// <summary>Showing what Claude Code is doing (off until the user turns it on).</summary>
    public ClaudeCode.ClaudeCodeSettings ClaudeCode { get; init; } = new();

    /// <summary>What the companion is called (chosen in the first-run setup; "Mithu" until then). See <see cref="CompanionNames"/>.</summary>
    public string CompanionName { get; init; } = CompanionNames.Default;

    /// <summary>
    /// The first-run setup was finished or skipped. True unless this is a brand-new install (the settings store sets
    /// it to false only when there were no settings at all), so people upgrading never see the setup.
    /// </summary>
    public bool SetupDone { get; init; } = true;

    /// <summary>An installed Mithu looks for a new version now and then (only when the installer has an update address).</summary>
    public bool CheckForUpdates { get; init; } = true;
}
