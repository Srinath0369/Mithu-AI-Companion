namespace Mithu.Core.Interfaces;

/// <summary>
/// Locations where Mithu stores its local data. Abstracted so tests can use a temp folder.
/// </summary>
public interface IAppPaths
{
    /// <summary>Root data folder, e.g. %LOCALAPPDATA%\Mithu.</summary>
    string DataDirectory { get; }

    /// <summary>Folder for diagnostic log files.</summary>
    string LogDirectory { get; }

    /// <summary>Full path of the SQLite database file (used from Phase 2).</summary>
    string DatabasePath { get; }

    /// <summary>Full path of the lightweight settings file (used from Phase 1).</summary>
    string SettingsPath { get; }

    /// <summary>Full path of the focus timer's state and stats file (until it moves to SQLite).</summary>
    string FocusPath { get; }

    /// <summary>Creates the folders if they do not exist yet.</summary>
    void EnsureCreated();
}
