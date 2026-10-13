using Mithu.App.Services;
using Velopack;

namespace Mithu.App;

/// <summary>
/// Entry point. Velopack runs first: when the installer or uninstaller starts Mithu to run its install hooks, it
/// handles that and exits before any window or tray icon appears. Then the normal WPF app starts.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => UninstallCleanup.Run())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
