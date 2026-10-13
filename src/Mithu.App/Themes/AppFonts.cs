using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Mithu.App.Themes;

/// <summary>
/// Mithu's typefaces, in one place: Windows' own <b>Segoe UI Variable</b> (owner's choice, 2026-10-13, after comparing
/// it with Inter and Inter + Nunito / Figtree / Plus Jakarta Sans). "Text" is tuned for small sizes, "Display" for
/// big titles and numbers. Windows 10 doesn't have Segoe UI Variable and gets Segoe UI. Nothing is bundled; icons are
/// vector paths and emoji use Segoe UI Emoji.
/// </summary>
public static class AppFonts
{
    /// <summary>Body and UI text.</summary>
    public static FontFamily Ui { get; } = new("Segoe UI Variable Text, Segoe UI");

    /// <summary>The greeting, page titles and big numbers.</summary>
    public static FontFamily Display { get; } = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>
    /// Makes <see cref="Ui"/> the default for every window, popup, menu, tooltip and piece of text that doesn't choose
    /// a font itself (WPF's own default is plain Segoe UI). Call once at startup, before any window is created.
    /// </summary>
    public static void UseAsDefault()
    {
        TextElement.FontFamilyProperty.OverrideMetadata(typeof(TextElement), new FrameworkPropertyMetadata(Ui));
        TextBlock.FontFamilyProperty.OverrideMetadata(typeof(TextBlock), new FrameworkPropertyMetadata(Ui));
        foreach (var type in new[] { typeof(Window), typeof(ToolTip), typeof(ContextMenu) })
        {
            Control.FontFamilyProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(Ui, FrameworkPropertyMetadataOptions.Inherits));
        }
    }
}
