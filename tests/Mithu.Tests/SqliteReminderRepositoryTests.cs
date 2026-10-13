using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mithu.Core.Reminders;
using Mithu.Infrastructure.Persistence;

namespace Mithu.Tests;

public sealed class SqliteReminderRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    private SqliteReminderRepository NewRepository() =>
        new(new SqliteDatabase(Path.Combine(_dir, "mithu.db"), NullLogger<SqliteDatabase>.Instance));

    [Fact]
    public void Round_trips_every_field()
    {
        var repo = NewRepository();
        var r = Reminder.Create("Call Mom", "Ask about Sunday", new DateTime(2026, 10, 13, 18, 30, 0), Zone,
            Repeat.Custom(3, RepeatUnit.Weeks), ReminderPriority.Urgent, Now) with
        {
            SnoozedUntilUtc = Now.AddMinutes(10),
            NotifiedUtc = Now,
        };

        repo.Save(r);
        SqliteConnection.ClearAllPools();
        var loaded = NewRepository().Get(r.Id)!;

        Assert.Equal(r, loaded);
    }

    [Fact]
    public void Saves_several_at_once_and_deletes()
    {
        var repo = NewRepository();
        var r = Reminder.Create("Stretch", "", new DateTime(2026, 10, 12, 16, 0, 0), Zone, Repeat.Daily, ReminderPriority.Normal, Now);
        var (updated, history) = r.Finish(ReminderStatus.Completed, Zone, Now.AddHours(2));

        repo.Save(updated, history!);
        Assert.Equal(2, repo.GetAll().Count);

        repo.Delete(history!.Id);
        Assert.Equal([updated.Id], repo.GetAll().Select(x => x.Id));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
