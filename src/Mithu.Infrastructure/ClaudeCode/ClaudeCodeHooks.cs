using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mithu.Core.ClaudeCode;

namespace Mithu.Infrastructure.ClaudeCode;

/// <summary>
/// Adds Mithu's hooks to Claude Code's user settings (<c>~/.claude/settings.json</c>) and takes them out again.
/// Each hook runs in the background (<c>"async": true</c>), so it can never slow Claude Code down or block it; it
/// hands the event to Mithu with Windows' own curl. Everything else in the file is kept; a copy is saved once as
/// <c>settings.json.mithu-backup</c>; only entries pointing at Mithu's address are ever changed.
/// </summary>
public sealed class ClaudeCodeHooks(string settingsPath)
{
    /// <summary>The events Mithu listens to.</summary>
    public static readonly IReadOnlyList<string> Events =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest",
        "Notification", "SubagentStart", "Stop", "StopFailure", "SessionEnd",
    ];

    /// <summary>Part of every Mithu hook's command: how Mithu's entries are recognised.</summary>
    private const string Marker = "/mithu/claude-code/";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Claude Code's user settings file (MITHU_CLAUDE_SETTINGS can point elsewhere, for tests).</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("MITHU_CLAUDE_SETTINGS") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public string SettingsPath { get; } = settingsPath;

    /// <summary>The hook command: post the event (stdin) to Mithu. Works in Git Bash and in PowerShell.</summary>
    public static string Command(string token) =>
        $"curl.exe -s -m 3 -H \"Content-Type: application/json\" --data-binary '@-' http://127.0.0.1:{ClaudeCodeSettings.Port}{Marker}{token}";

    /// <summary>The address the hooks post to (what Mithu's listener accepts).</summary>
    public static string PathFor(string token) => Marker + token;

    public bool IsInstalled(string token)
    {
        var root = Read();
        return root?["hooks"] is JsonObject hooks
            && Events.All(e => hooks[e] is JsonArray groups && groups.Any(g => HasMithu(g, token)));
    }

    /// <summary>Adds (or refreshes) Mithu's hooks.</summary>
    public void Install(string token)
    {
        var root = Read() ?? new JsonObject();
        Backup();
        Strip(root);
        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var name in Events)
        {
            if (hooks[name] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[name] = groups;
            }

            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = Command(token),
                    ["async"] = true,
                    ["timeout"] = 10,
                }),
            });
        }

        Write(root);
    }

    /// <summary>Takes Mithu's hooks out (anything else stays). Nothing happens when the file isn't there.</summary>
    public void Uninstall()
    {
        if (Read() is not { } root)
        {
            return;
        }

        if (Strip(root))
        {
            Write(root);
        }
    }

    /// <summary>Removes Mithu's entries; returns whether anything changed.</summary>
    private static bool Strip(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        var changed = false;
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray groups)
            {
                continue;
            }

            foreach (var group in groups.ToList())
            {
                if (group?["hooks"] is not JsonArray handlers)
                {
                    continue;
                }

                foreach (var handler in handlers.Where(h => IsMithu(h)).ToList())
                {
                    handlers.Remove(handler);
                    changed = true;
                }

                if (handlers.Count == 0)
                {
                    groups.Remove(group);
                }
            }

            if (groups.Count == 0)
            {
                hooks.Remove(name);
            }
        }

        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }

        return changed;
    }

    private static bool IsMithu(JsonNode? handler) =>
        handler?["command"]?.GetValueKind() == JsonValueKind.String && handler["command"]!.GetValue<string>().Contains(Marker, StringComparison.Ordinal);

    private static bool HasMithu(JsonNode? group, string token) =>
        group?["hooks"] is JsonArray handlers
        && handlers.Any(h => IsMithu(h) && h!["command"]!.GetValue<string>().EndsWith(Marker + token, StringComparison.Ordinal));

    private JsonObject? Read()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        var text = File.ReadAllText(SettingsPath);
        if (text.Trim().Length == 0)
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new InvalidDataException("Claude Code's settings file isn't a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Claude Code's settings file isn't valid JSON, so it was left alone.", ex);
        }
    }

    private void Backup()
    {
        var backup = SettingsPath + ".mithu-backup";
        if (File.Exists(SettingsPath) && !File.Exists(backup))
        {
            File.Copy(SettingsPath, backup);
        }
    }

    /// <summary>Writes the whole file at once (a half-written settings file would break Claude Code).</summary>
    private void Write(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + ".mithu-tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented) + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temp, SettingsPath, overwrite: true);
    }
}
