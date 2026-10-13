namespace Mithu.Core.Models;

/// <summary>Rules for the name the user gives their companion. Pure; unit-tested.</summary>
public static class CompanionNames
{
    public const string Default = "Mithu";

    public const int MaxLength = 20;

    /// <summary>
    /// The name to use: spaces tidied, control characters dropped, at most <see cref="MaxLength"/> characters (not
    /// cutting an emoji or accented letter in half); empty → "Mithu".
    /// </summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Default;
        }

        var text = string.Join(' ', new string(name.Where(c => !char.IsControl(c)).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
        {
            return Default;
        }

        var info = new System.Globalization.StringInfo(text);
        return info.LengthInTextElements <= MaxLength ? text : info.SubstringByTextElements(0, MaxLength).TrimEnd();
    }
}
