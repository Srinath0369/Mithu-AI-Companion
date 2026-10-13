namespace Mithu.Core.Ai;

/// <summary>Who wrote a message. Tool results travel inside a user message (the providers' clients map this).</summary>
public enum AiRole
{
    User,
    Assistant,
}

/// <summary>A piece of a message. Provider-neutral, so a chat can be saved and continued on either provider.</summary>
public abstract record AiPart;

public sealed record AiText(string Text) : AiPart;

/// <summary>The model asks to run a tool. <see cref="ArgumentsJson"/> is a JSON object (parse it, never string-match).</summary>
public sealed record AiToolCall(string Id, string Name, string ArgumentsJson) : AiPart;

/// <summary>What a tool returned, answering <see cref="AiToolCall"/> with the same id.</summary>
public sealed record AiToolResult(string ToolCallId, string Content, bool IsError = false) : AiPart;

/// <summary>
/// Claude's reasoning block. Must be sent back unchanged (with its signature) when continuing on the same model;
/// other providers ignore it.
/// </summary>
public sealed record AiThinking(string Text, string Signature) : AiPart;

/// <summary>Claude's encrypted reasoning block (sent back unchanged, ignored elsewhere).</summary>
public sealed record AiRedactedThinking(string Data) : AiPart;

/// <summary>
/// An image the user attached (a screenshot). Saved chats keep only <see cref="Id"/> (the file lives in the image
/// store); <see cref="Data"/> is filled in just before sending and is never stored in the database.
/// </summary>
public sealed record AiImage(string Id, string MediaType = "image/png", byte[]? Data = null) : AiPart
{
    public AiImage WithData(byte[]? data) => this with { Data = data };

    /// <summary>Records compare bytes by reference; compare by id and type instead.</summary>
    public bool Equals(AiImage? other) => other is not null && Id == other.Id && MediaType == other.MediaType;

    public override int GetHashCode() => HashCode.Combine(Id, MediaType);
}

public sealed record AiMessage(AiRole Role, IReadOnlyList<AiPart> Parts)
{
    public static AiMessage User(string text) => new(AiRole.User, [new AiText(text)]);

    /// <summary>A question about one or more images (images first, as the providers recommend).</summary>
    public static AiMessage UserWithImages(string text, IEnumerable<AiImage> images) =>
        new(AiRole.User, [.. images, new AiText(text)]);

    public IReadOnlyList<AiImage> Images => Parts.OfType<AiImage>().ToList();

    public static AiMessage Assistant(string text) => new(AiRole.Assistant, [new AiText(text)]);

    public static AiMessage ToolResults(IEnumerable<AiToolResult> results) => new(AiRole.User, results.ToList());

    /// <summary>All visible text, joined.</summary>
    public string Text => string.Join("\n\n", Parts.OfType<AiText>().Select(p => p.Text).Where(t => t.Length > 0));

    public IReadOnlyList<AiToolCall> ToolCalls => Parts.OfType<AiToolCall>().ToList();
}

/// <summary>A tool the model may call. <see cref="ParametersSchemaJson"/> is a JSON Schema object.</summary>
public sealed record AiTool(string Name, string Description, string ParametersSchemaJson);

public sealed record AiRequest(
    string SystemPrompt,
    IReadOnlyList<AiMessage> Messages,
    IReadOnlyList<AiTool>? Tools = null,
    int MaxTokens = 16000);

public enum AiStopReason
{
    /// <summary>The answer is complete.</summary>
    Done,

    /// <summary>The model wants tools run (see <see cref="AiResponse.Message"/>'s tool calls).</summary>
    ToolUse,

    /// <summary>Cut off by the length limit.</summary>
    MaxTokens,

    /// <summary>The provider declined to answer.</summary>
    Refused,
}

public sealed record AiResponse(AiMessage Message, AiStopReason StopReason, AiUsage? Usage = null);

public sealed record AiUsage(long InputTokens, long OutputTokens);

/// <summary>Which wire protocol a provider speaks.</summary>
public enum AiProviderKind
{
    /// <summary>OpenAI's <c>/chat/completions</c> (OpenAI, OpenRouter, Groq, Gemini's compatible endpoint, Ollama, LM Studio…).</summary>
    OpenAiCompatible,

    /// <summary>Claude, through Anthropic's own API.</summary>
    Anthropic,
}

/// <summary>Everything needed to talk to a provider. The key is never logged or stored in settings.</summary>
public sealed record AiConnection(AiProviderKind Kind, string BaseUrl, string Model, string? ApiKey)
{
    public override string ToString() => $"{Kind} {BaseUrl} {Model} (key {(string.IsNullOrEmpty(ApiKey) ? "none" : "set")})";
}
