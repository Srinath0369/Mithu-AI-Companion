using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mithu.Core.Ai;

/// <summary>A saved conversation. <see cref="UpdatedUtc"/> = last message (used for "new chat after a quiet spell").</summary>
public sealed record ChatThread(string Id, string Title, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, int MessageCount = 0)
{
    public static ChatThread Create(string title, DateTimeOffset nowUtc) =>
        new(Guid.NewGuid().ToString("N"), title, nowUtc, nowUtc);
}

/// <summary>One saved message of a chat. <see cref="Message"/> keeps every part (tool calls, thinking) for replay.</summary>
public sealed record ChatEntry(long Id, string ChatId, AiMessage Message, DateTimeOffset CreatedUtc, string? Model = null);

/// <summary>Rules for chats: when a new one starts, titles, and how much history is sent.</summary>
public static class ChatRules
{
    /// <summary>A message after this much quiet starts a new chat (the old one stays in the list).</summary>
    public static readonly TimeSpan NewChatAfter = TimeSpan.FromHours(3);

    public const int MaxTitleLength = 60;

    /// <summary>History sent with a request: at most this many recent messages…</summary>
    public const int MaxContextMessages = 40;

    /// <summary>…and roughly this much text (keeps cost and speed in check on long chats).</summary>
    public const int MaxContextChars = 60_000;

    public static bool ShouldStartNewChat(ChatThread? current, DateTimeOffset nowUtc) =>
        current is null || nowUtc - current.UpdatedUtc >= NewChatAfter;

    /// <summary>A title from the first message: its first line, tidied and shortened at a word.</summary>
    public static string TitleFrom(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0)
        {
            return "New chat";
        }

        if (line.Length > MaxTitleLength)
        {
            var cut = line[..MaxTitleLength];
            var space = cut.LastIndexOf(' ');
            line = (space > MaxTitleLength / 2 ? cut[..space] : cut).TrimEnd(',', '.', ';', ':', ' ') + "…";
        }

        return char.ToUpper(line[0], CultureInfo.CurrentCulture) + line[1..];
    }

    /// <summary>
    /// The recent part of a chat to send: newest messages within the limits, always starting with a plain user
    /// message (never a tool result or an assistant turn, which providers reject as a first message).
    /// </summary>
    public static IReadOnlyList<AiMessage> Context(IReadOnlyList<AiMessage> history, int maxMessages = MaxContextMessages, int maxChars = MaxContextChars)
    {
        var start = history.Count;
        var chars = 0;
        while (start > 0 && history.Count - start < maxMessages)
        {
            var size = Size(history[start - 1]);
            if (chars + size > maxChars && start < history.Count)
            {
                break;
            }

            chars += size;
            start--;
        }

        while (start < history.Count && !IsPlainUserMessage(history[start]))
        {
            start++;
        }

        return MergeUserRuns(DropUnansweredCalls(history.Skip(start).ToList()));
    }

    /// <summary>
    /// A tool call must be followed by its result (providers reject the chat otherwise). Calls without results
    /// (e.g. the app closed mid-action) are dropped, keeping the message's words.
    /// </summary>
    private static List<AiMessage> DropUnansweredCalls(List<AiMessage> messages)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var calls = messages[i].ToolCalls;
            if (messages[i].Role != AiRole.Assistant || calls.Count == 0)
            {
                continue;
            }

            var answered = i + 1 < messages.Count
                ? messages[i + 1].Parts.OfType<AiToolResult>().Select(r => r.ToolCallId).ToHashSet()
                : [];
            if (calls.All(c => answered.Contains(c.Id)))
            {
                continue;
            }

            var kept = messages[i].Parts.Where(p => p is not AiToolCall c || answered.Contains(c.Id)).ToList();
            messages[i] = new AiMessage(AiRole.Assistant, kept.Count > 0 ? kept : [new AiText("…")]);
        }

        return messages;
    }

    /// <summary>
    /// Two plain user messages in a row (an earlier request failed or was stopped) become one, so every provider
    /// accepts the conversation.
    /// </summary>
    private static List<AiMessage> MergeUserRuns(List<AiMessage> messages)
    {
        var merged = new List<AiMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (merged.Count > 0 && IsPlainUserMessage(message) && IsPlainUserMessage(merged[^1]))
            {
                merged[^1] = new AiMessage(AiRole.User, [.. merged[^1].Parts, .. message.Parts]); // keeps screenshots too
            }
            else
            {
                merged.Add(message);
            }
        }

        return merged;
    }

    private static bool IsPlainUserMessage(AiMessage m) => m.Role == AiRole.User && !m.Parts.OfType<AiToolResult>().Any();

    /// <summary>Only the newest few screenshots are re-sent with a request (each one costs tokens).</summary>
    public const int MaxImagesSent = 3;

    /// <summary>
    /// Images to send: the newest <see cref="MaxImagesSent"/> stay; older ones become a short note so the model
    /// knows a screenshot was there.
    /// </summary>
    public static IReadOnlyList<AiMessage> LimitImages(IReadOnlyList<AiMessage> messages, int keep = MaxImagesSent)
    {
        var kept = 0;
        var result = new AiMessage[messages.Count];
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.Images.Count == 0)
            {
                result[i] = message;
                continue;
            }

            var parts = new List<AiPart>();
            foreach (var part in message.Parts)
            {
                if (part is AiImage && kept++ >= keep)
                {
                    parts.Add(new AiText("[An earlier screenshot was here; it isn't re-sent.]"));
                }
                else
                {
                    parts.Add(part);
                }
            }

            result[i] = new AiMessage(message.Role, parts);
        }

        return result;
    }

    private static int Size(AiMessage m) => m.Parts.Sum(p => p switch
    {
        AiImage => 4000, // a screenshot counts like a long message
        AiText t => t.Text.Length,
        AiToolCall c => c.ArgumentsJson.Length + c.Name.Length,
        AiToolResult r => r.Content.Length,
        AiThinking t => t.Text.Length + t.Signature.Length,
        AiRedactedThinking r => r.Data.Length,
        _ => 0,
    });
}

