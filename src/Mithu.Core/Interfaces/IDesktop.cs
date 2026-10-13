using Mithu.Core.Desktop;

namespace Mithu.Core.Interfaces;

/// <summary>
/// The Windows side of Desktop control: what's installed and open, opening things, finding files, volume. Every
/// call is one of the allow-listed abilities; callers check permissions and paths first (see <see cref="PathGuard"/>).
/// </summary>
public interface IDesktop
{
    /// <summary>Apps from the Start menu (desktop and Store apps), cached for a few minutes.</summary>
    Task<IReadOnlyList<AppEntry>> InstalledAppsAsync(CancellationToken cancellationToken = default);

    /// <summary>Apps with a window open now (Mithu's own windows left out).</summary>
    IReadOnlyList<RunningApp> RunningApps();

    /// <summary>Brings an open window to the front (restored if minimised).</summary>
    bool SwitchTo(RunningApp app);

    void Launch(AppEntry app);

    /// <summary>Opens a folder in File Explorer, or a file in its default app.</summary>
    void Open(string path);

    /// <summary>Shows a file selected in File Explorer.</summary>
    void Reveal(string path);

    /// <summary>Files under the roots that match, newest first (bounded; links and hidden/system items skipped).</summary>
    Task<IReadOnlyList<FileHit>> SearchAsync(IReadOnlyList<string> roots, FileSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>Volume 0-100 and mute of the default speakers, or null when there's no audio device.</summary>
    (int Percent, bool Muted)? GetVolume();

    bool SetVolume(int? percent, bool? mute);

    /// <summary>Opens an allow-listed <c>ms-settings:</c> page.</summary>
    void OpenSettings(string uri);

    /// <summary>Where a path really leads (junctions and links followed), or null when unknown.</summary>
    string? FinalPath(string path);

    /// <summary>The default allowed folders (Desktop, Documents, Downloads, Pictures, Music, Videos), by name.</summary>
    IReadOnlyList<(string Name, string Path)> KnownFolders();

    /// <summary>Disks inside the PC other than the Windows disk ("D:\" with its name, "Data (D:)"), which can be allowed whole.</summary>
    IReadOnlyList<(string Root, string Name)> LocalDisks();

    /// <summary>Each monitor's work area (screen minus taskbar), the main monitor first; physical pixels.</summary>
    IReadOnlyList<Vision.CaptureRect> MonitorWorkAreas();

    /// <summary>Puts a window on a half / quarter / middle of its monitor (or <paramref name="monitor"/>), or maximizes, minimizes, restores it.</summary>
    bool Place(RunningApp window, WindowPlace place, int? monitor = null);

    /// <summary>Asks a window to close, the way its ✕ does (the app may still ask to save).</summary>
    bool Close(RunningApp window);

    /// <summary>Sends a file or folder to the Recycle Bin (only for undoing Mithu's own copies and new folders).</summary>
    bool Recycle(string path);

    /// <summary>Windows and program folders, which can't be added as allowed folders.</summary>
    IReadOnlyList<string> SystemFolders();
}
