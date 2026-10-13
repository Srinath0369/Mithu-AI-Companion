using Microsoft.Extensions.Logging;
using Mithu.Core.Ai;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Ai;

/// <summary>
/// Makes the right client for a connection. One shared <see cref="HttpClient"/> for OpenAI-compatible servers
/// (connections recycled every few minutes so DNS changes are picked up). Nothing is created until AI is used.
/// </summary>
public sealed class AiClientFactory(ILoggerFactory loggers) : IAiClientFactory, IDisposable
{
    private readonly Lazy<HttpClient> _http = new(() => new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // each attempt has its own timeout
    });

    public IAiClient Create(AiConnection connection) => connection.Kind switch
    {
        AiProviderKind.Anthropic => new AnthropicClaudeClient(connection, loggers.CreateLogger<AnthropicClaudeClient>()),
        _ => new OpenAiCompatibleClient(_http.Value, connection, loggers.CreateLogger<OpenAiCompatibleClient>()),
    };

    public void Dispose()
    {
        if (_http.IsValueCreated)
        {
            _http.Value.Dispose();
        }
    }
}
