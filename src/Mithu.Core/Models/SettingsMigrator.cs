using Mithu.Core.Layout;

namespace Mithu.Core.Models;

/// <summary>Upgrades settings saved by older versions so users keep their position and preferences.</summary>
public static class SettingsMigrator
{
    /// <summary>Returns settings at <see cref="AppSettings.CurrentVersion"/>; <paramref name="migrated"/> tells whether anything changed.</summary>
    public static AppSettings Migrate(AppSettings settings, out bool migrated)
    {
        migrated = false;
        var s = settings;

        if (s.Version < 2)
        {
            // v2 introduced the notch design: stick to the top and close when not in use by default.
            s = s with
            {
                Version = 2,
                DockToTop = true,
                CloseOnClickAway = true,
                LastVisibleMode = ModeNavigator.IsVisibleSize(s.LastMode) ? s.LastMode : WindowMode.Expanded,
            };
            migrated = true;
        }

        if (s.Version < 3)
        {
            // v3 replaced hover-to-open/auto-hide with click-to-open/click-away-to-close.
            // The old "autoHideWhenDocked" value is carried over by JsonSettingsStore.UpgradeLegacyJson.
            s = s with { Version = 3 };
            migrated = true;
        }

        // Defensive: never restore into a transient size.
        if (s.LastMode == WindowMode.Peek)
        {
            s = s with { LastMode = WindowMode.Compact };
            migrated = true;
        }

        if (!ModeNavigator.IsVisibleSize(s.LastVisibleMode))
        {
            s = s with { LastVisibleMode = WindowMode.Expanded };
            migrated = true;
        }

        return s;
    }
}
