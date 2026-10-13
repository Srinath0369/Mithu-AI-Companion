using System.Diagnostics.CodeAnalysis;

namespace Mithu.Core.Input;

/// <summary>Modifier flags. Values intentionally match Win32 <c>MOD_*</c> constants.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A global shortcut such as <c>Ctrl+Shift+Space</c>.
/// <see cref="VirtualKey"/> is the Win32 virtual-key code.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey, string KeyName)
{
    public static HotkeyGesture Default { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x20, "Space");

    /// <summary>
    /// Parses text like "Ctrl+Shift+Space", "Alt+F2" or "Win+Ctrl+M".
    /// Requires Ctrl, Alt or Win so the shortcut never blocks normal typing.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };

            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        if ((modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == 0)
        {
            return false;
        }

        if (!TryParseKey(parts[^1], out var virtualKey, out var keyName))
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, virtualKey, keyName);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(KeyName);
        return string.Join('+', parts);
    }

    private static bool TryParseKey(string key, out int virtualKey, out string keyName)
    {
        var upper = key.ToUpperInvariant();

        if (upper.Length == 1 && (char.IsAsciiLetterUpper(upper[0]) || char.IsAsciiDigit(upper[0])))
        {
            virtualKey = upper[0]; // VK codes for A–Z and 0–9 equal their ASCII codes.
            keyName = upper;
            return true;
        }

        if (upper == "SPACE")
        {
            virtualKey = 0x20;
            keyName = "Space";
            return true;
        }

        if (upper.Length >= 2 && upper[0] == 'F' && int.TryParse(upper.AsSpan(1), out var number) && number is >= 1 and <= 24)
        {
            virtualKey = 0x70 + number - 1; // VK_F1 = 0x70
            keyName = "F" + number;
            return true;
        }

        virtualKey = 0;
        keyName = string.Empty;
        return false;
    }
}
