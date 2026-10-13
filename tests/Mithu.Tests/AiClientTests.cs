using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Mithu.Core.Ai;
using Mithu.Infrastructure.Ai;

namespace Mithu.Tests;

/// <summary>Answers each request with the next canned response and records what was sent.</summary>
internal sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] replies) : HttpMessageHandler
{
    private int _next;

    public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add((request, body));
        var reply = replies[Math.Min(_next++, replies.Length - 1)];
        return reply(request);
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class OpenAiCompatibleClientTests
{
    private static readonly AiConnection Connection = new(AiProviderKind.OpenAiCompatible, "https://example.test/v1/", "test-model", "sk-secret");

    private static OpenAiCompatibleClient Client(FakeHandler handler) =>
        new(new HttpClient(handler), Connection, NullLogger.Instance) { Backoff = _ => TimeSpan.Zero };

    private const string TextReply = """
        {"choices":[{"message":{"role":"assistant","content":"Hi there!"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":12,"completion_tokens":3}}
        """;

    [Fact]
    public async Task Sends_a_chat_request_and_reads_the_answer()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, TextReply));
        var response = await Client(handler).SendAsync(new AiRequest("Be kind.", [AiMessage.User("Hello")]));

        Assert.Equal("Hi there!", response.Message.Text);
        Assert.Equal(AiStopReason.Done, response.StopReason);
        Assert.Equal(new AiUsage(12, 3), response.Usage);

        var (request, body) = handler.Sent.Single();
        Assert.Equal("https://example.test/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-secret", request.Headers.Authorization.Parameter);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("test-model", (string?)json["model"]);
        Assert.Equal("system", (string?)json["messages"]![0]!["role"]);
        Assert.Equal("Hello", (string?)json["messages"]![1]!["content"]);
    }

    [Fact]
    public void Maps_tool_calls_tool_results_and_tools()
    {
        var history = new[]
        {
            AiMessage.User("Remind me at 7"),
            new AiMessage(AiRole.Assistant,
            [
                new AiThinking("hmm", "sig"), // Claude-only: not sent here
                new AiText("Sure."),
                new AiToolCall("call_1", "create_reminder", """{"title":"Study"}"""),
            ]),
            AiMessage.ToolResults([new AiToolResult("call_1", """{"ok":true}""")]),
        };
        var tools = new[] { new AiTool("create_reminder", "Creates a reminder", """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""") };

        var body = OpenAiCompatibleClient.BuildBody(new AiRequest(string.Empty, history, tools), "m");
        var messages = body["messages"]!.AsArray();

        Assert.Equal(3, messages.Count); // no system message when the prompt is empty
        Assert.Equal("Sure.", (string?)messages[1]!["content"]);
        Assert.Equal("call_1", (string?)messages[1]!["tool_calls"]![0]!["id"]);
        Assert.Equal("create_reminder", (string?)messages[1]!["tool_calls"]![0]!["function"]!["name"]);
        Assert.Equal("tool", (string?)messages[2]!["role"]);
        Assert.Equal("call_1", (string?)messages[2]!["tool_call_id"]);
        Assert.Equal("function", (string?)body["tools"]![0]!["type"]);
        Assert.Equal("string", (string?)body["tools"]![0]!["function"]!["parameters"]!["properties"]!["title"]!["type"]);
    }

    [Fact]
    public void Reads_tool_calls_even_without_an_id()
    {
        using var doc = JsonDocument.Parse("""
            {"choices":[{"message":{"content":null,"tool_calls":[
              {"type":"function","function":{"name":"create_note","arguments":"{\"text\":\"milk\"}"}}]},
              "finish_reason":"tool_calls"}]}
            """);
        var response = OpenAiCompatibleClient.ParseResponse(doc.RootElement);

        var call = Assert.Single(response.Message.ToolCalls);
        Assert.Equal("create_note", call.Name);
        Assert.Equal("milk", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("text").GetString());
        Assert.StartsWith("call_", call.Id);
        Assert.Equal(AiStopReason.ToolUse, response.StopReason);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiErrorKind.InvalidKey)]
    [InlineData(HttpStatusCode.NotFound, AiErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, AiErrorKind.BadRequest)]
    public async Task Maps_errors_without_retrying_them(HttpStatusCode status, AiErrorKind kind)
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(status, """{"error":{"message":"Nope, that's wrong."}}"""));
        var ex = await Assert.ThrowsAsync<AiException>(() => Client(handler).SendAsync(new AiRequest("", [AiMessage.User("x")])));

        Assert.Equal(kind, ex.Kind);
        Assert.Contains("Nope, that's wrong.", ex.Message);
        Assert.DoesNotContain("sk-secret", ex.Message);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Retries_rate_limits_and_server_errors_then_succeeds()
    {
        var handler = new FakeHandler(
            _ => FakeHandler.Json(HttpStatusCode.TooManyRequests, "{}"),
            _ => FakeHandler.Json(HttpStatusCode.ServiceUnavailable, "{}"),
            _ => FakeHandler.Json(HttpStatusCode.OK, TextReply));

        var response = await Client(handler).SendAsync(new AiRequest("", [AiMessage.User("x")]));
        Assert.Equal("Hi there!", response.Message.Text);
        Assert.Equal(3, handler.Sent.Count);
    }

    [Fact]
    public async Task Gives_up_after_two_retries()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.TooManyRequests, "{}"));
        var ex = await Assert.ThrowsAsync<AiException>(() => Client(handler).SendAsync(new AiRequest("", [AiMessage.User("x")])));
        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Equal(3, handler.Sent.Count);
    }

    [Fact]
    public async Task No_network_is_offline()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("no route"));
        var ex = await Assert.ThrowsAsync<AiException>(() => Client(handler).SendAsync(new AiRequest("", [AiMessage.User("x")])));
        Assert.Equal(AiErrorKind.Offline, ex.Kind);
    }

    [Fact]
    public async Task A_local_server_that_is_not_running_fails_at_once()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("refused"));
        var local = new OpenAiCompatibleClient(new HttpClient(handler),
            new AiConnection(AiProviderKind.OpenAiCompatible, "http://localhost:11434/v1", "llama3", null), NullLogger.Instance);
        var ex = await Assert.ThrowsAsync<AiException>(() => local.ListModelsAsync());
        Assert.Equal(AiErrorKind.Offline, ex.Kind);
        Assert.Single(handler.Sent); // no retries
        Assert.Equal("http://127.0.0.1:11434/v1/models", handler.Sent[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Cancelling_is_not_an_error()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, TextReply));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).SendAsync(new AiRequest("", [AiMessage.User("x")]), cts.Token));
    }

    [Fact]
    public async Task Asks_for_one_answer_and_still_reads_a_stream()
    {
        const string stream = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"reasoning_content":"thinking out loud"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"content":"Hi there, "},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{"content":"friend!"},"finish_reason":null}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":5}}

            data: [DONE]
            """;
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        });

        var response = await Client(handler).SendAsync(new AiRequest("", [AiMessage.User("hi")]));

        Assert.Equal("Hi there, friend!", response.Message.Text);
        Assert.Equal(AiStopReason.Done, response.StopReason);
        Assert.Equal(new AiUsage(20, 5), response.Usage);
        Assert.False((bool)JsonNode.Parse(handler.Sent.Single().Body)!["stream"]!);
    }

    [Fact]
    public async Task Streams_text_pieces_as_they_arrive()
    {
        const string stream = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant"}}]}

            data: {"choices":[{"index":0,"delta":{"reasoning_content":"hmm"}}]}

            data: {"choices":[{"index":0,"delta":{"content":"Hello"}}]}

            data: {"choices":[{"index":0,"delta":{"content":", world"}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]
            """;
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
        });
        var pieces = new List<string>();

        var response = await Client(handler).StreamAsync(new AiRequest("", [AiMessage.User("hi")]), pieces.Add);

        Assert.Equal(["Hello", ", world"], pieces);
        Assert.Equal("Hello, world", response.Message.Text);
        Assert.True((bool)JsonNode.Parse(handler.Sent.Single().Body)!["stream"]!);
    }

    [Fact]
    public async Task A_server_that_does_not_stream_still_answers_when_streaming()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, TextReply));
        var pieces = new List<string>();
        var response = await Client(handler).StreamAsync(new AiRequest("", [AiMessage.User("hi")]), pieces.Add);
        Assert.Equal(["Hi there!"], pieces);
        Assert.Equal("Hi there!", response.Message.Text);
    }

    [Fact]
    public void Joins_streamed_tool_call_pieces()
    {
        const string stream = """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_9","type":"function","function":{"name":"create_note","arguments":"{\"te"}}]}}]}
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"xt\":\"milk\"}"}}]}}]}
            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}
            """;
        using var doc = JsonDocument.Parse(OpenAiCompatibleClient.JoinStream(stream).ToJsonString());
        var call = Assert.Single(OpenAiCompatibleClient.ParseResponse(doc.RootElement).Message.ToolCalls);
        Assert.Equal(("call_9", "create_note"), (call.Id, call.Name));
        Assert.Equal("milk", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Lists_models()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.OK, """{"data":[{"id":"zeta"},{"id":"alpha"}]}"""));
        var models = await Client(handler).ListModelsAsync();
        Assert.Equal(["alpha", "zeta"], models);
        Assert.Equal("https://example.test/v1/models", handler.Sent.Single().Request.RequestUri!.ToString());
    }
}

