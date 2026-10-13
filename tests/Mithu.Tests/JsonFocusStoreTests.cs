using Microsoft.Extensions.Logging.Abstractions;
using Mithu.Core.Focus;
using Mithu.Infrastructure.Focus;

namespace Mithu.Tests;

public sealed class JsonFocusStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    private string FocusPath => Path.Combine(_dir, "focus.json");

    private JsonFocusStore NewStore() => new(FocusPath, NullLogger<JsonFocusStore>.Instance);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var data = NewStore().Load();

        Assert.Null(data.Active);
        Assert.Equal(TimeSpan.FromMinutes(FocusPlan.DefaultFocusMinutes), data.FocusLength);
        Assert.Equal(FocusPlan.DefaultRounds, data.Rounds);
    }

    [Fact]
    public void Save_and_load_round_trips()
    {
        var deadline = new DateTimeOffset(2026, 10, 12, 9, 25, 0, TimeSpan.Zero);
        var data = new FocusData
        {
            Active = new FocusSession(FocusKind.Focus, TimeSpan.FromMinutes(25), TimeSpan.FromMinutes(25), deadline, "Write report"),
            FocusLength = TimeSpan.FromMinutes(37),
            Rounds = 3,
            BreakLength = TimeSpan.FromMinutes(10),
            Round = 2,
        };

        NewStore().Save(data);

        Assert.Equal(data, NewStore().Load());
    }

    [Fact]
    public void Old_file_with_stats_still_loads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FocusPath, """
            { "active": null, "focusLength": "00:40:00", "rounds": 3, "breakLength": "00:10:00", "round": 0,
              "stats": { "day": "2026-10-11", "sessions": 2, "focused": "00:50:00", "lastSessionDay": "2026-10-11", "streak": 1 } }
            """);

        var data = NewStore().Load();

        Assert.Equal(TimeSpan.FromMinutes(40), data.FocusLength);
        Assert.Equal(3, data.Rounds);
        Assert.Empty(Directory.GetFiles(_dir, "focus.json.corrupt-*"));
    }

    [Fact]
    public void Corrupt_file_starts_fresh_and_keeps_backup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FocusPath, "{ not json");

        var data = NewStore().Load();

        Assert.Equal(new FocusData(), data);
        Assert.Single(Directory.GetFiles(_dir, "focus.json.corrupt-*"));
    }

    [Fact]
    public void Saved_file_uses_readable_names()
    {
        NewStore().Save(new FocusData { Active = new FocusSession(FocusKind.Break, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(3), null, null) });

        Assert.Contains("\"Break\"", File.ReadAllText(FocusPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
