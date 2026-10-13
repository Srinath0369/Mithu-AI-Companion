namespace Mithu.Core.ClaudeCode;

/// <summary>Showing what Claude Code is doing (part of <see cref="Models.AppSettings"/>). Off by default.</summary>
public sealed record ClaudeCodeSettings
{
    /// <summary>Mithu listens for Claude Code's status (and its hooks are added to Claude Code's settings).</summary>
    public bool Enabled { get; init; }

    /// <summary>A chime and a peek when a request finishes or Claude Code needs the user.</summary>
    public bool Notify { get; init; } = true;

    /// <summary>While it works, Mithu stays open in compact size (clicking elsewhere doesn't tuck it away).</summary>
    public bool StayOpen { get; init; }

    /// <summary>A random secret in the address the hooks post to, so nothing else on the PC can pretend to be Claude Code.</summary>
    public string Token { get; init; } = string.Empty;

    /// <summary>The local port Mithu listens on (127.0.0.1 only).</summary>
    public const int Port = 47613;
}
