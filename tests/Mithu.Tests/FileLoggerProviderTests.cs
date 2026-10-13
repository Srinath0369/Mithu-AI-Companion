using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Mithu.Infrastructure.Logging;

namespace Mithu.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Writes_daily_log_file_with_message()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(TimeZoneInfo.Utc);

        using (var provider = new FileLoggerProvider(_dir, clock))
        {
            provider.CreateLogger("Mithu.Tests.Sample").LogInformation("hello {Name}", "Mithu");
        } // Dispose flushes the background queue.

        var file = Path.Combine(_dir, "mithu-20261009.log");
        Assert.True(File.Exists(file));
        var text = File.ReadAllText(file);
        Assert.Contains("[INF] Sample: hello Mithu", text);
    }

    [Fact]
    public void Ignores_messages_below_minimum_level()
    {
        using (var provider = new FileLoggerProvider(_dir, TimeProvider.System) { MinimumLevel = LogLevel.Warning })
        {
            provider.CreateLogger("X").LogInformation("should not appear");
        }

        Assert.Empty(Directory.GetFiles(_dir, "*.log"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
