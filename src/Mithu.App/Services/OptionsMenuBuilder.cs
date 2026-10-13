using System.Windows;
using System.Windows.Controls;

namespace Mithu.App.Services;

/// <summary>Callbacks the options menu needs from the shell.</summary>
public interface IShellActions
{
    bool IsCompanionVisible { get; }

    string HotkeyText { get; }

    OptionsState GetOptions();

    void ToggleVisibility();

    void ShowCompanion();

    void SetDockToTop(bool enabled);

    void SetCloseOnClickAway(bool enabled);

    void SetAlwaysOnTop(bool enabled);

    void SetQuietMode(bool enabled);

    void SetReducedMotion(bool enabled);

    /// <summary>Thin border lines round the island and the face.</summary>
    void SetShowBorders(bool enabled)
    {
    }

    /// <summary>Open panel size: 1, 1.25 or 1.5.</summary>
    void SetPanelScale(double scale);

    /// <summary>Show what Claude Code is doing (adds or removes Mithu's hooks).</summary>
    void SetClaudeCode(bool enabled);

    /// <summary>Chime and peek when Claude Code finishes or needs the user.</summary>
    void SetClaudeNotify(bool enabled);

    /// <summary>Stay open in compact size while Claude Code works.</summary>
    void SetClaudeStayOpen(bool enabled);

    void SetLaunchAtLogin(bool enabled);

    /// <summary>Installed with an update address: Updates show in the menu.</summary>
    bool UpdatesAvailable => false;

    /// <summary>A downloaded version waiting to install ("0.2.0"), or null.</summary>
    string? UpdateReadyVersion => null;

    /// <summary>Checks for a new version now (and downloads it).</summary>
    void CheckForUpdates()
    {
    }

    /// <summary>Closes Mithu, installs the downloaded version and starts it.</summary>
    void RestartToUpdate()
    {
    }

    void SetCheckForUpdates(bool enabled)
    {
    }

    void ResetPosition();

    /// <summary>Opens the first-run setup again (name, AI, permissions).</summary>
    void RunSetup()
    {
    }

    /// <summary>Opens the AI settings page.</summary>
    void OpenAiSettings();

    /// <summary>Opens the Desktop control settings page.</summary>
    void OpenDesktopControl();

    void Exit();
}

/// <summary>Current option values shown as check marks.</summary>
public readonly record struct OptionsState(
    bool DockToTop, bool CloseOnClickAway, bool AlwaysOnTop, bool Quiet, bool ReducedMotion, bool LaunchAtLogin, double PanelScale = 1, bool ClaudeCode = false, bool ClaudeNotify = true, bool ClaudeStayOpen = false, bool CheckForUpdates = true, bool ShowBorders = false);

/// <summary>
/// Builds the dark options menu. The same menu is used by the ⚙ button and the tray icon,
/// so both always show identical, up-to-date choices.
/// </summary>
public static class OptionsMenuBuilder
{
    public static ContextMenu Build(IShellActions actions, bool includeShowHide)
    {
        var app = Application.Current;
        var menu = new ContextMenu { Style = (Style)app.FindResource("IslandMenu") };
        var itemStyle = (Style)app.FindResource("IslandMenuItem");
        var separatorStyle = (Style)app.FindResource("IslandMenuSeparator");
        var o = actions.GetOptions();

        MenuItem Item(string header, Action onClick, string? gesture = null)
        {
            var item = new MenuItem { Header = header, Style = itemStyle, InputGestureText = gesture ?? string.Empty };
            item.Click += (_, _) => onClick();
            return item;
        }

        MenuItem Check(string header, bool isChecked, Action<bool> onChanged, bool enabled = true)
        {
            var item = new MenuItem { Header = header, Style = itemStyle, IsCheckable = true, IsChecked = isChecked, IsEnabled = enabled };
            item.Click += (_, _) => onChanged(item.IsChecked);
            return item;
        }

        Separator NewSeparator() => new() { Style = separatorStyle };

        void Separator() => menu.Items.Add(NewSeparator());

        // A group of related settings, opening to the side (keeps the menu short).
        MenuItem Sub(string header, params Control[] items)
        {
            var sub = new MenuItem { Header = header, Style = itemStyle };
            foreach (var item in items)
            {
                sub.Items.Add(item);
            }

            return sub;
        }

        // Order (owner, 2026-10-13): what people reach for often first, then settings pages, then groups, then the rest.
        if (includeShowHide)
        {
            menu.Items.Add(Item(actions.IsCompanionVisible ? $"Hide {Companion.Name}" : $"Show {Companion.Name}", actions.ToggleVisibility, actions.HotkeyText));
            Separator();
        }

        menu.Items.Add(Item("AI settings…", actions.OpenAiSettings));
        menu.Items.Add(Item("Desktop control…", actions.OpenDesktopControl));
        menu.Items.Add(Sub("Claude Code",
            Check("Show what Claude Code is doing", o.ClaudeCode, actions.SetClaudeCode),
            Check("Stay open while it works", o.ClaudeStayOpen, actions.SetClaudeStayOpen, enabled: o.ClaudeCode),
            Check("Tell me when it finishes or needs me", o.ClaudeNotify, actions.SetClaudeNotify, enabled: o.ClaudeCode)));
        Separator();

        menu.Items.Add(Sub("Size & look",
            [
                .. new[] { ("Normal (1×)", 1.0), ("Bigger (1.25×)", 1.25), ("Biggest (1.5×)", 1.5) }
                    .Select(c => (Control)Check(c.Item1, Math.Abs(o.PanelScale - c.Item2) < 0.01, _ => actions.SetPanelScale(c.Item2))),
                NewSeparator(),
                Check("Show border lines", o.ShowBorders, actions.SetShowBorders),
                Check("Reduce motion", o.ReducedMotion, actions.SetReducedMotion),
            ]));
        menu.Items.Add(Sub("Position",
            Check("Stick to top edge", o.DockToTop, actions.SetDockToTop),
            Check("Always on top", o.AlwaysOnTop, actions.SetAlwaysOnTop),
            Check("Tuck away when I click elsewhere", o.CloseOnClickAway, actions.SetCloseOnClickAway),
            NewSeparator(),
            Item("Reset position", actions.ResetPosition)));
        menu.Items.Add(Check("Start with Windows", o.LaunchAtLogin, actions.SetLaunchAtLogin));
        Separator();

        if (actions.UpdatesAvailable)
        {
            menu.Items.Add(Sub(actions.UpdateReadyVersion is { } ready ? $"Updates · {ready} ready" : "Updates",
                actions.UpdateReadyVersion is { } version
                    ? Item($"Restart to update to {version}", actions.RestartToUpdate)
                    : Item("Check for updates now", actions.CheckForUpdates),
                Check("Check automatically", o.CheckForUpdates, actions.SetCheckForUpdates)));
        }

        menu.Items.Add(Item("Run setup again…", actions.RunSetup));
        Separator();
        menu.Items.Add(Check("Quiet mode", o.Quiet, actions.SetQuietMode));
        menu.Items.Add(Item($"Exit {Companion.Name}", actions.Exit));

        // The version, quietly at the very bottom (not clickable).
        var appVersion = typeof(OptionsMenuBuilder).Assembly.GetName().Version?.ToString(3) ?? "?";
        menu.Items.Add(new MenuItem { Header = $"Mithu {appVersion}", Style = (Style)app.FindResource("IslandMenuHeader") });
        return menu;
    }
}
