using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure;

/// <summary>
/// Default <see cref="IAppPaths"/> rooted at %LOCALAPPDATA%\Mithu (or a custom root for tests).
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Mithu"))
    {
    }

    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = dataDirectory;
    }

    public string DataDirectory { get; }

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string DatabasePath => Path.Combine(DataDirectory, "mithu.db");

    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public string FocusPath => Path.Combine(DataDirectory, "focus.json");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
