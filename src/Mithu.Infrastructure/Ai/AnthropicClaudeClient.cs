using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;
using Mithu.Core.Ai;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Ai;

/// <summary>
/// Claude through Anthropic's official C# SDK (Messages API). Thinking is left to the model (always on for the
/// current Opus) and its blocks are kept and sent back unchanged, as the API requires during tool use.
/// Chat runs at <c>low</c> effort (quick answers); on the newest Opus/Fable models a server-side refusal fallback
/// re-serves a declined request on Claude Opus 4.8. The SDK retries 429/5xx itself. Never logs the key or content.
/// </summary>
public sealed class AnthropicClaudeClient : IAiClient
{
    /// <summary>Server-side refusal fallback (beta): a declined request is answered by the fallback model in the same call.</summary>
    private const string FallbackBeta = "server-side-fallback-2026-06-01";

    private static readonly string[] FallbackModels = ["claude-opus-5-5", "claude-opus-5", "claude-fable-5-1"];

    private readonly AnthropicClient _client;
    private readonly AiConnection _connection;
    private readonly ILogger _logger;

    public AnthropicClaudeClient(AiConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
        _client = new AnthropicClient
        {
            ApiKey = connection.ApiKey,
            BaseUrl = string.IsNullOrWhiteSpace(connection.BaseUrl) ? "https://api.anthropic.com" : connection.BaseUrl.TrimEnd('/'),
            MaxRetries = 2,
            Timeout = TimeSpan.FromSeconds(120),
        };
    }

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var parameters = BuildParams(request);
        try
        {
            var message = await _client.Beta.Messages.Create(parameters, cancellationToken);
            return FromMessage(message);
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            _logger.LogWarning("Claude request failed: {Kind}", mapped.Kind);
            throw mapped;
        }
    }

    /// <summary>
    /// Streams the answer: text pieces go to <paramref name="onText"/> as Claude writes them; the complete message
    /// (text, thinking with its signature, tool calls) is rebuilt from the events and returned.
    /// </summary>
    public async Task<AiResponse> StreamAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken = default)
    {
        var parameters = BuildParams(request);
        var blocks = new SortedDictionary<long, StreamBlock>();
        string? stopReason = null;
        long inputTokens = 0, outputTokens = 0;
        try
        {
            await foreach (var ev in _client.Beta.Messages.CreateStreaming(parameters, cancellationToken))
            {
                if (ev.TryPickStart(out var start))
                {
                    inputTokens = start.Message.Usage.InputTokens;
                }
                else if (ev.TryPickContentBlockStart(out var blockStart))
                {
                    var block = blockStart.ContentBlock;
                    blocks[blockStart.Index] =
                        block.TryPickBetaText(out var text) ? new StreamBlock("text", text.Text)
                        : block.TryPickBetaThinking(out var thinking) ? new StreamBlock("thinking", thinking.Thinking) { Signature = thinking.Signature }
                        : block.TryPickBetaRedactedThinking(out var redacted) ? new StreamBlock("redacted") { Signature = redacted.Data }
                        : block.TryPickBetaToolUse(out var toolUse) ? new StreamBlock("tool_use") { Id = toolUse.ID, Name = toolUse.Name }
                        : new StreamBlock("other");
                    if (blocks[blockStart.Index].Kind == "text" && blocks[blockStart.Index].Text.Length > 0)
                    {
                        onText(blocks[blockStart.Index].Text.ToString());
                    }
                }
                else if (ev.TryPickContentBlockDelta(out var blockDelta) && blocks.TryGetValue(blockDelta.Index, out var current))
                {
                    var delta = blockDelta.Delta;
                    if (delta.TryPickText(out var textDelta))
                    {
                        current.Text.Append(textDelta.Text);
                        onText(textDelta.Text);
                    }
                    else if (delta.TryPickThinking(out var thinkingDelta))
                    {
                        current.Text.Append(thinkingDelta.Thinking);
                    }
                    else if (delta.TryPickSignature(out var signatureDelta))
                    {
                        current.Signature = signatureDelta.Signature;
                    }
                    else if (delta.TryPickInputJson(out var jsonDelta))
                    {
                        current.Json.Append(jsonDelta.PartialJson);
                    }
                }
                else if (ev.TryPickDelta(out var messageDelta))
                {
                    stopReason = messageDelta.Delta.StopReason?.ToString();
                    outputTokens = messageDelta.Usage.OutputTokens;
                }
            }
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            _logger.LogWarning("Claude stream failed: {Kind}", mapped.Kind);
            throw mapped;
        }

        var parts = new List<AiPart>();
        foreach (var block in blocks.Values)
        {
            switch (block.Kind)
            {
                case "text":
                    parts.Add(new AiText(block.Text.ToString()));
                    break;
                case "thinking":
                    parts.Add(new AiThinking(block.Text.ToString(), block.Signature ?? string.Empty));
                    break;
                case "redacted":
                    parts.Add(new AiRedactedThinking(block.Signature ?? string.Empty));
                    break;
                case "tool_use":
                    parts.Add(new AiToolCall(block.Id ?? string.Empty, block.Name ?? string.Empty, block.Json.Length > 0 ? block.Json.ToString() : "{}"));
                    break;
            }
        }

        return new AiResponse(new AiMessage(AiRole.Assistant, parts), StopOf(stopReason, parts), new AiUsage(inputTokens, outputTokens));
    }

    /// <summary>A content block being assembled from stream events.</summary>
    private sealed class StreamBlock(string kind, string text = "")
    {
        public string Kind { get; } = kind;

        public System.Text.StringBuilder Text { get; } = new(text);

        public System.Text.StringBuilder Json { get; } = new();

        public string? Signature { get; set; }

        public string? Id { get; init; }

        public string? Name { get; init; }
    }

    private MessageCreateParams BuildParams(AiRequest request)
    {
        if (string.IsNullOrEmpty(_connection.ApiKey) || string.IsNullOrWhiteSpace(_connection.Model))
        {
            throw new AiException(AiErrorKind.NotConfigured, AiException.Friendly(AiErrorKind.NotConfigured));
        }

        var parameters = new MessageCreateParams
        {
            Model = _connection.Model,
            MaxTokens = request.MaxTokens,
            System = request.SystemPrompt,
            Messages = request.Messages.Select(ToParam).ToList(),
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            parameters = parameters with { Tools = tools.Select(ToTool).ToList() };
        }

        if (SupportsEffort(_connection.Model))
        {
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = Effort.Low } };
        }

        if (FallbackModels.Contains(_connection.Model))
        {
            parameters = parameters with
            {
                Betas = [FallbackBeta],
                Fallbacks = new List<BetaFallbackParam> { new(Anthropic.Models.Messages.Model.ClaudeOpus4_8) },
            };
        }

        return parameters;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var page = await _client.Models.List(cancellationToken: cancellationToken);
            return page.Items.Select(m => m.ID).ToList();
        }
        catch (Exception ex) when (Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    /// <summary>Effort is accepted by the Claude 5 family and Opus 4.5+ / Sonnet 4.6; older models reject it.</summary>
    internal static bool SupportsEffort(string model) =>
        model.StartsWith("claude-opus-5", StringComparison.Ordinal)
        || model.StartsWith("claude-sonnet-5", StringComparison.Ordinal)
        || model.StartsWith("claude-haiku-5", StringComparison.Ordinal)
        || model.StartsWith("claude-fable-5", StringComparison.Ordinal)
        || model.StartsWith("claude-mythos-5", StringComparison.Ordinal)
        || model is "claude-opus-4-8" or "claude-opus-4-7" or "claude-opus-4-6" or "claude-sonnet-4-6";

    // ===== Mapping ================================================================

    private static BetaMessageParam ToParam(AiMessage message)
    {
        var blocks = new List<BetaContentBlockParam>();
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case AiThinking t:
                    blocks.Add(new BetaThinkingBlockParam { Thinking = t.Text, Signature = t.Signature });
                    break;
                case AiRedactedThinking r:
                    blocks.Add(new BetaRedactedThinkingBlockParam { Data = r.Data });
                    break;
                case AiText { Text.Length: > 0 } text:
                    blocks.Add(new BetaTextBlockParam { Text = text.Text });
                    break;
                case AiToolCall call:
                    blocks.Add(new BetaToolUseBlockParam { ID = call.Id, Name = call.Name, Input = ParseObject(call.ArgumentsJson) });
                    break;
                case AiToolResult result:
                    blocks.Add(new BetaToolResultBlockParam { ToolUseID = result.ToolCallId, Content = result.Content, IsError = result.IsError });
                    break;
                case AiImage { Data: { } data } image:
                    blocks.Add(new BetaImageBlockParam
                    {
                        Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(data), MediaType = image.MediaType },
                    });
                    break;
            }
        }

        return new BetaMessageParam { Role = message.Role == AiRole.Assistant ? Role.Assistant : Role.User, Content = blocks };
    }

    private static BetaToolUnion ToTool(AiTool tool)
    {
        using var schema = JsonDocument.Parse(tool.ParametersSchemaJson);
        var root = schema.RootElement;
        var properties = new Dictionary<string, JsonElement>();
        if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
            {
                properties[p.Name] = p.Value.Clone();
            }
        }

        var required = root.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];

        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new() { Properties = properties, Required = required },
        };
    }

    private static AiResponse FromMessage(BetaMessage message)
    {
        var parts = new List<AiPart>();
        foreach (var block in message.Content)
        {
            if (block.TryPickThinking(out var thinking))
            {
                parts.Add(new AiThinking(thinking.Thinking, thinking.Signature));
            }
            else if (block.TryPickRedactedThinking(out var redacted))
            {
                parts.Add(new AiRedactedThinking(redacted.Data));
            }
            else if (block.TryPickText(out var text))
            {
                parts.Add(new AiText(text.Text));
            }
            else if (block.TryPickToolUse(out var toolUse))
            {
                parts.Add(new AiToolCall(toolUse.ID, toolUse.Name, JsonSerializer.Serialize(toolUse.Input)));
            }
        }

        return new AiResponse(new AiMessage(AiRole.Assistant, parts), StopOf(message.StopReason?.ToString(), parts),
            new AiUsage(message.Usage.InputTokens, message.Usage.OutputTokens));
    }

    private static AiStopReason StopOf(string? stopReason, IReadOnlyList<AiPart> parts) => stopReason switch
    {
        "tool_use" => AiStopReason.ToolUse,
        "max_tokens" => AiStopReason.MaxTokens,
        "refusal" => AiStopReason.Refused,
        _ => parts.OfType<AiToolCall>().Any() ? AiStopReason.ToolUse : AiStopReason.Done,
    };

    private static Dictionary<string, JsonElement> ParseObject(string json)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : [];
    }

    /// <summary>SDK exceptions → <see cref="AiException"/>, most specific first. Null = not ours (rethrow as is).</summary>
    internal static AiException? Map(Exception ex) => ex switch
    {
        AiException => null,
        OperationCanceledException => null, // the caller cancelled
        AnthropicUnauthorizedException or AnthropicForbiddenException => Wrap(AiErrorKind.InvalidKey, ex),
        AnthropicNotFoundException => Wrap(AiErrorKind.NotFound, ex),
        AnthropicRateLimitException => Wrap(AiErrorKind.RateLimited, ex),
        AnthropicBadRequestException or AnthropicUnprocessableEntityException => Wrap(AiErrorKind.BadRequest, ex),
        Anthropic5xxException => Wrap(AiErrorKind.ServerError, ex),
        AnthropicIOException => Wrap(AiErrorKind.Offline, ex),
        HttpRequestException => Wrap(AiErrorKind.Offline, ex),
        TimeoutException => Wrap(AiErrorKind.Timeout, ex),
        AnthropicApiException => Wrap(AiErrorKind.Unknown, ex),
        _ => null,
    };

    private static AiException Wrap(AiErrorKind kind, Exception inner) => new(kind, AiException.Friendly(kind), inner);
}
