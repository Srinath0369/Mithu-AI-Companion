namespace Mithu.Core.Ai;

/// <summary>
/// A ready-made provider choice for Settings. <see cref="NeedsKey"/>: a key is required (cloud providers).
/// <see cref="AcceptsKey"/>: the key field is shown (cloud providers, and "Other" where it is optional).
/// </summary>
public sealed record AiPreset(string Id, string Name, AiProviderKind Kind, string BaseUrl, string SuggestedModel, bool NeedsKey, string KeyHint)
{
    public bool AcceptsKey => NeedsKey || Id == "custom";
}

/// <summary>Providers Mithu knows how to reach (bring your own key). Any other OpenAI-compatible server works via Custom.</summary>
public static class AiPresets
{
    /// <summary>Claude's default: the current Opus. Users can pick Sonnet or Haiku in Settings.</summary>
    public const string DefaultClaudeModel = "claude-opus-5-5";

    public static IReadOnlyList<AiPreset> All { get; } =
    [
        new("claude", "Claude (Anthropic)", AiProviderKind.Anthropic, "https://api.anthropic.com", DefaultClaudeModel, true,
            "From console.anthropic.com → API keys"),
        new("openai", "OpenAI", AiProviderKind.OpenAiCompatible, "https://api.openai.com/v1", string.Empty, true,
            "From platform.openai.com → API keys"),
        new("openrouter", "OpenRouter", AiProviderKind.OpenAiCompatible, "https://openrouter.ai/api/v1", string.Empty, true,
            "From openrouter.ai → Keys"),
        new("groq", "Groq", AiProviderKind.OpenAiCompatible, "https://api.groq.com/openai/v1", string.Empty, true,
            "From console.groq.com → API keys"),
        new("gemini", "Google Gemini", AiProviderKind.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai", string.Empty, true,
            "From aistudio.google.com → API key"),
        new("ollama", "Ollama (on this PC)", AiProviderKind.OpenAiCompatible, "http://localhost:11434/v1", string.Empty, false,
            "No key needed"),
        new("lmstudio", "LM Studio (on this PC)", AiProviderKind.OpenAiCompatible, "http://localhost:1234/v1", string.Empty, false,
            "No key needed"),
        new("custom", "Other (OpenAI-compatible)", AiProviderKind.OpenAiCompatible, string.Empty, string.Empty, false,
            "Optional — only if the server asks for one"),
    ];

    public static AiPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}
