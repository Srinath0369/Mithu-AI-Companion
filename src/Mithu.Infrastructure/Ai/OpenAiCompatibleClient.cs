using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Mithu.Core.Ai;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Ai;

/// <summary>
/// Any server speaking OpenAI's <c>/chat/completions</c> with function tools (OpenAI, OpenRouter, Groq, Gemini's
/// compatible endpoint, Ollama, LM Studio). Retries 429/5xx/network errors twice with backoff (honouring
/// <c>Retry-After</c>), times out each attempt, and maps every failure to <see cref="AiException"/>.
/// Never logs the key or message content.
/// </summary>
public sealed class OpenAiCompatibleClient(HttpClient http, AiConnection connection, ILogger logger) : IAiClient
{
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(90);
    private const int MaxRetries = 2;
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(10);

    /// <summary>Retry pause (overridable so tests don't wait).</summary>
    internal Func<int, TimeSpan> Backoff { get; init; } = attempt => TimeSpan.FromSeconds(attempt);

    public async Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request, connection.Model);
        using var json = await SendWithRetryAsync(HttpMethod.Post, "chat/completions", body, cancellationToken);
        return ParseResponse(json.RootElement);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        using var json = await SendWithRetryAsync(HttpMethod.Get, "models", null, cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return data.EnumerateArray()
            .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ===== Request ==============================================================

    internal static JsonObject BuildBody(AiRequest request, string model)
    {
        var messages = new JsonArray();
        if (request.SystemPrompt.Length > 0)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        }

        foreach (var message in request.Messages)
        {
            if (message.Role == AiRole.Assistant)
            {
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = message.Text.Length > 0 ? message.Text : null };
                var calls = message.ToolCalls;
                if (calls.Count > 0)
                {
                    assistant["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                    }).ToArray());
                }

                messages.Add(assistant);
                continue;
            }

            // A user turn: tool results become "tool" messages; text becomes a user message.
            foreach (var result in message.Parts.OfType<AiToolResult>())
            {
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = result.ToolCallId, ["content"] = result.Content });
            }

            var images = message.Images.Where(i => i.Data is not null).ToList();
            if (images.Count > 0)
            {
                // Text and screenshots together: the "content parts" form.
                var content = new JsonArray();
                if (message.Text.Length > 0)
                {
                    content.Add(new JsonObject { ["type"] = "text", ["text"] = message.Text });
                }

                foreach (var image in images)
                {
                    content.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data!)}" },
                    });
                }

                messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            }
            else if (message.Text.Length > 0)
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = message.Text });
            }
        }

        // One complete answer, not a stream (some servers stream unless told otherwise).
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = false };
        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.ParametersSchemaJson),
                },
            }).ToArray());
        }

        // No token limit is sent: servers disagree on its name (max_tokens vs max_completion_tokens).
        return body;
    }

    // ===== Response =============================================================

    internal static AiResponse ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            throw new AiException(AiErrorKind.Unknown, "The AI service sent an empty answer.");
        }

        var choice = choices[0];
        var message = choice.GetProperty("message");
        var parts = new List<AiPart>();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
        {
            parts.Add(new AiText(text));
        }

        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var id = call.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } s ? s : "call_" + Guid.NewGuid().ToString("N")[..12];
                var arguments = function.TryGetProperty("arguments", out var args)
                    ? args.ValueKind == JsonValueKind.String ? args.GetString() ?? "{}" : args.GetRawText()
                    : "{}";
                parts.Add(new AiToolCall(id, function.GetProperty("name").GetString() ?? string.Empty, arguments.Length == 0 ? "{}" : arguments));
            }
        }

        var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
        var stop = parts.OfType<AiToolCall>().Any() ? AiStopReason.ToolUse
            : finish switch
            {
                "length" => AiStopReason.MaxTokens,
                "content_filter" => AiStopReason.Refused,
                _ => AiStopReason.Done,
            };

        AiUsage? usage = null;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            usage = new AiUsage(
                u.TryGetProperty("prompt_tokens", out var i) && i.TryGetInt64(out var input) ? input : 0,
                u.TryGetProperty("completion_tokens", out var o) && o.TryGetInt64(out var output) ? output : 0);
        }

        return new AiResponse(new AiMessage(AiRole.Assistant, parts), stop, usage);
    }

    // ===== HTTP ===================================================================

    /// <summary>A whole answer: the response read in one go (a stream that arrives anyway is joined).</summary>
    private async Task<JsonDocument> SendWithRetryAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        var (response, timeout) = await OpenAsync(method, path, body, cancellationToken);
        using (response)
        using (timeout)
        {
            var text = await Read(() => response.Content.ReadAsStringAsync(timeout.Token), cancellationToken);
            return ParseJson(() => IsEventStream(response, text) ? JoinStream(text).ToJsonString() : text);
        }
    }

    /// <summary>Asks for a stream and hands each piece of text to <paramref name="onText"/> as it arrives.</summary>
    public async Task<AiResponse> StreamAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken = default)
    {
        var body = BuildBody(request, connection.Model);
        body["stream"] = true;
        var (response, timeout) = await OpenAsync(HttpMethod.Post, "chat/completions", body, cancellationToken);
        using (response)
        using (timeout)
        {
            // A server that ignores "stream": one ordinary answer.
            if (response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
            {
                var text = await Read(() => response.Content.ReadAsStringAsync(timeout.Token), cancellationToken);
                using var whole = ParseJson(() => text.TrimStart().StartsWith("data:", StringComparison.Ordinal) ? JoinStream(text).ToJsonString() : text);
                var answer = ParseResponse(whole.RootElement);
                if (answer.Message.Text.Length > 0)
                {
                    onText(answer.Message.Text);
                }

                return answer;
            }

            var events = new StringBuilder();
            var stream = await Read(() => response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                // Idle timeout: a long answer is fine as long as pieces keep coming.
                timeout.CancelAfter(AttemptTimeout);
                var line = await Read(() => reader.ReadLineAsync(timeout.Token).AsTask(), cancellationToken);
                if (line is null)
                {
                    break;
                }

                events.Append(line).Append('\n');
                if (TextDelta(line) is { Length: > 0 } piece)
                {
                    onText(piece);
                }
            }

            using var joined = ParseJson(() => JoinStream(events.ToString()).ToJsonString());
            return ParseResponse(joined.RootElement);
        }
    }

    /// <summary>The text piece of one <c>data:</c> line of a stream (null if it has none).</summary>
    internal static string? TextDelta(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.Ordinal) || trimmed.Contains("[DONE]", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(trimmed[5..])?["choices"]?[0]?["delta"]?["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends the request and returns a successful response with its headers read (the body is still to come),
    /// plus the timeout that guards reading it. Retries rate limits, server errors and dropped connections.
    /// </summary>
    private async Task<(HttpResponseMessage Response, CancellationTokenSource Timeout)> OpenAsync(
        HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUrl))
        {
            throw new AiException(AiErrorKind.NotConfigured, AiException.Friendly(AiErrorKind.NotConfigured));
        }

        var url = LocalToIPv4(connection.BaseUrl.TrimEnd('/')) + "/" + path;
        var payload = body?.ToJsonString();
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AttemptTimeout);
            using var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(connection.ApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
            }

            if (payload is not null)
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            TimeSpan? retryAfter = null;
            AiException failure;
            try
            {
                var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return (response, timeout);
                }

                using (response)
                {
                    var text = await response.Content.ReadAsStringAsync(timeout.Token);
                    failure = FromStatus(response.StatusCode, text);
                    retryAfter = response.Headers.RetryAfter?.Delta;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                timeout.Dispose();
                throw; // the user cancelled
            }
            catch (OperationCanceledException ex)
            {
                failure = new AiException(AiErrorKind.Timeout, AiException.Friendly(AiErrorKind.Timeout), ex);
            }
            catch (HttpRequestException ex)
            {
                failure = new AiException(AiErrorKind.Offline, AiException.Friendly(AiErrorKind.Offline), ex);
            }

            timeout.Dispose();

            // A local server (Ollama, LM Studio) that refuses the connection isn't running: retrying only delays the answer.
            var retryable = failure.Kind is AiErrorKind.RateLimited or AiErrorKind.ServerError
                || (failure.Kind == AiErrorKind.Offline && !IsLoopback(url));
            if (!retryable || attempt >= MaxRetries)
            {
                logger.LogWarning("AI request failed: {Kind} after {Attempts} attempt(s)", failure.Kind, attempt + 1);
                throw failure;
            }

            var wait = retryAfter is { } ra && ra > TimeSpan.Zero ? (ra > MaxRetryWait ? MaxRetryWait : ra) : Backoff(attempt + 1);
            logger.LogInformation("AI request {Kind}; retrying in {Seconds:0.#}s", failure.Kind, wait.TotalSeconds);
            await Task.Delay(wait, cancellationToken);
        }
    }

    /// <summary>Reads the body: a dropped connection is Offline, silence for too long is Timeout, Stop is a cancel.</summary>
    private static async Task<T> Read<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new AiException(AiErrorKind.Timeout, AiException.Friendly(AiErrorKind.Timeout), ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new AiException(AiErrorKind.Offline, AiException.Friendly(AiErrorKind.Offline), ex);
        }
    }

    private static JsonDocument ParseJson(Func<string> json)
    {
        try
        {
            return JsonDocument.Parse(json());
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.Unknown, "The AI service sent an answer that couldn't be read.", ex);
        }
    }

    private static bool IsEventStream(HttpResponseMessage response, string text) =>
        response.Content.Headers.ContentType?.MediaType == "text/event-stream" || text.TrimStart().StartsWith("data:", StringComparison.Ordinal);

    /// <summary>
    /// Server-sent events of <c>chat.completion.chunk</c>s → one <c>chat.completion</c>: text deltas joined, tool-call
    /// pieces joined by index, the last finish reason and usage kept. Reasoning deltas are left out.
    /// </summary>
    internal static JsonObject JoinStream(string events)
    {
        var content = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
        string? finish = null;
        JsonNode? usage = null;

        foreach (var raw in events.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            var chunk = JsonNode.Parse(data);
            if (chunk?["usage"] is JsonObject u)
            {
                usage = u.DeepClone();
            }

            if (chunk?["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice)
            {
                continue;
            }

            if (choice["finish_reason"] is JsonValue f && f.TryGetValue<string>(out var reason))
            {
                finish = reason;
            }

            if (choice["delta"] is not JsonObject delta)
            {
                continue;
            }

            if (delta["content"] is JsonValue c && c.TryGetValue<string>(out var piece))
            {
                content.Append(piece);
            }

            if (delta["tool_calls"] is JsonArray toolCalls)
            {
                foreach (var node in toolCalls.OfType<JsonObject>())
                {
                    var index = node["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : calls.Count;
                    var call = calls.TryGetValue(index, out var existing) ? existing : (null, null, new StringBuilder());
                    var id = node["id"]?.GetValue<string>() ?? call.Item1;
                    var name = node["function"]?["name"]?.GetValue<string>() ?? call.Item2;
                    call.Item3.Append(node["function"]?["arguments"]?.GetValue<string>() ?? string.Empty);
                    calls[index] = (id, name, call.Item3);
                }
            }
        }

        var message = new JsonObject { ["role"] = "assistant", ["content"] = content.Length > 0 ? content.ToString() : null };
        if (calls.Count > 0)
        {
            message["tool_calls"] = new JsonArray(calls.Values.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.ToString() },
            }).ToArray());
        }

        var result = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = message, ["finish_reason"] = finish }),
        };
        if (usage is not null)
        {
            result["usage"] = usage;
        }

        return result;
    }

    /// <summary>
    /// "localhost" → 127.0.0.1: local model servers listen on IPv4, and Windows otherwise tries IPv6 first, so a
    /// server that isn't running takes seconds longer to report.
    /// </summary>
    internal static string LocalToIPv4(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.ToString().TrimEnd('/')
            : baseUrl;

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    internal static AiException FromStatus(HttpStatusCode status, string body)
    {
        var kind = (int)status switch
        {
            401 or 403 => AiErrorKind.InvalidKey,
            404 => AiErrorKind.NotFound,
            408 => AiErrorKind.Timeout,
            429 => AiErrorKind.RateLimited,
            400 or 409 or 413 or 422 => AiErrorKind.BadRequest,
            >= 500 => AiErrorKind.ServerError,
            _ => AiErrorKind.Unknown,
        };

        var detail = ErrorDetail(body);
        var message = AiException.Friendly(kind) + (detail is null ? string.Empty : $" ({detail})");
        return new AiException(kind, message);
    }

    /// <summary>The provider's own error text, shortened ("error.message" in OpenAI's format).</summary>
    private static string? ErrorDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
            if (error.TryGetProperty("error", out var e))
            {
                error = e;
            }

            var text = error.ValueKind == JsonValueKind.String ? error.GetString()
                : error.TryGetProperty("message", out var m) ? m.GetString() : null;
            return text is null ? null : text.Length > 200 ? text[..200] + "…" : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
