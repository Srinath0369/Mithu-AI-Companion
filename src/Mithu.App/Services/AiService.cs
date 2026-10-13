using Microsoft.Extensions.Logging;
using Mithu.Core.Ai;
using Mithu.Core.Interfaces;

namespace Mithu.App.Services;

/// <summary>Result of "Test connection": the models the provider offers, or why it failed.</summary>
public sealed record AiTestResult(bool Ok, IReadOnlyList<string> Models, string Message);

/// <summary>
/// The app's view of AI: current settings, the (encrypted) key per provider, connection tests, and a client for
/// the chat. Nothing is contacted until the user tests or uses AI. Never logs keys or content.
/// </summary>
public sealed class AiService(ISettingsStore settings, ISecretStore secrets, IAiClientFactory clients, ILogger<AiService> logger)
{
    public AiSettings Settings => settings.Current.Ai;

    /// <summary>AI is on and usable (the Home input goes to the AI).</summary>
    public bool IsReady => Settings.IsReady && (!NeedsKey(Settings.PresetId) || HasKey(Settings.PresetId));

    public bool HasKey(string presetId) => !string.IsNullOrEmpty(GetKey(presetId));

    /// <summary>Last four characters of the saved key ("••••1a2b"), or null when none is saved.</summary>
    public string? KeyHint(string presetId) =>
        GetKey(presetId) is { Length: > 0 } key ? "••••" + key[^Math.Min(4, key.Length)..] : null;

    public static bool NeedsKey(string presetId) => AiPresets.Find(presetId)?.NeedsKey ?? true;

    /// <summary>Saves (or with null, removes) a provider's key, encrypted.</summary>
    public void SetKey(string presetId, string? key) => secrets.Set(AiSettings.KeyName(presetId), key?.Trim());

    public void Update(Func<AiSettings, AiSettings> change) => settings.Update(s => s with { Ai = change(s.Ai) });

    /// <summary>A client for the saved settings (for the chat).</summary>
    public IAiClient CreateClient() => clients.Create(ConnectionFor(Settings, GetKey(Settings.PresetId)));

    /// <summary>Is the network down for this provider? (Local servers like Ollama work offline, so never for them.)</summary>
    public Func<bool> IsOffline { get; set; } = () => !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

    /// <summary>True when the provider is on the internet and there is no network right now: fail at once, no waiting.</summary>
    public bool ProviderUnreachable =>
        Uri.TryCreate(Settings.BaseUrl, UriKind.Absolute, out var uri) && !uri.IsLoopback
        && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && IsOffline();

    /// <summary>
    /// "Test connection": asks the provider for its model list, which checks the address and key without
    /// spending any tokens. A server without a model list still counts as reachable.
    /// </summary>
    /// <param name="key">A newly typed key, or null to use the one saved for that provider.</param>
    public async Task<AiTestResult> TestAsync(AiSettings draft, string? key, CancellationToken cancellationToken = default)
    {
        key ??= GetKey(draft.PresetId);
        if (draft.BaseUrl.Trim().Length == 0)
        {
            return new AiTestResult(false, [], "Enter the server address first.");
        }

        if (NeedsKey(draft.PresetId) && string.IsNullOrWhiteSpace(key))
        {
            return new AiTestResult(false, [], "Paste your API key first.");
        }

        try
        {
            var models = await clients.Create(ConnectionFor(draft, key)).ListModelsAsync(cancellationToken);
            logger.LogInformation("AI connection test OK ({Kind}, {Count} models)", draft.Kind, models.Count);
            return new AiTestResult(true, models, models.Count > 0 ? $"Connected ✓ · {models.Count} models available" : "Connected ✓");
        }
        catch (AiException ex) when (ex.Kind == AiErrorKind.NotFound && draft.Kind == AiProviderKind.OpenAiCompatible)
        {
            // Some compatible servers have no /models: the address answered, so let the user type the model.
            return new AiTestResult(true, [], "Connected, but this server doesn't list its models — type the model name.");
        }
        catch (AiException ex)
        {
            logger.LogInformation("AI connection test failed: {Kind}", ex.Kind);
            return new AiTestResult(false, [], ex.Message.Replace(AiException.SettingsHint, string.Empty, StringComparison.Ordinal));
        }
    }

    private string? GetKey(string presetId) => secrets.Get(AiSettings.KeyName(presetId));

    private static AiConnection ConnectionFor(AiSettings s, string? key) =>
        new(s.Kind, s.BaseUrl.Trim(), s.Model.Trim(), string.IsNullOrWhiteSpace(key) ? null : key.Trim());
}