/// <summary>Runs the real Anthropic SDK against a tiny local server that plays the Messages API.</summary>
public sealed class AnthropicClaudeClientTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly List<(string Path, string Body, string? Key)> _received = [];
    private string _reply = string.Empty;
    private string _contentType = "application/json";
    private int _status = 200;

    public AnthropicClaudeClientTests()
    {
        // Find a free port.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _baseUrl = $"http://localhost:{port}";
        _listener.Prefixes.Add(_baseUrl + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            lock (_received)
            {
                _received.Add((context.Request.Url!.AbsolutePath, reader.ReadToEnd(), context.Request.Headers["x-api-key"]));
            }

            context.Response.StatusCode = _status;
            context.Response.ContentType = _contentType;
            var bytes = Encoding.UTF8.GetBytes(_reply);
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private AnthropicClaudeClient Client(string model = "claude-opus-5-5") =>
        new(new AiConnection(AiProviderKind.Anthropic, _baseUrl, model, "sk-ant-test"), NullLogger.Instance);

    private const string ToolUseReply = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[
           {"type":"thinking","thinking":"","signature":"sig-123"},
           {"type":"text","text":"On it."},
           {"type":"tool_use","id":"toolu_1","name":"create_reminder","input":{"title":"Study","hour":19}}],
         "stop_reason":"tool_use","stop_sequence":null,
         "usage":{"input_tokens":50,"output_tokens":20}}
        """;

    [Fact]
    public async Task Sends_a_messages_request_and_reads_thinking_text_and_tool_use()
    {
        _reply = ToolUseReply;
        var tools = new[] { new AiTool("create_reminder", "Creates a reminder", """{"type":"object","properties":{"title":{"type":"string"},"hour":{"type":"integer"}},"required":["title"]}""") };

        var response = await Client().SendAsync(new AiRequest("You are Mithu.", [AiMessage.User("Remind me to study at 7")], tools, MaxTokens: 2048));

        Assert.Equal(AiStopReason.ToolUse, response.StopReason);
        Assert.Equal("On it.", response.Message.Text);
        var call = Assert.Single(response.Message.ToolCalls);
        Assert.Equal(("toolu_1", "create_reminder"), (call.Id, call.Name));
        Assert.Equal(19, JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("hour").GetInt32());
        Assert.Equal("sig-123", response.Message.Parts.OfType<AiThinking>().Single().Signature);
        Assert.Equal(new AiUsage(50, 20), response.Usage);

        var (path, body, key) = _received.Single();
        Assert.Equal("/v1/messages", path);
        Assert.Equal("sk-ant-test", key);
        var json = JsonNode.Parse(body)!;
        Assert.Equal("claude-opus-5-5", (string?)json["model"]);
        Assert.Equal(2048, (int?)json["max_tokens"]);
        Assert.Equal("You are Mithu.", (string?)json["system"]);
        Assert.Equal("low", (string?)json["output_config"]!["effort"]);
        Assert.Equal("claude-opus-4-8", (string?)json["fallbacks"]![0]!["model"]);
        Assert.Equal("create_reminder", (string?)json["tools"]![0]!["name"]);
        Assert.Equal("integer", (string?)json["tools"]![0]!["input_schema"]!["properties"]!["hour"]!["type"]);
        Assert.Null(json["thinking"]); // left to the model
    }

    [Fact]
    public async Task Sends_screenshots_as_base64_image_blocks_before_the_text()
    {
        _reply = """
            {"id":"msg_3","type":"message","role":"assistant","model":"claude-opus-5-5",
             "content":[{"type":"text","text":"That's a spreadsheet."}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":900,"output_tokens":6}}
            """;
        var message = AiMessage.UserWithImages("what's this?", [new AiImage("x", "image/png", [1, 2, 3])]);

        await Client().SendAsync(new AiRequest("You are Mithu.", [message]));

        var content = JsonNode.Parse(_received.Single().Body)!["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("image", (string?)content[0]!["type"]);
        Assert.Equal("base64", (string?)content[0]!["source"]!["type"]);
        Assert.Equal("image/png", (string?)content[0]!["source"]!["media_type"]);
        Assert.Equal("AQID", (string?)content[0]!["source"]!["data"]);
        Assert.Equal("what's this?", (string?)content[1]!["text"]);
    }

    [Fact]
    public async Task Sends_thinking_and_tool_results_back_unchanged()
    {
        _reply = """
            {"id":"msg_2","type":"message","role":"assistant","model":"claude-haiku-5-5",
             "content":[{"type":"text","text":"Done! Reminder set for 7 PM."}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":80,"output_tokens":9}}
            """;
        var history = new[]
        {
            AiMessage.User("Remind me to study at 7"),
            new AiMessage(AiRole.Assistant,
            [
                new AiThinking("", "sig-123"),
                new AiText("On it."),
                new AiToolCall("toolu_1", "create_reminder", """{"title":"Study"}"""),
            ]),
            AiMessage.ToolResults([new AiToolResult("toolu_1", "Saved for Today 7:00 PM")]),
        };

        var response = await Client("claude-haiku-5-5").SendAsync(new AiRequest("", history));
        Assert.Equal("Done! Reminder set for 7 PM.", response.Message.Text);
        Assert.Equal(AiStopReason.Done, response.StopReason);

        var json = JsonNode.Parse(_received.Single().Body)!;
        var assistant = json["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("thinking", (string?)assistant[0]!["type"]);
        Assert.Equal("sig-123", (string?)assistant[0]!["signature"]);
        Assert.Equal("tool_use", (string?)assistant[2]!["type"]);
        Assert.Equal("Study", (string?)assistant[2]!["input"]!["title"]);
        var result = json["messages"]![2]!["content"]![0]!;
        Assert.Equal("tool_result", (string?)result["type"]);
        Assert.Equal("toolu_1", (string?)result["tool_use_id"]);
        Assert.Null(json["fallbacks"]); // only on the newest Opus/Fable models
        Assert.Equal("low", (string?)json["output_config"]!["effort"]);
    }

    [Fact]
    public async Task Streams_text_and_rebuilds_thinking_and_tool_calls()
    {
        _contentType = "text/event-stream";
        _reply = string.Join("\n\n", new[]
        {
            """event: message_start""" + "\n" + """data: {"type":"message_start","message":{"id":"msg_s","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":30,"output_tokens":1}}}""",
            """event: content_block_start""" + "\n" + """data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
            """event: content_block_delta""" + "\n" + """data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-9"}}""",
            """event: content_block_stop""" + "\n" + """data: {"type":"content_block_stop","index":0}""",
            """event: content_block_start""" + "\n" + """data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
            """event: content_block_delta""" + "\n" + """data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Setting "}}""",
            """event: content_block_delta""" + "\n" + """data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"it up."}}""",
            """event: content_block_stop""" + "\n" + """data: {"type":"content_block_stop","index":1}""",
            """event: content_block_start""" + "\n" + """data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_7","name":"create_note","input":{}}}""",
            """event: content_block_delta""" + "\n" + """data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"text\":"}}""",
            """event: content_block_delta""" + "\n" + """data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"milk\"}"}}""",
            """event: content_block_stop""" + "\n" + """data: {"type":"content_block_stop","index":2}""",
            """event: message_delta""" + "\n" + """data: {"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":42}}""",
            """event: message_stop""" + "\n" + """data: {"type":"message_stop"}""",
        }) + "\n\n";
        var pieces = new List<string>();

        var response = await Client().StreamAsync(new AiRequest("", [AiMessage.User("note milk")]), pieces.Add);

        Assert.Equal(["Setting ", "it up."], pieces);
        Assert.Equal("Setting it up.", response.Message.Text);
        Assert.Equal("sig-9", response.Message.Parts.OfType<AiThinking>().Single().Signature);
        var call = Assert.Single(response.Message.ToolCalls);
        Assert.Equal(("toolu_7", "create_note"), (call.Id, call.Name));
        Assert.Equal("milk", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("text").GetString());
        Assert.Equal(AiStopReason.ToolUse, response.StopReason);
        Assert.Equal(new AiUsage(30, 42), response.Usage);
        Assert.True((bool)JsonNode.Parse(_received.Single().Body)!["stream"]!);
    }

    [Fact]
    public async Task A_bad_key_is_explained()
    {
        _status = 401;
        _reply = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""";
        var ex = await Assert.ThrowsAsync<AiException>(() => Client().SendAsync(new AiRequest("", [AiMessage.User("hi")])));
        Assert.Equal(AiErrorKind.InvalidKey, ex.Kind);
        Assert.DoesNotContain("sk-ant-test", ex.Message);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var client = new AnthropicClaudeClient(new AiConnection(AiProviderKind.Anthropic, _baseUrl, "claude-opus-5-5", null), NullLogger.Instance);
        var ex = await Assert.ThrowsAsync<AiException>(() => client.SendAsync(new AiRequest("", [AiMessage.User("hi")])));
        Assert.Equal(AiErrorKind.NotConfigured, ex.Kind);
        Assert.Empty(_received);
    }

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-sonnet-4-5", false)]
    public void Effort_only_where_supported(string model, bool expected) =>
        Assert.Equal(expected, AnthropicClaudeClient.SupportsEffort(model));

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}

public sealed class DpapiSecretStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "MithuTests", Guid.NewGuid().ToString("N"), "secrets.dat");

    private DpapiSecretStore Store() => new(_path, NullLogger<DpapiSecretStore>.Instance);

    [Fact]
    public void Round_trips_encrypted()
    {
        Store().Set("ai.key", "sk-very-secret");
        Assert.Equal("sk-very-secret", Store().Get("ai.key"));
        Assert.DoesNotContain("sk-very-secret", File.ReadAllText(_path));
    }

    [Fact]
    public void Removing_and_missing()
    {
        Assert.Null(Store().Get("nothing"));
        Store().Set("ai.key", "x");
        Store().Set("ai.key", null);
        Assert.Null(Store().Get("ai.key"));
    }

    [Fact]
    public void A_damaged_file_is_set_aside()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{not json");
        Assert.Null(Store().Get("ai.key"));
        Store().Set("ai.key", "new");
        Assert.Equal("new", Store().Get("ai.key"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
