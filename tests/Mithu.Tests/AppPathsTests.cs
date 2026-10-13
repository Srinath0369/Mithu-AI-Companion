using Mithu.Infrastructure;

namespace Mithu.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Paths_are_rooted_in_data_directory()
    {
        var paths = new AppPaths(_root);

        Assert.Equal(_root, paths.DataDirectory);
        Assert.Equal(Path.Combine(_root, "logs"), paths.LogDirectory);
        Assert.Equal(Path.Combine(_root, "mithu.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(_root, "settings.json"), paths.SettingsPath);
    }

    [Fact]
    public void EnsureCreated_creates_data_and_log_folders()
    {
        var paths = new AppPaths(_root);

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.DataDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
    }

    [Fact]
    public void Default_location_is_local_app_data()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mithu");

        Assert.Equal(expected, new AppPaths().DataDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
