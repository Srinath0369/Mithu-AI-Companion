using Mithu.Core.Desktop;
using Mithu.Core.Vision;
using Mithu.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;

namespace Mithu.Tests;

public class FileOpsAndLayoutTests
{
    [Theory]
    [InlineData("report.pdf", null)]
    [InlineData("", "empty")]
    [InlineData("a/b", "can't contain")]
    [InlineData("what?", "can't contain")]
    [InlineData("CON.txt", "reserved")]
    [InlineData("name.", "end")]
    public void Bad_names_are_explained(string name, string? problem)
    {
        var why = FileNames.WhyBadName(name);
        if (problem is null)
        {
            Assert.Null(why);
        }
        else
        {
            Assert.Contains(problem, why);
        }
    }

    [Fact]
    public void Clashing_names_get_a_number()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\p\a.png", @"C:\p\a (2).png" };
        Assert.Equal(@"C:\p\a (3).png", FileNames.FreeName(@"C:\p", "a.png", taken.Contains));
        Assert.Equal(@"C:\p\b.png", FileNames.FreeName(@"C:\p", "b.png", taken.Contains));
        Assert.Equal("a, b, c … (+2 more)", FileNames.Preview(["a", "b", "c", "d", "e"]));
    }

    [Fact]
    public void Halves_and_quarters_of_the_work_area()
    {
        var work = new CaptureRect(0, 0, 1920, 1040);
        Assert.Equal(new CaptureRect(0, 0, 960, 1040), WindowLayout.Target(work, WindowPlace.Left));
        Assert.Equal(new CaptureRect(960, 0, 960, 1040), WindowLayout.Target(work, WindowPlace.Right));
        Assert.Equal(new CaptureRect(960, 520, 960, 520), WindowLayout.Target(work, WindowPlace.BottomRight));
        Assert.Null(WindowLayout.Target(work, WindowPlace.Maximize));
        Assert.Equal(WindowPlace.TopLeft, WindowLayout.Parse("top left"));
        Assert.Equal(WindowPlace.Maximize, WindowLayout.Parse("full screen"));
        Assert.Null(WindowLayout.Parse("sideways"));
    }

    [Fact]
    public void Invisible_borders_are_added_so_the_visible_frame_lands_exactly()
    {
        // Windows 11: 7 px invisible borders left, right and bottom.
        var window = new CaptureRect(93, 100, 814, 607);
        var frame = new CaptureRect(100, 100, 800, 600);
        var target = WindowLayout.WithBorders(new CaptureRect(0, 0, 960, 1040), window, frame);
        Assert.Equal(new CaptureRect(-7, 0, 974, 1047), target);
    }

    [Fact]
    public void Actions_are_logged_for_undo_and_marked_when_undone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SqliteActionLog(new SqliteDatabase(Path.Combine(dir, "mithu.db"), NullLogger<SqliteDatabase>.Instance));
            var now = new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);
            var id = log.Add("chat1", "call1", "Moved 2 files → Pictures", [new FileOp(FileOpKind.Move, @"C:\a\x.png", @"C:\b\x.png")], now);

            var saved = Assert.Single(log.ForChat("chat1"));
            Assert.Equal(("call1", "Moved 2 files → Pictures", false), (saved.CallId, saved.Summary, saved.Undone));
            Assert.Equal(FileOpKind.Move, saved.Ops.Single().Kind);
            Assert.Equal(now, saved.CreatedUtc);

            log.MarkUndone(id);
            Assert.True(log.Get(id)!.Undone);
            Assert.Empty(log.ForChat("other"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}