using Mithu.Core.Ai;

namespace Mithu.Core.Interfaces;

/// <summary>One AI provider connection. Throws <see cref="AiException"/> on failure (never a raw HTTP exception).</summary>
public interface IAiClient
{
    Task<AiResponse> SendAsync(AiRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Like <see cref="SendAsync"/>, but the answer's text is handed to <paramref name="onText"/> piece by piece as
    /// it is written (for a typing effect). Returns the complete answer. Clients that can't stream send it whole.
    /// </summary>
    async Task<AiResponse> StreamAsync(AiRequest request, Action<string> onText, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(request, cancellationToken);
        if (response.Message.Text.Length > 0)
        {
            onText(response.Message.Text);
        }

        return response;
    }

    /// <summary>Model ids the provider offers (for the Settings picker and "Test connection").</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Makes a client for a connection (provider kind, base URL, model, key).</summary>
public interface IAiClientFactory
{
    IAiClient Create(AiConnection connection);
}

/// <summary>Small secrets (API keys), encrypted for the current Windows user. Never logged.</summary>
public interface ISecretStore
{
    string? Get(string name);

    /// <summary>Stores <paramref name="value"/>, or removes the secret when it is null or empty.</summary>
    void Set(string name, string? value);
}
