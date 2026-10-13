using System.Text.Json;

namespace Mithu.Core.ClaudeCode;

/// <summary>What a Claude Code session is doing.</summary>
public enum ClaudeState
{
    /// <summary>Open, nothing asked yet (or done a while ago).</summary>
    Idle,

    /// <summary>Working out what to do next (between tools).</summary>
    Thinking,

    /// <summary>Using a tool: editing, running a command, reading, searching…</summary>
    Working,

    /// <summary>Waiting for the user's permission.</summary>
    NeedsYou,

    /// <summary>Finished its answer.</summary>
    Done,
}

/// <summary>One Claude Code hook event, boiled down to what Mithu shows (nothing else is kept).</summary>
public sealed record ClaudeEvent(string SessionId, string Project, string Name, string? Activity, string? ToolUseId, bool IsEdit, bool IsCommand, string? NotificationType, string? TranscriptPath = null);

/// <summary>
/// Reads Claude Code hook events (the JSON Claude Code sends: <c>hook_event_name</c>, <c>session_id</c>, <c>cwd</c>,
/// <c>tool_name</c>, <c>tool_input</c>…) and describes tools in a few words ("Editing App.xaml.cs", "Running dotnet
/// test"). Pure; unit-tested. Prompts, file contents and command output are never kept.
/// </summary>
public static class ClaudeCodeEvents
{
    private const int MaxActivity = 60;

