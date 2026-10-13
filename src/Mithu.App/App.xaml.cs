using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mithu.App.Animation;
using Mithu.App.Services;
using Mithu.App.ViewModels;
using Mithu.App.Views;
using Mithu.Core.Interfaces;
using Mithu.Infrastructure;
using Mithu.Infrastructure.Ai;
using Mithu.Infrastructure.Focus;
using Mithu.Infrastructure.Logging;
using Mithu.Infrastructure.Notifications;
using Mithu.Infrastructure.Persistence;
using Mithu.Infrastructure.Settings;
using Mithu.Infrastructure.Windows;

namespace Mithu.App;

public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private IHost? _host;
    private ILogger<App>? _logger;
    private ShellController? _shell;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Themes.AppFonts.UseAsDefault(); // Segoe UI Variable everywhere, before any window exists

        // 1. Single instance: a second launch wakes the first one and exits immediately.
        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.IsFirstInstance)
        {
            _instanceGuard.SignalFirstInstance();
            Shutdown();
            return;
        }

        // 2. Local data folders.
        var paths = new AppPaths();
        paths.EnsureCreated();

        // 3. Generic Host: dependency injection + logging + lifetime.
        _host = BuildHost(paths);
        await _host.StartAsync();

        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        RegisterGlobalExceptionHandlers();

        // 4. Start the companion shell (floating window, tray, hotkey).
        _shell = _host.Services.GetRequiredService<ShellController>();
        // Windows starts Mithu with -ToastActivated when a reminder notification is clicked while it isn't running.
        _shell.Start(launchedAtStartup: e.Args.Contains("--startup"), launchedFromNotification: e.Args.Contains("-ToastActivated"));

        _instanceGuard.ListenForActivation(() =>
            Dispatcher.BeginInvoke(_shell.ShowCompanion));

        _logger.LogInformation(
            "Mithu {Version} started (pid {Pid}, .NET {Runtime})",
            typeof(App).Assembly.GetName().Version,
            Environment.ProcessId,
            Environment.Version);
    }

    private static IHost BuildHost(IAppPaths paths)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
#if DEBUG
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
#endif
        builder.Logging.AddProvider(new FileLoggerProvider(paths.LogDirectory, TimeProvider.System));

        // Core / infrastructure
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(paths);
        // Everything lives in mithu.db; the old JSON files are imported once and renamed *.migrated.
        builder.Services.AddSingleton(sp =>
            new SqliteDatabase(paths.DatabasePath, sp.GetRequiredService<ILogger<SqliteDatabase>>()));
        builder.Services.AddSingleton<ISettingsStore>(sp => new SqliteSettingsStore(
            sp.GetRequiredService<SqliteDatabase>(), paths.SettingsPath, sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<INoteRepository, SqliteNoteRepository>();
        builder.Services.AddSingleton<IFocusStore>(sp => new SqliteFocusStore(
            sp.GetRequiredService<SqliteDatabase>(), paths.FocusPath, sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton<IReminderRepository, SqliteReminderRepository>();
        builder.Services.AddSingleton<IReminderNotifier, ToastReminderNotifier>();
        // AI (bring your own key): nothing is created or contacted until the user sets AI up.
        builder.Services.AddSingleton<ISecretStore>(sp => new DpapiSecretStore(
            System.IO.Path.Combine(paths.DataDirectory, "secrets.dat"), sp.GetRequiredService<ILogger<DpapiSecretStore>>()));
        builder.Services.AddSingleton<IAiClientFactory, AiClientFactory>();
        // Screenshots kept with chats, and text recognition on this PC (works with AI off).
        builder.Services.AddSingleton<IImageStore>(_ => new Mithu.Infrastructure.Vision.FileImageStore(System.IO.Path.Combine(paths.DataDirectory, "images")));
        builder.Services.AddSingleton<ITextRecognizer, Mithu.Infrastructure.Vision.WindowsTextRecognizer>();
        builder.Services.AddSingleton<IMonitorProvider, Win32MonitorProvider>();
        builder.Services.AddSingleton<IStartupRegistration, RegistryStartupRegistration>();
        builder.Services.AddSingleton<FullscreenWatcher>();

        // Presentation
        builder.Services.AddSingleton<FaceAnimationCoordinator>();
        builder.Services.AddSingleton<ClickReactionDriver>();
        builder.Services.AddSingleton<WindowPlacementService>();
        builder.Services.AddSingleton<GlobalHotkeyService>();
        builder.Services.AddSingleton<ScreenshotHotkeyService>();
        builder.Services.AddSingleton<TrayService>();
        builder.Services.AddSingleton<FocusViewModel>();
        builder.Services.AddSingleton<NotesViewModel>();
        builder.Services.AddSingleton<ReminderService>();
        builder.Services.AddSingleton<RemindersViewModel>();
        builder.Services.AddSingleton<AiService>();
        builder.Services.AddSingleton<AiSettingsViewModel>();
        builder.Services.AddSingleton<IChatRepository, SqliteChatRepository>();
        builder.Services.AddSingleton<AssistantTools>();
        builder.Services.AddSingleton<ITodoRepository, SqliteTodoRepository>();
        builder.Services.AddSingleton<TodosViewModel>();
        builder.Services.AddSingleton<TodoTools>();
        // Claude Code status (off until turned on in the ⋯ menu).
        builder.Services.AddSingleton<Mithu.Infrastructure.ClaudeCode.ClaudeCodeListener>();
        builder.Services.AddSingleton<Mithu.Infrastructure.ClaudeCode.TranscriptTail>();
        builder.Services.AddSingleton<ClaudeCodeViewModel>();
        builder.Services.AddSingleton<ClaudeCodeService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<SetupViewModel>();
        // Desktop control (Phase 5.2): off until the user turns it on; every path checked, every action shown.
        builder.Services.AddSingleton<IDesktop, Mithu.Infrastructure.Desktop.WindowsDesktop>();
        builder.Services.AddSingleton<ActionApprovals>();
        builder.Services.AddSingleton<IClipboardAccess, WindowsClipboard>();
        builder.Services.AddSingleton<IActionLog, Mithu.Infrastructure.Persistence.SqliteActionLog>();
        builder.Services.AddSingleton<DesktopTools>();
        builder.Services.AddSingleton<DesktopControlViewModel>();
        builder.Services.AddSingleton<IAssistantToolbox, Toolbox>();
        builder.Services.AddSingleton<AskViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<FloatingWindow>();
        builder.Services.AddSingleton<ShellController>();

        return builder.Build();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        // Keep the companion alive for recoverable UI errors; a friendly error UI comes later.
        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _logger?.LogInformation("Mithu shutting down");
            _shell?.Dispose();
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose(); // Also flushes the file logger.
        }

        _instanceGuard?.Dispose();
        base.OnExit(e);
    }
}

