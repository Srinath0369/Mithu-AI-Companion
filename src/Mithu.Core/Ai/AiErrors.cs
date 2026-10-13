namespace Mithu.Core.Ai;

/// <summary>Why an AI request failed, in terms Mithu can explain.</summary>
public enum AiErrorKind
{
    /// <summary>No provider, model or key set up yet.</summary>
    NotConfigured,

    /// <summary>401/403: the key is wrong, revoked or lacks access.</summary>
    InvalidKey,

    /// <summary>404: the model name (or base URL) doesn't exist.</summary>
    NotFound,

    /// <summary>429: too many requests or out of credit.</summary>
    RateLimited,

    /// <summary>400/422: the provider rejected the request.</summary>
    BadRequest,

    /// <summary>5xx or overloaded.</summary>
    ServerError,

    /// <summary>No network / the server can't be reached.</summary>
    Offline,

    /// <summary>The provider took too long.</summary>
    Timeout,

    /// <summary>Anything else (e.g. a reply Mithu couldn't read).</summary>
    Unknown,
}

/// <summary>A failed AI request. <see cref="Exception.Message"/> is safe to show; it never contains the key.</summary>
public sealed class AiException(AiErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public AiErrorKind Kind { get; } = kind;

    /// <summary>Appended where the fix is in Settings (left out on the Settings page itself).</summary>
    public const string SettingsHint = " Check it in Settings.";

    /// <summary>A short line Mithu can say.</summary>
    public static string Friendly(AiErrorKind kind) => kind switch
    {
        AiErrorKind.NotConfigured => "Set up AI in Settings first.",
        AiErrorKind.InvalidKey => "That API key didn't work." + SettingsHint,
        AiErrorKind.NotFound => "That model or address wasn't found." + SettingsHint,
        AiErrorKind.RateLimited => "The AI service says slow down (or the account is out of credit). Try again in a moment.",
        AiErrorKind.BadRequest => "The AI service couldn't handle that request.",
        AiErrorKind.ServerError => "The AI service is having trouble right now. Try again soon.",
        AiErrorKind.Offline => "I can't reach the AI service. Are you online (or is the local server running)?",
        AiErrorKind.Timeout => "The AI service took too long to answer.",
        _ => "Something went wrong talking to the AI service.",
    };
}
