namespace Mithu.Core.Desktop;

/// <summary>The result of checking a path: the full path when allowed, otherwise why not (said to the user).</summary>
public sealed record PathCheck(bool Allowed, string FullPath, string? Reason)
{
    public static PathCheck No(string reason) => new(false, string.Empty, reason);
}

/// <summary>
/// Keeps file actions inside the user's allowed folders. Refuses network and device paths, alternate data streams,
/// relative paths and any <c>..</c>, and checks the real location (after junctions and symbolic links, via
/// <paramref name="finalPath"/>) so a link inside an allowed folder can't lead outside it. Pure; unit-tested.
/// </summary>
public static class PathGuard
{
    public static PathCheck Check(string? path, IReadOnlyList<string> allowedRoots, Func<string, string?>? finalPath = null)
    {
        var text = path?.Trim().Trim('"') ?? string.Empty;
        if (text.Length == 0)
        {
            return PathCheck.No("No path was given.");
        }

        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
        {
            return PathCheck.No("Network and device paths aren't allowed.");
        }

        if (!System.IO.Path.IsPathFullyQualified(text))
        {
            return PathCheck.No("Use a full path (like C:\\Users\\…\\Documents\\file.pdf).");
        }

        // Only "C:" may contain a colon: "file.txt:stream" (alternate data streams) is refused.
        if (text.IndexOf(':', 2) >= 0)
        {
            return PathCheck.No("That path isn't allowed.");
        }

        var parts = text.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p.Trim() is ".." or "."))
        {
            return PathCheck.No("Paths with \"..\" aren't allowed.");
        }

        string full;
        try
        {
            full = Normalize(System.IO.Path.GetFullPath(text));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            return PathCheck.No("That isn't a valid path.");
        }

        var real = Normalize(finalPath?.Invoke(full) ?? full);
        foreach (var root in allowedRoots)
        {
            var rootFull = Normalize(finalPath?.Invoke(Normalize(root)) ?? root);
            if (IsInside(real, rootFull))
            {
                return new PathCheck(true, full, null);
            }
        }

        return PathCheck.No("That's outside the allowed folders. The user can add folders in Desktop control settings.");
    }

    /// <summary>The same folder, or inside it (whole folder names only: "Docs2" isn't inside "Docs").</summary>
    public static bool IsInside(string path, string root)
    {
        path = Normalize(path);
        root = Normalize(root);
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A folder the user may add: an existing local folder that isn't a Windows/program folder (or holds one), or
    /// a whole local disk from <paramref name="localDisks"/> (fixed disks other than the Windows one, e.g. "D:\").
    /// </summary>
    public static string? WhyNotAllowedFolder(string path, IReadOnlyList<string> systemFolders, IReadOnlyList<string>? localDisks = null)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || !System.IO.Path.IsPathFullyQualified(path))
        {
            return "Pick a folder on this PC.";
        }

        var full = Normalize(path);
        if (full.Length <= 3)
        {
            return (localDisks ?? []).Any(d => Normalize(d).Equals(full, StringComparison.OrdinalIgnoreCase))
                ? null
                : systemFolders.Any(s => s.StartsWith(full, StringComparison.OrdinalIgnoreCase))
                    ? "That's the Windows disk, so it can't be added whole. Pick a folder on it."
                    : "Only disks inside this PC can be added (not USB, network or DVD drives).";
        }

        return systemFolders.Any(s => s.Length > 0 && (IsInside(full, s) || IsInside(s, full)))
            ? "Windows and program folders can't be added."
            : null;
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }

        path = path.Replace('/', '\\');
        return path.Length > 3 ? path.TrimEnd('\\') : path;
    }
}
