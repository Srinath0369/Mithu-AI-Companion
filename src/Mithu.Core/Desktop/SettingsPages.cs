namespace Mithu.Core.Desktop;

/// <summary>
/// The Windows Settings pages Mithu may open (an allow-list of <c>ms-settings:</c> addresses). Opening a page
/// changes nothing: the user makes any change themselves.
/// </summary>
public static class SettingsPages
{
    public static readonly IReadOnlyDictionary<string, (string Uri, string Label)> All =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["display"] = ("ms-settings:display", "Display"),
            ["night-light"] = ("ms-settings:nightlight", "Night light"),
            ["sound"] = ("ms-settings:sound", "Sound"),
            ["notifications"] = ("ms-settings:notifications", "Notifications"),
            ["focus"] = ("ms-settings:quiethours", "Focus / Do not disturb"),
            ["power"] = ("ms-settings:powersleep", "Power & sleep"),
            ["battery"] = ("ms-settings:batterysaver", "Battery"),
            ["storage"] = ("ms-settings:storagesense", "Storage"),
            ["multitasking"] = ("ms-settings:multitasking", "Multitasking"),
            ["clipboard"] = ("ms-settings:clipboard", "Clipboard"),
            ["about"] = ("ms-settings:about", "About"),
            ["bluetooth"] = ("ms-settings:bluetooth", "Bluetooth & devices"),
            ["printers"] = ("ms-settings:printers", "Printers & scanners"),
            ["mouse"] = ("ms-settings:mousetouchpad", "Mouse"),
            ["touchpad"] = ("ms-settings:devices-touchpad", "Touchpad"),
            ["typing"] = ("ms-settings:typing", "Typing"),
            ["wifi"] = ("ms-settings:network-wifi", "Wi-Fi"),
            ["network"] = ("ms-settings:network-status", "Network & internet"),
            ["vpn"] = ("ms-settings:network-vpn", "VPN"),
            ["personalization"] = ("ms-settings:personalization", "Personalization"),
            ["background"] = ("ms-settings:personalization-background", "Background"),
            ["colors"] = ("ms-settings:colors", "Colors"),
            ["themes"] = ("ms-settings:themes", "Themes"),
            ["lock-screen"] = ("ms-settings:lockscreen", "Lock screen"),
            ["taskbar"] = ("ms-settings:taskbar", "Taskbar"),
            ["apps"] = ("ms-settings:appsfeatures", "Installed apps"),
            ["default-apps"] = ("ms-settings:defaultapps", "Default apps"),
            ["startup-apps"] = ("ms-settings:startupapps", "Startup apps"),
            ["accounts"] = ("ms-settings:yourinfo", "Your info"),
            ["date-time"] = ("ms-settings:dateandtime", "Date & time"),
            ["language"] = ("ms-settings:regionlanguage", "Language & region"),
            ["privacy"] = ("ms-settings:privacy", "Privacy & security"),
            ["camera"] = ("ms-settings:privacy-webcam", "Camera permissions"),
            ["microphone"] = ("ms-settings:privacy-microphone", "Microphone permissions"),
            ["windows-update"] = ("ms-settings:windowsupdate", "Windows Update"),
            ["accessibility"] = ("ms-settings:easeofaccess", "Accessibility"),
        };

    /// <summary>The page for a key ("wifi", "Wi-Fi", "windows update" all work), or null.</summary>
    public static (string Uri, string Label)? Find(string? key)
    {
        var k = (key ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        if (All.TryGetValue(k, out var page))
        {
            return page;
        }

        var compact = k.Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var (name, value) in All)
        {
            if (name.Replace("-", string.Empty, StringComparison.Ordinal) == compact
                || value.Label.ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal) == compact)
            {
                return value;
            }
        }

        return null;
    }
}