    /// <summary>The event, or null when it isn't one (bad JSON, no session).</summary>
    public static ClaudeEvent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "session_id") is not { Length: > 0 } session || Str(root, "hook_event_name") is not { Length: > 0 } name)
            {
                return null;
            }

            var cwd = Str(root, "cwd") ?? string.Empty;
            var project = System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
            var tool = Str(root, "tool_name");
            var input = root.TryGetProperty("tool_input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
            string? activity = name switch
            {
                "PreToolUse" or "PermissionRequest" when tool is not null => Describe(tool, input),
                "SubagentStart" => "A helper agent is working",
                _ => null,
            };
            return new ClaudeEvent(
                session,
                project.Length > 0 ? project : "Claude Code",
                name,
                activity,
                Str(root, "tool_use_id"),
                IsEdit: name == "PreToolUse" && tool is "Edit" or "Write" or "MultiEdit" or "NotebookEdit",
                IsCommand: name == "PreToolUse" && tool is "Bash" or "PowerShell",
                Str(root, "notification_type"),
                Str(root, "transcript_path"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A transcript line's reply id and output tokens (Claude Code writes one line per part of a reply, each with
    /// the same usage), or null for anything else. Only the numbers are read.
    /// </summary>
    public static (string MessageId, int OutputTokens)? Usage(string line)
    {
        if (!line.Contains("\"output_tokens\"", StringComparison.Ordinal) || !line.Contains("\"assistant\"", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && Str(root, "type") == "assistant"
                && root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
                && Str(message, "id") is { Length: > 0 } id
                && message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var tokens)
                ? (id, tokens)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A tool in a few words.</summary>
    public static string Describe(string tool, JsonElement input)
    {
        string? S(string name) => input.ValueKind == JsonValueKind.Object ? Str(input, name) : null;
        string File(string name) => System.IO.Path.GetFileName((S(name) ?? string.Empty).TrimEnd('\\', '/')) is { Length: > 0 } f ? f : "a file";
        var text = tool switch
        {
            "Edit" or "MultiEdit" => $"Editing {File("file_path")}",
            "Write" => $"Writing {File("file_path")}",
            "Read" => $"Reading {File("file_path")}",
            "NotebookEdit" => $"Editing {File("notebook_path")}",
            "Bash" or "PowerShell" => S("description") is { Length: > 0 } d ? Doing(d) : "Running " + OneLine(S("command") ?? "a command"),
            "Grep" => S("pattern") is { Length: > 0 } p ? $"Searching for “{OneLine(p)}”" : "Searching the code",
            "Glob" => "Finding files",
            "WebFetch" => Uri.TryCreate(S("url"), UriKind.Absolute, out var url) ? $"Reading {url.Host}" : "Reading a web page",
            "WebSearch" => "Searching the web",
            "Agent" or "Task" => "Helper agent: " + (S("description") is { Length: > 0 } a ? Lower(a) : "working"),
            "TodoWrite" => "Updating its plan",
            "ExitPlanMode" => "Finishing its plan",
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "Using " + tool.Split("__").Last().Replace('_', ' '),
            _ => $"Using {tool}",
        };
        return text.Length > MaxActivity ? text[..(MaxActivity - 1)].TrimEnd() + "…" : text;
    }

    /// <summary>"Run the tests" → "Running the tests"; other descriptions as they are ("Install packages").</summary>
    private static string Doing(string description)
    {
        var line = OneLine(description);
        return line.StartsWith("Run ", StringComparison.OrdinalIgnoreCase) ? "Running " + line[4..] : char.ToUpperInvariant(line[0]) + line[1..];
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>"Run test suite" → "run test suite" (it follows "Running").</summary>
    private static string Lower(string text)
    {
        var line = OneLine(text);
        return line.Length > 1 && char.IsUpper(line[0]) && !char.IsUpper(line[1]) ? char.ToLowerInvariant(line[0]) + line[1..] : line;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A Claude Code session as Mithu sees it.</summary>
public sealed class ClaudeSession(string id, string project, DateTimeOffset nowUtc)
{
    public string Id { get; } = id;

    public string Project { get; set; } = project;

    public ClaudeState State { get; set; } = ClaudeState.Idle;

    /// <summary>"Editing App.xaml.cs", "Thinking…", "Needs your permission"…</summary>
    public string Activity { get; set; } = "Ready";

    /// <summary>When the current request started (for "4 min").</summary>
    public DateTimeOffset TurnStartUtc { get; set; } = nowUtc;

    public DateTimeOffset LastUtc { get; set; } = nowUtc;

    public DateTimeOffset? DoneUtc { get; set; }

    public int Edits { get; set; }

    public int Commands { get; set; }

    /// <summary>Its transcript file (for the token count).</summary>
    public string? TranscriptPath { get; set; }

    /// <summary>Tokens Claude has written (thinking, text, tool calls) for the current request.</summary>
    public int TurnTokens => TurnReplies.Values.Sum();

    /// <summary>Tool calls that already finished (async events can arrive out of order).</summary>
    internal HashSet<string> FinishedTools { get; } = [];

    /// <summary>This request's replies: id → output tokens.</summary>
    internal Dictionary<string, int> TurnReplies { get; } = [];
}

/// <summary>
/// Follows Claude Code sessions from their hook events: Thinking while it works out what to do, Working while a
/// tool runs (with what it's doing), Needs you at a permission prompt, Done when it finishes. Events can arrive
/// slightly out of order (the hooks run in the background), so a tool that already finished never comes back as
/// running. Pure; unit-tested.
/// </summary>
public sealed class ClaudeTracker
{
    /// <summary>A finished session stays on show this long.</summary>
    public static readonly TimeSpan ShowDoneFor = TimeSpan.FromMinutes(10);

    /// <summary>A session that's gone quiet this long (Claude Code closed without saying) is forgotten.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(2);

    private readonly Dictionary<string, ClaudeSession> _sessions = [];

    /// <summary>Applies an event; returns the session and its new state when the state changed (else null).</summary>
    public (ClaudeSession? Session, ClaudeState? NewState) Apply(ClaudeEvent e, DateTimeOffset nowUtc)
    {
        if (e.Name == "SessionEnd")
        {
            _sessions.Remove(e.SessionId);
            return (null, null);
        }

        if (!_sessions.TryGetValue(e.SessionId, out var s))
        {
            s = new ClaudeSession(e.SessionId, e.Project, nowUtc);
            _sessions[e.SessionId] = s;
        }

        s.Project = e.Project;
        s.LastUtc = nowUtc;
        s.TranscriptPath = e.TranscriptPath ?? s.TranscriptPath;
        var before = s.State;
        switch (e.Name)
        {
            case "SessionStart":
                if (s.State is ClaudeState.Done)
                {
                    s.State = ClaudeState.Idle;
                    s.Activity = "Ready";
                }

                break;
            case "UserPromptSubmit":
                s.State = ClaudeState.Thinking;
                s.Activity = "Thinking…";
                s.TurnStartUtc = nowUtc;
                s.DoneUtc = null;
                s.Edits = 0;
                s.Commands = 0;
                s.FinishedTools.Clear();
                s.TurnReplies.Clear();
                break;
            case "PreToolUse":
                if (e.ToolUseId is { } id && s.FinishedTools.Contains(id))
                {
                    break; // already finished: its "started" came late
                }

                StartWorkIfIdle(s, nowUtc);
                s.State = ClaudeState.Working;
                s.Activity = e.Activity ?? "Working";
                s.Edits += e.IsEdit ? 1 : 0;
                s.Commands += e.IsCommand ? 1 : 0;
                break;
            case "PostToolUse" or "PostToolUseFailure" or "PostToolBatch":
                if (e.ToolUseId is { } done)
                {
                    s.FinishedTools.Add(done);
                }

                if (s.State is ClaudeState.Working or ClaudeState.NeedsYou)
                {
                    s.State = ClaudeState.Thinking;
                    s.Activity = "Thinking…";
                }

                break;
            case "PermissionRequest":
                s.State = ClaudeState.NeedsYou;
                s.Activity = e.Activity is { } what ? $"Needs your OK: {what}" : "Needs your permission";
                break;
            case "Notification" when e.NotificationType == "permission_prompt":
                if (s.State != ClaudeState.NeedsYou)
                {
                    s.State = ClaudeState.NeedsYou;
                    s.Activity = "Needs your permission";
                }

                break;
            case "SubagentStart":
                StartWorkIfIdle(s, nowUtc);
                s.State = ClaudeState.Working;
                s.Activity = e.Activity ?? "A helper agent is working";
                break;
            case "Stop" or "StopFailure":
                s.State = ClaudeState.Done;
                s.DoneUtc = nowUtc;
                s.Activity = e.Name == "StopFailure" ? "Stopped (an error)" : Summary(s, nowUtc);
                break;
        }

        return (s, s.State != before ? s.State : null);
    }

    /// <summary>Sessions to show: working ones first, then recently finished ones; old ones are forgotten.</summary>
    public IReadOnlyList<ClaudeSession> Visible(DateTimeOffset nowUtc)
    {
        foreach (var gone in _sessions.Values.Where(s => nowUtc - s.LastUtc > ForgetAfter).ToList())
        {
            _sessions.Remove(gone.Id);
        }

        return _sessions.Values
            .Where(s => s.State is ClaudeState.Thinking or ClaudeState.Working or ClaudeState.NeedsYou
                || (s.State == ClaudeState.Done && s.DoneUtc is { } d && nowUtc - d < ShowDoneFor))
            .OrderBy(s => s.State switch { ClaudeState.NeedsYou => 0, ClaudeState.Working or ClaudeState.Thinking => 1, _ => 2 })
            .ThenByDescending(s => s.LastUtc)
            .ToList();
    }

    /// <summary>"Finished · 4:12 · 12.3k tokens · 12 edits, 3 commands".</summary>
    public static string Summary(ClaudeSession s, DateTimeOffset nowUtc)
    {
        var parts = new List<string> { "Finished", Progress(s, nowUtc) };
        var work = new List<string>();
        if (s.Edits > 0)
        {
            work.Add($"{s.Edits} edit{(s.Edits == 1 ? string.Empty : "s")}");
        }

        if (s.Commands > 0)
        {
            work.Add($"{s.Commands} command{(s.Commands == 1 ? string.Empty : "s")}");
        }

        if (work.Count > 0)
        {
            parts.Add(string.Join(", ", work));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"under a minute", "4 min", "1 h 20 min".</summary>
    public static string Elapsed(TimeSpan time) =>
        time < TimeSpan.FromMinutes(1) ? "under a minute"
        : time < TimeSpan.FromHours(1) ? $"{(int)time.TotalMinutes} min"
        : $"{(int)time.TotalHours} h {time.Minutes} min";

    /// <summary>Work without a prompt event (e.g. the hooks were added mid-request): the clock starts now.</summary>
    private static void StartWorkIfIdle(ClaudeSession s, DateTimeOffset nowUtc)
    {
        if (s.State is ClaudeState.Idle or ClaudeState.Done)
        {
            s.TurnStartUtc = nowUtc;
            s.DoneUtc = null;
            s.Edits = 0;
            s.Commands = 0;
            s.TurnReplies.Clear();
        }
    }

    /// <summary>The session with this id, if Mithu is following it.</summary>
    public ClaudeSession? Find(string id) => _sessions.GetValueOrDefault(id);

    /// <summary>Counts the tokens in new transcript lines towards the current request.</summary>
    public static void AddTranscript(ClaudeSession s, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (ClaudeCodeEvents.Usage(line) is { } usage)
            {
                s.TurnReplies[usage.MessageId] = usage.OutputTokens; // the same reply repeats: last one wins
            }
        }
    }

    /// <summary>"0:42", "4:12", "1:02:05": a running clock.</summary>
    public static string Clock(TimeSpan time) =>
        time < TimeSpan.Zero ? "0:00"
        : time < TimeSpan.FromHours(1) ? $"{(int)time.TotalMinutes}:{time.Seconds:00}"
        : $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}";

    /// <summary>"850 tokens", "3.4k tokens", "120k tokens", "1.2M tokens".</summary>
    public static string Tokens(int tokens) =>
        tokens < 1000 ? $"{tokens} tokens"
        : tokens < 100_000 ? $"{(tokens / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}k tokens"
        : tokens < 1_000_000 ? $"{tokens / 1000}k tokens"
        : $"{(tokens / 1_000_000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}M tokens";

    /// <summary>"1:42 · 3.4k tokens" (no tokens yet: just the clock).</summary>
    public static string Progress(ClaudeSession s, DateTimeOffset nowUtc) =>
        s.TurnTokens > 0 ? $"{Clock(nowUtc - s.TurnStartUtc)} · {Tokens(s.TurnTokens)}" : Clock(nowUtc - s.TurnStartUtc);
}
