using H.NotifyIcon;

namespace Mithu.App.Services;

/// <summary>
/// System tray icon. Left-click shows Mithu; right-click opens the same dark options menu as the ⚙ button.
/// The menu is rebuilt on each open so check marks are always current.
/// </summary>
public sealed class TrayService : IDisposable
{
    private TaskbarIcon? _icon;
    private IShellActions? _actions;

    public void Create(IShellActions actions)
    {
        _actions = actions;
        _icon = new TaskbarIcon
        {
            ToolTipText = ToolTip(actions.HotkeyText),
            Icon = TrayIconFactory.CreateIcon(),
            ContextMenu = OptionsMenuBuilder.Build(actions, includeShowHide: true),
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => actions.ShowCompanion();
        _icon.TrayRightMouseDown += (_, _) => Refresh();
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>Rebuilds the menu so it reflects current settings.</summary>
    public void Refresh()
    {
        if (_icon is null || _actions is null)
        {
            return;
        }

        _icon.ContextMenu = OptionsMenuBuilder.Build(_actions, includeShowHide: true);
        _icon.ToolTipText = ToolTip(_actions.HotkeyText);
    }

    private static string ToolTip(string hotkey) => $"{Companion.Name} — your little desktop dost ({hotkey})";

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
