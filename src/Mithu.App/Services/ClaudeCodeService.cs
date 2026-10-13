using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Extensions.Logging;
using Mithu.App.ViewModels;
using Mithu.Core.ClaudeCode;
using Mithu.Core.Interfaces;
using Mithu.Infrastructure.ClaudeCode;

namespace Mithu.App.Services;

/// <summary>
/// "Show what Claude Code is doing": turning it on adds Mithu's hooks to Claude Code's settings and starts the
/// local listener; turning it off takes the hooks out and stops listening. Events go to
/// <see cref="ClaudeCodeViewModel"/> on the UI thread.
/// </summary>
public sealed class ClaudeCodeService(
    ISettingsStore settings,
    ClaudeCodeListener listener,
    ClaudeCodeViewModel status,
    ILogger<ClaudeCodeService> logger) : IDisposable
{
    private readonly ClaudeCodeHooks _hooks = new(ClaudeCodeHooks.DefaultPath);

    public ClaudeCodeSettings Settings => settings.Current.ClaudeCode;

    /// <summary>Claude Code's settings file (shown to the user).</summary>
    public string HooksFile => _hooks.SettingsPath;

    /// <summary>At startup: listen again if it was on (and put the hooks back if they went missing).</summary>
    public void Start()
    {
        listener.Received += json => Application.Current?.Dispatcher.BeginInvoke(() => status.OnEvent(json));
        if (Settings.Enabled)
        {
            TryEnable(out _);
        }
    }

    /// <summary>Turns it on: hooks in, listener up. Returns false with a reason the user can read.</summary>
    public bool TryEnable(out string message)
    {
        var token = Settings.Token.Length >= 16 ? Settings.Token : Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            if (!_hooks.IsInstalled(token))
            {
                _hooks.Install(token);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(ex, "Couldn't add Mithu's hooks to Claude Code");
            message = ex is InvalidDataException ? ex.Message : "Couldn't update Claude Code's settings file.";
            return false;
        }

        settings.Update(s => s with { ClaudeCode = s.ClaudeCode with { Enabled = true, Token = token } });
        if (!listener.Start(ClaudeCodeSettings.Port, ClaudeCodeHooks.PathFor(token)))
        {
            message = $"Port {ClaudeCodeSettings.Port} is busy, so {Companion.Name} can't hear Claude Code right now.";
            return false;
        }

        message = "I'll show what Claude Code is doing. New Claude Code sessions report right away.";
        return true;
    }

    /// <summary>Turns it off: hooks out, listener down, nothing shown.</summary>
    public string Disable()
    {
        listener.Stop();
        status.Clear();
        settings.Update(s => s with { ClaudeCode = s.ClaudeCode with { Enabled = false } });
        try
        {
            _hooks.Uninstall();
            return "Claude Code status is off (Mithu's hooks were removed).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(ex, "Couldn't remove Mithu's hooks from Claude Code");
            return "Stopped listening, but couldn't edit Claude Code's settings file.";
        }
    }

    public void Dispose() => listener.Dispose();
}
