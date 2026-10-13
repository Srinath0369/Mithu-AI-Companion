using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Mithu.Core.ClaudeCode;
using Mithu.Infrastructure.ClaudeCode;

namespace Mithu.Tests;

public class ClaudeCodeEventTests
{
    private static string Hook(string name, string? tool = null, string? input = null, string? id = null, string? notification = null) =>
        $$"""
        {"session_id":"s1","cwd":"C:\\Users\\me\\Desktop\\Companion","hook_event_name":"{{name}}"
         {{(tool is null ? "" : $",\"tool_name\":\"{tool}\"")}}
         {{(input is null ? "" : $",\"tool_input\":{input}")}}
         {{(id is null ? "" : $",\"tool_use_id\":\"{id}\"")}}
         {{(notification is null ? "" : $",\"notification_type\":\"{notification}\"")}}}
        """;

    [Theory]
    [InlineData("Edit", """{"file_path":"C:\\src\\App.xaml.cs","old_string":"a","new_string":"b"}""", "Editing App.xaml.cs")]
    [InlineData("Write", """{"file_path":"C:\\src\\New.cs","content":"secret"}""", "Writing New.cs")]
    [InlineData("Read", """{"file_path":"C:\\src\\Notes.md"}""", "Reading Notes.md")]
    [InlineData("Bash", """{"command":"dotnet test","description":"Run the tests"}""", "Running the tests")]
    [InlineData("PowerShell", """{"command":"dotnet  build\n -c Release"}""", "Running dotnet build -c Release")]
    [InlineData("Grep", """{"pattern":"TODO"}""", "Searching for “TODO”")]
    [InlineData("WebFetch", """{"url":"https://code.claude.com/docs/en/hooks"}""", "Reading code.claude.com")]
    [InlineData("Agent", """{"description":"Explore the codebase","subagent_type":"Explore"}""", "Helper agent: explore the codebase")]
    [InlineData("mcp__github__create_issue", "{}", "Using create issue")]
    public void Tools_are_described_in_a_few_words(string tool, string input, string expected)
    {
        var e = ClaudeCodeEvents.Parse(Hook("PreToolUse", tool, input));
        Assert.Equal(expected, e!.Activity);
        Assert.Equal("Companion", e.Project);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"hook_event_name":"Stop"}""")]           // no session
    [InlineData("[1,2]")]
    public void Anything_else_is_ignored(string body) => Assert.Null(ClaudeCodeEvents.Parse(body));

    [Fact]
    public void A_request_goes_thinking_working_needs_you_and_done()
    {
        var tracker = new ClaudeTracker();
        var t0 = new DateTimeOffset(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);
        ClaudeState? Apply(string json, int seconds) => tracker.Apply(ClaudeCodeEvents.Parse(json)!, t0.AddSeconds(seconds)).NewState;

        Apply(Hook("SessionStart"), 0);
        Assert.Empty(tracker.Visible(t0)); // open but idle: nothing to show

        Assert.Equal(ClaudeState.Thinking, Apply(Hook("UserPromptSubmit"), 1));
        Assert.Equal(ClaudeState.Working, Apply(Hook("PreToolUse", "Edit", """{"file_path":"C:\\a\\One.cs"}""", "t1"), 5));
        Assert.Equal("Editing One.cs", tracker.Visible(t0).Single().Activity);
        Assert.Equal(ClaudeState.Thinking, Apply(Hook("PostToolUse", "Edit", null, "t1"), 6));
        Apply(Hook("PreToolUse", "Bash", """{"command":"dotnet test"}""", "t2"), 7);
        Assert.Equal(ClaudeState.NeedsYou, Apply(Hook("PermissionRequest", "Bash", """{"command":"dotnet test"}"""), 8));
        Assert.Equal("Needs your OK: Running dotnet test", tracker.Visible(t0).Single().Activity); // no description: the command
        Apply(Hook("PostToolUse", "Bash", null, "t2"), 30);
        Apply(Hook("PreToolUse", "Edit", """{"file_path":"C:\\a\\Two.cs"}""", "t3"), 40);
        Assert.Equal(ClaudeState.Done, Apply(Hook("Stop"), 241));

        var done = tracker.Visible(t0.AddSeconds(241)).Single();
        Assert.Equal("Finished · 4:00 · 2 edits, 1 command", done.Activity);
        Assert.Empty(tracker.Visible(t0.AddSeconds(241) + ClaudeTracker.ShowDoneFor)); // gone after a while

        Apply(Hook("SessionEnd"), 300);
        Assert.Empty(tracker.Visible(t0.AddSeconds(300)));
    }

    [Fact]
    public void A_tool_that_already_finished_never_comes_back_as_running()
    {
        // The hooks run in the background, so "done" can arrive before "started".
        var tracker = new ClaudeTracker();
        var now = DateTimeOffset.UnixEpoch;
        tracker.Apply(ClaudeCodeEvents.Parse(Hook("UserPromptSubmit"))!, now);
        tracker.Apply(ClaudeCodeEvents.Parse(Hook("PostToolUse", "Read", null, "fast"))!, now);
        tracker.Apply(ClaudeCodeEvents.Parse(Hook("PreToolUse", "Read", """{"file_path":"x.cs"}""", "fast"))!, now);
        Assert.Equal(ClaudeState.Thinking, tracker.Visible(now).Single().State);
    }

    [Fact]
    public void Tokens_are_counted_per_request_from_the_transcript()
    {
        var tracker = new ClaudeTracker();
        var t0 = DateTimeOffset.UnixEpoch;
        static string Reply(string id, int output, string part = "text") =>
            $$"""{"type":"assistant","message":{"id":"{{id}}","content":[{"type":"{{part}}"}],"usage":{"input_tokens":2,"cache_read_input_tokens":90000,"output_tokens":{{output}}""" + "}}}";

        var (s, _) = tracker.Apply(ClaudeCodeEvents.Parse(Hook("UserPromptSubmit"))!, t0);
        ClaudeTracker.AddTranscript(s!, [
            """{"type":"user","message":{"content":"output_tokens assistant"}}""", // not a reply
            Reply("m1", 400, "thinking"), Reply("m1", 400), // one reply, written in two parts
            Reply("m2", 1300), "{ broken",
        ]);
        Assert.Equal(1700, s!.TurnTokens);
        Assert.Equal("1:05 · 1.7k tokens", ClaudeTracker.Progress(s, t0.AddSeconds(65)));

        tracker.Apply(ClaudeCodeEvents.Parse(Hook("Stop"))!, t0.AddSeconds(65));
        Assert.Equal("Finished · 1:05 · 1.7k tokens", s.Activity);

        tracker.Apply(ClaudeCodeEvents.Parse(Hook("UserPromptSubmit"))!, t0.AddSeconds(90));
        Assert.Equal(0, s.TurnTokens); // a new request starts from zero
    }

    [Theory]
    [InlineData(850, "850 tokens")]
    [InlineData(3420, "3.4k tokens")]
    [InlineData(12000, "12k tokens")]
    [InlineData(245_600, "245k tokens")]
    [InlineData(1_240_000, "1.2M tokens")]
    public void Token_counts_read_short(int tokens, string expected) => Assert.Equal(expected, ClaudeTracker.Tokens(tokens));

    [Fact]
    public void The_clock_reads_like_a_stopwatch()
    {
        Assert.Equal("0:07", ClaudeTracker.Clock(TimeSpan.FromSeconds(7)));
        Assert.Equal("4:12", ClaudeTracker.Clock(TimeSpan.FromSeconds(252)));
        Assert.Equal("1:02:05", ClaudeTracker.Clock(new TimeSpan(1, 2, 5)));
    }

    [Fact]
    public void Elapsed_reads_naturally()
    {
        Assert.Equal("under a minute", ClaudeTracker.Elapsed(TimeSpan.FromSeconds(40)));
        Assert.Equal("12 min", ClaudeTracker.Elapsed(TimeSpan.FromMinutes(12.7)));
        Assert.Equal("1 h 5 min", ClaudeTracker.Elapsed(TimeSpan.FromMinutes(65)));
    }
}

public sealed class ClaudeCodeHooksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"));

    private string SettingsFile => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Installing_keeps_the_users_settings_and_hooks_and_uninstalling_takes_out_only_mithus()
    {
        Directory.CreateDirectory(_dir);
        const string original = """
            {
              "model": "opus",
              "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "my-guard.sh" } ] } ]
              }
            }
            """;
        File.WriteAllText(SettingsFile, original);
        var hooks = new ClaudeCodeHooks(SettingsFile);

        hooks.Install("abc123");
        Assert.True(hooks.IsInstalled("abc123"));
        Assert.False(hooks.IsInstalled("other"));
        Assert.Equal(original, File.ReadAllText(SettingsFile + ".mithu-backup")); // a copy of how it was

        var json = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.Equal("opus", (string?)json["model"]);
        var pre = json["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal("my-guard.sh", (string?)pre[0]!["hooks"]![0]!["command"]); // the user's hook is untouched
        var mine = pre[1]!["hooks"]![0]!;
        Assert.True((bool)mine["async"]!);                                  // never blocks Claude Code
        Assert.EndsWith("/mithu/claude-code/abc123", (string?)mine["command"]);
        Assert.All(ClaudeCodeHooks.Events, e => Assert.NotNull(json["hooks"]![e]));

        hooks.Install("abc123"); // again: no duplicates
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(SettingsFile))!["hooks"]!["PreToolUse"]!.AsArray().Count);

        hooks.Uninstall();
        var after = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.Equal("opus", (string?)after["model"]);
        var left = after["hooks"]!.AsObject();
        Assert.Equal(["PreToolUse"], left.Select(p => p.Key));
        Assert.Equal("my-guard.sh", (string?)left["PreToolUse"]![0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public void A_missing_file_is_created_and_a_broken_one_left_alone()
    {
        var hooks = new ClaudeCodeHooks(SettingsFile);
        hooks.Uninstall(); // nothing there: nothing happens
        hooks.Install("t0k3n");
        Assert.True(hooks.IsInstalled("t0k3n"));
        hooks.Uninstall();
        Assert.Equal("{}", File.ReadAllText(SettingsFile).Trim());

        File.WriteAllText(SettingsFile, "{ this isn't json");
        Assert.Throws<InvalidDataException>(() => hooks.Install("t0k3n"));
        Assert.Equal("{ this isn't json", File.ReadAllText(SettingsFile));
    }

    [Fact]
    public async Task The_listener_takes_events_with_the_secret_only()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var listener = new ClaudeCodeListener(NullLogger<ClaudeCodeListener>.Instance);
        var received = new TaskCompletionSource<string>();
        listener.Received += body => received.TrySetResult(body);
        Assert.True(listener.Start(port, ClaudeCodeHooks.PathFor("s3cret")));

        using var http = new HttpClient();
        var wrong = await http.PostAsync($"http://127.0.0.1:{port}/mithu/claude-code/guess", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, wrong.StatusCode);

        var body = """{"session_id":"s","hook_event_name":"Stop","cwd":"C:\\x"}""";
        var right = await http.PostAsync($"http://127.0.0.1:{port}/mithu/claude-code/s3cret", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.True(right.IsSuccessStatusCode);
        Assert.Equal(body, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
    [Fact]
    public void The_transcript_is_followed_from_where_mithu_first_saw_it()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "session.jsonl");
        File.WriteAllText(file, "old line 1\nold line 2\n");
        var tail = new TranscriptTail();

        Assert.Empty(tail.ReadNew(file));              // earlier requests don't count
        File.AppendAllText(file, "new 1\nnew 2\npart");
        Assert.Equal(["new 1", "new 2"], tail.ReadNew(file));
        File.AppendAllText(file, "ial\n");
        Assert.Equal(["partial"], tail.ReadNew(file)); // a half-written line waits for its end
        Assert.Empty(tail.ReadNew(file));
        Assert.Empty(tail.ReadNew(Path.Combine(_dir, "settings.json"))); // only transcripts
    }

    [Fact]
    public void Utf16_bodies_from_some_shells_are_read_too()
    {
        const string json = """{"session_id":"s","hook_event_name":"Stop"}""";
        Assert.Equal(json, ClaudeCodeListener.Decode(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(json, ClaudeCodeListener.Decode(Encoding.Unicode.GetBytes(json)));
        Assert.Equal(json, ClaudeCodeListener.Decode([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(json)]));
        Assert.Equal(json, ClaudeCodeListener.Decode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]));
    }
}