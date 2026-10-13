namespace Mithu.Core.Ai;

/// <summary>
/// The user's AI choices (part of <see cref="Models.AppSettings"/>). The API key is NOT here: it lives encrypted in
/// <see cref="Interfaces.ISecretStore"/> under <see cref="KeyName"/>.
/// </summary>
public sealed record AiSettings
{
    /// <summary>AI answers the Home input and the Ask page. Only true after consent and a provider are set.</summary>
    public bool Enabled { get; init; }

    /// <summary>Which preset was chosen (<see cref="AiPresets"/>): "claude", "openai", "ollama", "custom"…</summary>
    public string PresetId { get; init; } = "claude";

    public AiProviderKind Kind { get; init; } = AiProviderKind.Anthropic;

    public string BaseUrl { get; init; } = "https://api.anthropic.com";

    public string Model { get; init; } = AiPresets.DefaultClaudeModel;

    /// <summary>The user has read what AI sends and where (first-use notice).</summary>
    public bool ConsentGiven { get; init; }

    /// <summary>The AI may search the user's notes (short snippets are sent to the provider). Off by default.</summary>
    public bool AllowNotes { get; init; }

    /// <summary>
    /// Ctrl+Shift+S takes a screenshot to ask Mithu about (on by default; the 👀 button in the chat always works).
    /// Nothing is sent until the user sends the message with the screenshot attached.
    /// </summary>
    public bool ScreenshotShortcut { get; init; } = true;

    /// <summary>
    /// Mithu may take a screenshot itself when the user asks in chat ("look at my screen", "explain this"). The
    /// chat shows each one. On by default.
    /// </summary>
    public bool LookAtScreen { get; init; } = true;

    /// <summary>The screenshot shortcut (system-wide).</summary>
    public const string ScreenshotHotkey = "Ctrl+Shift+S";

    /// <summary>Ready to use: on, consented, and an address and model set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsReady => Enabled && ConsentGiven && BaseUrl.Trim().Length > 0 && Model.Trim().Length > 0;

    /// <summary>Secret-store name of a preset's key (each provider remembers its own key).</summary>
    public static string KeyName(string presetId) => $"ai.key.{presetId}";
}
