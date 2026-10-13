using Microsoft.Extensions.Logging;

namespace Mithu.App.Services;

/// <summary>The screenshot shortcut (Ctrl+Shift+S): a second system-wide shortcut with its own id.</summary>
public sealed class ScreenshotHotkeyService(ILogger<GlobalHotkeyService> logger) : GlobalHotkeyService(logger)
{
    protected override int HotkeyId => 0x4D4A; // "MJ"
}