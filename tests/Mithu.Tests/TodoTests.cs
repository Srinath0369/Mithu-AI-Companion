using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Mithu.App.Services;
using Mithu.App.ViewModels;
using Mithu.Core.Ai;
using Mithu.Core.Todos;
using Mithu.Infrastructure.Persistence;

namespace Mithu.Tests;

public class TodoRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);

    private static TodoItem Task(string title, int order = 0, bool done = false, bool important = false) =>
        TodoItem.Create(TodoList.DefaultId, title, order, Now, important) with { Done = done, DoneUtc = done ? Now : null };

    [Fact]
    public void Starred_first_then_the_users_order_then_done()
    {
        var sorted = TodoRules.Sort([Task("c", 3), Task("done", 0, done: true), Task("a", 1), Task("star", 5, important: true)]);
        Assert.Equal(["star", "a", "c", "done"], sorted.Select(t => t.Title));
    }

    [Fact]
    public void New_tasks_go_on_top() => Assert.Equal(-3, TodoRules.TopOrder([Task("a", -2), Task("b", 4), Task("old", -9, done: true)]));

    [Theory]
    [InlineData("Finish the quarterly report", "Finish the quarterly report")]   // exact
    [InlineData("the report", "Finish the quarterly report")]                     // words
    [InlineData("REPORT", "Finish the quarterly report")]
    [InlineData("milk", null)]                                                     // two tasks: not sure
    [InlineData("dentist", null)]
    public void Finds_the_task_meant(string words, string? expected)
    {
        IReadOnlyList<TodoItem> items = [Task("Finish the quarterly report"), Task("Buy milk"), Task("Oat milk for Mom"), Task("Old report", done: true)];
        Assert.Equal(expected, TodoRules.Find(words, items)?.Title);
    }

    [Fact]
    public void Titles_are_one_tidy_line() => Assert.Equal("Buy milk and eggs", TodoItem.Clean("  Buy milk\n and   eggs "));
}

public sealed class TodoStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 9, 0, 0, TimeSpan.Zero));
    private readonly SqliteTodoRepository _repo;

    public TodoStorageTests() =>
        _repo = new SqliteTodoRepository(new SqliteDatabase(Path.Combine(_dir, "mithu.db"), NullLogger<SqliteDatabase>.Instance));

    private TodosViewModel Page() => new(_repo, _time, NullLogger<TodosViewModel>.Instance);

    [Fact]
    public void My_tasks_always_exists_and_lists_keep_their_tasks()
    {
        var inbox = Assert.Single(_repo.Lists());
        Assert.Equal((TodoList.DefaultId, TodoList.DefaultName), (inbox.Id, inbox.Name));

        var work = TodoList.Create("Work", 1, _time.GetUtcNow());
        _repo.SaveList(work);
        _repo.Save(TodoItem.Create(work.Id, "Send slides", 0, _time.GetUtcNow()));
        _repo.DeleteList(work.Id);

        var moved = Assert.Single(_repo.Items());
        Assert.Equal(TodoList.DefaultId, moved.ListId); // the task survived, in My tasks
        Assert.Throws<InvalidOperationException>(() => _repo.DeleteList(TodoList.DefaultId));
    }

    [Fact]
    public void Adding_ticking_and_starring_on_the_page()
    {
        var page = Page();
        page.NewTitle = "Buy milk";
        page.AddCommand.Execute(null);
        page.NewTitle = "Call the bank";
        page.AddCommand.Execute(null);
        Assert.Equal(["Call the bank", "Buy milk"], page.Open.Select(r => r.Title)); // newest on top
        Assert.Equal(string.Empty, page.NewTitle);
        Assert.Equal(2, page.OpenCount);

        page.StarCommand.Execute(page.Open.Single(r => r.Title == "Buy milk"));
        Assert.Equal("Buy milk", page.Open[0].Title); // starred first

        page.ToggleCommand.Execute(page.Open.Single(r => r.Title == "Call the bank"));
        Assert.Equal(["Buy milk"], page.Open.Select(r => r.Title));
        Assert.Equal("Done (1)", page.FinishedHeader);
        Assert.Equal(1, page.OpenCount);

        page.ToggleCommand.Execute(page.Finished.Single()); // back on the list
        Assert.Equal(2, page.Open.Count);
    }

    [Fact]
    public void Delete_and_clear_done_can_be_undone()
    {
        var page = Page();
        foreach (var t in new[] { "a", "b", "c" })
        {
            page.NewTitle = t;
            page.AddCommand.Execute(null);
        }

        page.DeleteCommand.Execute(page.Open.Single(r => r.Title == "b"));
        Assert.True(page.CanUndo);
        page.UndoCommand.Execute(null);
        Assert.Contains(page.Open, r => r.Title == "b");

        page.ToggleCommand.Execute(page.Open.Single(r => r.Title == "a"));
        page.ToggleCommand.Execute(page.Open.Single(r => r.Title == "c"));
        page.ClearDoneCommand.Execute(null);
        Assert.Empty(page.Finished);
        Assert.Equal("2 done tasks cleared", page.UndoText);
        page.UndoCommand.Execute(null);
        Assert.Equal(2, page.Finished.Count);
    }

    [Fact]
    public void Lists_new_rename_move_and_delete()
    {
        var page = Page();
        page.NewList("Shopping");
        Assert.Equal("Shopping", page.SelectedListName);
        page.NewTitle = "Eggs";
        page.AddCommand.Execute(null);
        Assert.Equal("Shopping", _repo.Lists().Single(l => l.Id == _repo.Items().Single().ListId).Name);

        page.RenameList("Groceries");
        Assert.Contains(_repo.Lists(), l => l.Name == "Groceries");

        page.MoveTo(page.Open.Single(), TodoList.DefaultId);
        Assert.Empty(page.Open); // gone from Groceries
        page.DeleteListCommand.Execute(null);
        Assert.True(page.IsAllView);
        Assert.Single(_repo.Lists());
        Assert.Equal("All  1", page.ListChips[0].Label);
    }

    [Fact]
    public async Task The_ai_adds_to_a_list_by_name_lists_and_ticks_tasks()
    {
        var page = Page();
        var tools = new Toolbox(null!, null!, new TodoTools(page, NullLogger<TodoTools>.Instance));
        async Task<ToolOutcome> Call(string name, object args) => await tools.RunAsync(new AiToolCall("t", name, JsonSerializer.Serialize(args)));

        var milk = await Call("add_task", new { title = "Buy milk", list = "shopping" });
        Assert.Equal("Task added · Buy milk · shopping", milk.Action);
        Assert.Contains("(new list)", milk.Result.Content);
        await Call("add_task", new { title = "Finish the quarterly report", important = true });

        var listed = await Call("list_tasks", new { });
        Assert.Contains("★ \"Finish the quarterly report\" · My tasks", listed.Result.Content);
        Assert.Contains("not instructions", listed.Result.Content);

        var done = await Call("complete_task", new { task = "the report" });
        Assert.Equal("Done ✓ · Finish the quarterly report", done.Action);
        Assert.Equal(1, page.OpenCount);

        var unsure = await Call("complete_task", new { task = "dentist" });
        Assert.True(unsure.Result.IsError);
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
