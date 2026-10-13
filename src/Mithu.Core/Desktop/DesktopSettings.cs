namespace Mithu.Core.Desktop;

/// <summary>
/// What Mithu's AI may do on the PC (part of <see cref="Models.AppSettings"/>). Off by default; each area has its own
/// switch, and files are reachable only inside <see cref="AllowedFolders"/>.
/// </summary>
public sealed record DesktopSettings
{
    /// <summary>The master switch ("Let Mithu use apps and files on this PC").</summary>
    public bool Enabled { get; init; }

    /// <summary>Open and switch to apps.</summary>
    public bool AllowApps { get; init; } = true;

    /// <summary>Open folders and files, find files, show them in File Explorer (allowed folders only).</summary>
    public bool AllowFiles { get; init; } = true;

    /// <summary>Volume and Windows Settings pages.</summary>
    public bool AllowSystem { get; init; } = true;

    /// <summary>Read the text the user copied, when they ask ("summarize what I copied"). Off: copied text can be a password.</summary>
    public bool AllowClipboard { get; init; }

    public bool ClipboardOn => Enabled && AllowClipboard;

    /// <summary>Every action waits for the user's Approve click (off by default; looking things up never asks).</summary>
    public bool AskBeforeEachAction { get; init; }

    /// <summary>Folders files may come from; null = the defaults (Desktop, Documents, Downloads, Pictures, Music, Videos).</summary>
    public IReadOnlyList<string>? AllowedFolders { get; init; }

    public bool AppsOn => Enabled && AllowApps;

    public bool FilesOn => Enabled && AllowFiles;

    public bool SystemOn => Enabled && AllowSystem;
}
