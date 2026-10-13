using System.Reflection;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Mithu.Core.Interfaces;
using Velopack;

namespace Mithu.App.Services;

/// <summary>
/// Updates for an installed Mithu (Velopack). The update address is built in when the installer is made
/// (<c>scripts/package.ps1 -UpdateUrl</c>); without one, or when Mithu runs from a build folder, updates are off and
/// the menu doesn't show them. A check downloads only the release list and, if newer, the package (nothing about
/// the user is sent). A downloaded update installs when Mithu closes, or right away with "Restart to update".
/// Automatic checks (on by default, ⋯ → Updates): 2 minutes after start, then once a day while running.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Daily = TimeSpan.FromHours(24);

    private readonly ISettingsStore _settings;
    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateManager? _manager;
    private DispatcherTimer? _timer;
    private UpdateInfo? _ready;
    private bool _checking;

    public UpdateService(ISettingsStore settings, ILogger<UpdateService> logger)
    {
        _settings = settings;
        _logger = logger;
        try
        {
            _manager = FeedUrl is { } url ? new UpdateManager(url) : null;
            var installed = _manager?.IsInstalled ?? false;
            _logger.LogInformation("Updates: address {HasAddress}, installed copy {Installed}", FeedUrl is not null, installed);
            if (!installed)
            {
                _manager = null; // a build folder, not an installed copy: nothing to update
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updates unavailable");
            _manager = null;
        }
    }

    /// <summary>A new version was downloaded (its version): it installs when Mithu closes.</summary>
    public event Action<string>? Ready;

    /// <summary>The update address built into this copy, or null.</summary>
    public static string? FeedUrl =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "MithuUpdateUrl")?.Value is { Length: > 0 } url ? url : null;

    /// <summary>Installed with an update address: the ⋯ menu shows Updates.</summary>
    public bool IsAvailable => _manager is not null;

    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "?";

    public void Start()
    {
        if (_manager is null)
        {
            return;
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FirstCheck };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Daily;
            if (_settings.Current.CheckForUpdates)
            {
                await CheckAsync();
            }
        };
        _timer.Start();
    }

    /// <summary>Checks now and downloads a newer version; returns a line for the status text.</summary>
    public async Task<string> CheckAsync()
    {
        if (_manager is null)
        {
            return "Updates aren't set up for this copy of Mithu.";
        }

        if (_ready is not null)
        {
            return $"Mithu {ReadyVersion} is ready: it installs when Mithu restarts.";
        }

        if (_checking)
        {
            return "Already checking for updates…";
        }

        _checking = true;
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                _logger.LogInformation("Update check: up to date ({Version})", CurrentVersion);
                return $"You have the latest Mithu ({CurrentVersion}).";
            }

            await _manager.DownloadUpdatesAsync(update);
            _ready = update;
            _logger.LogInformation("Update {Version} downloaded", ReadyVersion);
            Ready?.Invoke(ReadyVersion!);
            return $"Mithu {ReadyVersion} is ready: it installs when Mithu restarts.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed");
            return "Couldn't check for updates right now (offline?).";
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Mithu is closing (or restarting to update): hands the downloaded update to Velopack's updater, which waits
    /// for Mithu to exit, installs it and, when <paramref name="restart"/>, starts the new version.
    /// </summary>
    public void ApplyOnExit(bool restart = false)
    {
        if (_manager is null || _ready is null)
        {
            return;
        }

        try
        {
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: restart);
            _logger.LogInformation("Update {Version} will install on exit (restart: {Restart})", ReadyVersion, restart);
            _ready = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't hand the update over");
        }
    }

    public void Dispose() => _timer?.Stop();
}
