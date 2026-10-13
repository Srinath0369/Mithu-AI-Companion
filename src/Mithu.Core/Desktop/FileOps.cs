using System.Globalization;

namespace Mithu.Core.Desktop;

public enum FileOpKind
{
    Move,
    Copy,
    Rename,
    CreateFolder,
}

/// <summary>One file change Mithu made, with what's needed to undo it (<see cref="From"/> is empty for a new folder).</summary>
public sealed record FileOp(FileOpKind Kind, string From, string To);

/// <summary>A logged action: the changes one tool call made, for Undo (never file contents).</summary>
public sealed record ActionRecord(long Id, string ChatId, string CallId, string Summary, DateTimeOffset CreatedUtc, IReadOnlyList<FileOp> Ops, bool Undone);

/// <summary>File-name rules for moving, copying and renaming. Pure; unit-tested.</summary>
public static class FileNames
{
    /// <summary>Most files one step may touch.</summary>
    public const int MaxFilesPerStep = 200;

    private static readonly string[] Reserved =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>Why a new name can't be used, or null when it's fine.</summary>
    public static string? WhyBadName(string? name)
    {
        var n = name ?? string.Empty;
        if (n.Trim().Length == 0)
        {
            return "The new name is empty.";
        }

        if (n.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || n.Contains('/') || n.Contains('\\'))
        {
            return "Names can't contain \\ / : * ? \" < > |.";
        }

        if (n.EndsWith('.') || n.EndsWith(' ') || n.StartsWith(' '))
        {
            return "Names can't start with a space or end with a space or a dot.";
        }

        if (Reserved.Contains(System.IO.Path.GetFileNameWithoutExtension(n), StringComparer.OrdinalIgnoreCase))
        {
            return $"\"{n}\" is reserved by Windows.";
        }

        return n.Length > 200 ? "That name is too long." : null;
    }

    /// <summary>
    /// A name that's free in the folder: the same name, or "report (2).pdf", "report (3).pdf"… Nothing is ever
    /// overwritten.
    /// </summary>
    public static string FreeName(string folder, string name, Func<string, bool> exists)
    {
        var path = System.IO.Path.Combine(folder, name);
        if (!exists(path))
        {
            return path;
        }

        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var extension = System.IO.Path.GetExtension(name);
        for (var i = 2; i < 10_000; i++)
        {
            path = System.IO.Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem} ({i}){extension}"));
            if (!exists(path))
            {
                return path;
            }
        }

        throw new InvalidOperationException("No free name.");
    }

    /// <summary>"IMG_0412.png, IMG_0413.png, IMG_0414.png … (+9 more)".</summary>
    public static string Preview(IReadOnlyList<string> names, int show = 3) =>
        names.Count <= show
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(show)) + string.Create(CultureInfo.InvariantCulture, $" … (+{names.Count - show} more)");

    /// <summary>"1 file", "12 files", "3 items".</summary>
    public static string Count(int count, string one = "file", string many = "files") =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
}
