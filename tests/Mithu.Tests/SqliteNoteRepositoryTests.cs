using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mithu.Core.Notes;
using Mithu.Infrastructure.Persistence;

namespace Mithu.Tests;

public sealed class SqliteNoteRepositoryTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "mithu.db");

    private SqliteDatabase NewDatabase() => new(DbPath, NullLogger<SqliteDatabase>.Instance);

    private SqliteNoteRepository NewRepository() => new(NewDatabase());

    private static Note Make(string title, string content = "", int minutes = 0, params string[] tags) =>
        Note.Create(T0.AddMinutes(minutes), title, content, tags);

    [Fact]
    public void Saves_and_reads_back_a_note()
    {
        var repo = NewRepository();
        var note = Make("Plan", "- [ ] write\n**bold**", 0, "work", "ideas");

        repo.Save(note);
        var loaded = repo.Get(note.Id)!;

        Assert.Equal(note.Title, loaded.Title);
        Assert.Equal(note.Content, loaded.Content);
        Assert.Equal(["ideas", "work"], loaded.Tags);
        Assert.Equal(note.CreatedUtc, loaded.CreatedUtc);
        Assert.Equal(note.UpdatedUtc, loaded.UpdatedUtc);
    }

    [Fact]
    public void Survives_a_restart()
    {
        var note = Make("Kept");
        NewRepository().Save(note);
        SqliteConnection.ClearAllPools();

        Assert.NotNull(NewRepository().Get(note.Id));
    }

    [Fact]
    public void Update_changes_content_tags_and_time_but_not_creation()
    {
        var repo = NewRepository();
        var note = Make("Plan", "v1", 0, "work");
        repo.Save(note);

        repo.Save(note with { Content = "v2", Tags = ["home"], UpdatedUtc = T0.AddHours(1) });
        var loaded = repo.Get(note.Id)!;

        Assert.Equal("v2", loaded.Content);
        Assert.Equal(["home"], loaded.Tags);
        Assert.Equal(T0, loaded.CreatedUtc);
        Assert.Equal(T0.AddHours(1), loaded.UpdatedUtc);
    }

    [Fact]
    public void Delete_removes_the_note_its_tags_and_search_entry()
    {
        var repo = NewRepository();
        var note = Make("Secret plan", "", 0, "work");
        repo.Save(note);

        repo.Delete(note.Id);

        Assert.Null(repo.Get(note.Id));
        Assert.Empty(repo.GetTags());
        Assert.Empty(repo.Find(new NoteQuery("secret")));
    }

    [Fact]
    public void Deleted_note_can_be_restored_by_saving_it_again()
    {
        var repo = NewRepository();
        var note = Make("Oops", "", 0, "work");
        repo.Save(note);
        repo.Delete(note.Id);

        repo.Save(note);

        Assert.Equal(["work"], repo.Get(note.Id)!.Tags);
    }

    [Fact]
    public void Search_matches_word_prefixes_in_title_and_content()
    {
        var repo = NewRepository();
        var report = Make("Quarterly report", "numbers");
        var groceries = Make("Groceries", "buy milk and bread");
        repo.Save(report);
        repo.Save(groceries);

        Assert.Equal([report.Id], repo.Find(new NoteQuery("rep")).Select(n => n.Id));
        Assert.Equal([groceries.Id], repo.Find(new NoteQuery("MILK")).Select(n => n.Id));
        Assert.Equal([groceries.Id], repo.Find(new NoteQuery("bu mi")).Select(n => n.Id)); // every word must match
        Assert.Empty(repo.Find(new NoteQuery("milk report")));
    }

    [Fact]
    public void Search_ignores_formatting_and_odd_characters()
    {
        var repo = NewRepository();
        var note = Make("", "- [ ] **Call** mom");
        repo.Save(note);

        Assert.Single(repo.Find(new NoteQuery("call")));
        Assert.Single(repo.Find(new NoteQuery("\"call* (mom)")));
        Assert.Single(repo.Find(new NoteQuery("  "))); // blank search = everything
    }

    [Fact]
    public void Search_finds_tags_too()
    {
        var repo = NewRepository();
        var note = Make("Plan", "", 0, "homework");
        repo.Save(note);

        Assert.Single(repo.Find(new NoteQuery("#home")));
    }

    [Fact]
    public void Filters_by_tag()
    {
        var repo = NewRepository();
        repo.Save(Make("A", "", 0, "work"));
        repo.Save(Make("B", "", 1, "home"));
        repo.Save(Make("C", "", 2, "work", "home"));

        Assert.Equal(["C", "A"], repo.Find(new NoteQuery(Tag: "work")).Select(n => n.Title));
        Assert.Equal(["C", "B"], repo.Find(new NoteQuery(Tag: "#Home")).Select(n => n.Title));
    }

    [Fact]
    public void Lists_tags_by_use()
    {
        var repo = NewRepository();
        repo.Save(Make("A", "", 0, "work"));
        repo.Save(Make("B", "", 1, "home", "work"));

        Assert.Equal([new TagCount("work", 2), new TagCount("home", 1)], repo.GetTags());
    }

    [Fact]
    public void Sorts_by_last_modified_newest_or_oldest()
    {
        var repo = NewRepository();
        var a = Make("A", "", 0);
        var b = Make("B", "", 10);
        var c = Make("C", "", 20);
        repo.Save(a);
        repo.Save(b);
        repo.Save(c);
        repo.Save(a with { UpdatedUtc = T0.AddHours(1) }); // A edited last

        Assert.Equal(["A", "C", "B"], repo.Find(new NoteQuery(Sort: NoteSort.LastModified)).Select(n => n.Title));
        Assert.Equal(["C", "B", "A"], repo.Find(new NoteQuery(Sort: NoteSort.Newest)).Select(n => n.Title));
        Assert.Equal(["A", "B", "C"], repo.Find(new NoteQuery(Sort: NoteSort.Oldest)).Select(n => n.Title));
    }

    [Fact]
    public void Pinned_notes_come_first_in_every_order()
    {
        var repo = NewRepository();
        repo.Save(Make("Old pinned", "", 0) with { IsPinned = true });
        repo.Save(Make("New", "", 30));

        Assert.Equal("Old pinned", repo.Find(new NoteQuery(Sort: NoteSort.Newest))[0].Title);
        Assert.Equal("Old pinned", repo.Find(new NoteQuery(Sort: NoteSort.Oldest))[0].Title);
    }

    [Fact]
    public void Archived_notes_leave_the_main_list_and_search()
    {
        var repo = NewRepository();
        var kept = Make("Kept milk", "", 0, "home");
        var old = Make("Old milk", "", 1, "home", "old");
        repo.Save(kept);
        repo.Save(old with { IsArchived = true });

        Assert.Equal(["Kept milk"], repo.Find(new NoteQuery()).Select(n => n.Title));
        Assert.Equal(["Kept milk"], repo.Find(new NoteQuery("milk")).Select(n => n.Title));
        Assert.Equal(["Old milk"], repo.Find(new NoteQuery("milk", Archived: true)).Select(n => n.Title));
        Assert.True(repo.Get(old.Id)!.IsArchived);
    }

    [Fact]
    public void Tag_counts_follow_the_list_being_shown()
    {
        var repo = NewRepository();
        repo.Save(Make("A", "", 0, "home"));
        repo.Save(Make("B", "", 1, "home", "old") with { IsArchived = true });

        Assert.Equal([new TagCount("home", 1)], repo.GetTags());
        Assert.Equal([new TagCount("home", 1), new TagCount("old", 1)], repo.GetTags(archived: true));
    }

    [Fact]
    public void Unarchiving_brings_a_note_back()
    {
        var repo = NewRepository();
        var note = Make("Back again") with { IsArchived = true };
        repo.Save(note);

        repo.Save(note with { IsArchived = false });

        Assert.Single(repo.Find(new NoteQuery()));
        Assert.Empty(repo.Find(new NoteQuery(Archived: true)));
    }

    [Fact]
    public void Version_1_database_is_backed_up_and_upgraded_keeping_notes()
    {
        // Build a v1 database the way the previous release left it.
        Directory.CreateDirectory(_dir);
        using (var v1 = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            v1.Open();
            using var command = v1.CreateCommand();
            command.CommandText = Migrations.Scripts[0] + """

                PRAGMA user_version = 1;
                INSERT INTO notes (id, title, content, is_pinned, created_at_utc, updated_at_utc)
                VALUES ('n1', 'Old note', 'from v1', 1, '2026-10-11T10:00:00.0000000Z', '2026-10-11T10:00:00.0000000Z');
                INSERT INTO note_tags (note_id, tag) VALUES ('n1', 'work');
                """;
            command.ExecuteNonQuery();
        }

        var repo = NewRepository();
        var note = repo.Get("n1")!;

        Assert.Equal("Old note", note.Title);
        Assert.True(note.IsPinned);
        Assert.False(note.IsArchived);
        Assert.Equal(["work"], note.Tags);
        Assert.Single(repo.Find(new NoteQuery("v1")));
        Assert.True(File.Exists(DbPath + ".bak-v1"));
    }

    [Fact]
    public void Fresh_database_is_at_the_latest_version_in_wal_mode()
    {
        var db = NewDatabase();
        using var connection = db.Open();

        Assert.Equal((long)Migrations.Scripts.Count, Scalar(connection, "PRAGMA user_version;"));
        Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, Scalar(connection, "PRAGMA foreign_keys;"));
        Assert.False(db.WasRecovered);
    }

    [Fact]
    public void Corrupt_file_is_set_aside_and_replaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(DbPath, "this is not a database, just some text that is long enough to look like a file");

        var db = NewDatabase();
        var repo = new SqliteNoteRepository(db);
        repo.Save(Make("After recovery"));

        Assert.True(db.WasRecovered);
        Assert.Single(repo.Find(new NoteQuery()));
        Assert.Single(Directory.GetFiles(_dir, "mithu.db.corrupt-*"));
    }

    [Fact]
    public void Existing_database_is_not_migrated_twice()
    {
        NewRepository().Save(Make("One"));
        SqliteConnection.ClearAllPools();

        var repo = NewRepository();
        repo.Save(Make("Two"));

        Assert.Equal(2, repo.Find(new NoteQuery()).Count);
        Assert.Empty(Directory.GetFiles(_dir, "mithu.db.bak-*")); // backups only before real upgrades
    }

    [Theory]
    [InlineData("  rep  ", new[] { "rep" })]
    [InlineData("\"quoted\" (paren) a*", new[] { "quoted", "paren", "a" })]
    [InlineData("--", new string[0])]
    public void Search_words_are_sanitised(string input, string[] expected)
    {
        Assert.Equal(expected, SqliteNoteRepository.SearchWords(input));
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