/// <summary>Saves a message's parts as JSON and reads them back (unknown parts from a newer version are skipped).</summary>
public static class AiMessageJson
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    public static string Write(IReadOnlyList<AiPart> parts) =>
        JsonSerializer.Serialize(parts.Select(ToDto).OfType<PartDto>().ToList(), Options);

    public static IReadOnlyList<AiPart> Read(string json)
    {
        var dtos = JsonSerializer.Deserialize<List<PartDto>>(json, Options) ?? [];
        return dtos.Select(FromDto).OfType<AiPart>().ToList();
    }

    private static PartDto? ToDto(AiPart part) => part switch
    {
        AiText t => new PartDto { Type = "text", Text = t.Text },
        AiToolCall c => new PartDto { Type = "tool_call", Id = c.Id, Name = c.Name, Data = c.ArgumentsJson },
        AiToolResult r => new PartDto { Type = "tool_result", Id = r.ToolCallId, Text = r.Content, IsError = r.IsError },
        AiThinking t => new PartDto { Type = "thinking", Text = t.Text, Data = t.Signature },
        AiRedactedThinking r => new PartDto { Type = "redacted_thinking", Data = r.Data },
        AiImage i => new PartDto { Type = "image", Id = i.Id, Name = i.MediaType }, // bytes stay in the image store
        _ => null,
    };

    private static AiPart? FromDto(PartDto d) => d.Type switch
    {
        "text" => new AiText(d.Text ?? string.Empty),
        "tool_call" => new AiToolCall(d.Id ?? string.Empty, d.Name ?? string.Empty, d.Data ?? "{}"),
        "tool_result" => new AiToolResult(d.Id ?? string.Empty, d.Text ?? string.Empty, d.IsError),
        "thinking" => new AiThinking(d.Text ?? string.Empty, d.Data ?? string.Empty),
        "redacted_thinking" => new AiRedactedThinking(d.Data ?? string.Empty),
        "image" => new AiImage(d.Id ?? string.Empty, d.Name ?? "image/png"),
        _ => null,
    };

    private sealed class PartDto
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("data")]
        public string? Data { get; set; }

        [JsonPropertyName("isError")]
        public bool IsError { get; set; }
    }
}
