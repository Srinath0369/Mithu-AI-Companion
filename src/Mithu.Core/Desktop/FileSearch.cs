using System.Globalization;

namespace Mithu.Core.Desktop;

/// <summary>A file found by a search (name, folder, when it was last changed, size).</summary>
public sealed record FileHit(string Path, string Name, DateTimeOffset ModifiedUtc, long Size);

/// <summary>
/// What to look for: words in the file name, file types, and a changed-between window (local dates). Pure;
/// unit-tested.
/// </summary>
public sealed record FileSearchQuery(IReadOnlyList<string> Words, IReadOnlyList<string> Extensions, DateTimeOffset? AfterUtc, DateTimeOffset? BeforeUtc)
{
    /// <summary>Files listed at most (newest first).</summary>
    public const int MaxResults = 20;

    /// <summary>Most files looked at in one search (keeps it quick on big folders).</summary>
    public const int MaxScanned = 50_000;

    public bool IsEmpty => Words.Count == 0 && Extensions.Count == 0 && AfterUtc is null && BeforeUtc is null;

    public bool Matches(string name, DateTimeOffset modifiedUtc)
    {
        if (Words.Any(w => !name.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (Extensions.Count > 0 && !Extensions.Contains(System.IO.Path.GetExtension(name).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return (AfterUtc is null || modifiedUtc >= AfterUtc) && (BeforeUtc is null || modifiedUtc < BeforeUtc);
    }

    /// <summary>
    /// From the AI's arguments: <paramref name="words"/> to find in names, <paramref name="type"/> ("pdf", ".docx",
    /// or a kind: image, document, video, audio, archive, spreadsheet, presentation), and local dates
    /// (YYYY-MM-DD; <paramref name="before"/> includes that whole day).
    /// </summary>
    public static FileSearchQuery From(string? words, string? type, string? after, string? before, TimeZoneInfo zone)
    {
        var wordList = (words ?? string.Empty)
            .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('"', '*', '.'))
            .Where(w => w.Length > 0)
            .ToList();
        return new FileSearchQuery(wordList, Kinds(type), Day(after, zone, 0), Day(before, zone, 1));
    }

    /// <summary>The extensions for a type or kind ("image" → png, jpg…).</summary>
    public static IReadOnlyList<string> Kinds(string? type)
    {
        var t = (type ?? string.Empty).Trim().TrimStart('.', '*').ToLowerInvariant();
        return t switch
        {
            "" => [],
            "image" or "images" or "photo" or "photos" or "picture" or "pictures" or "screenshot" or "screenshots" =>
                ["png", "jpg", "jpeg", "gif", "bmp", "webp", "heic", "tif", "tiff", "svg"],
            "document" or "documents" or "doc" or "docs" => ["pdf", "doc", "docx", "txt", "rtf", "odt", "md"],
            "video" or "videos" or "movie" or "movies" => ["mp4", "mkv", "mov", "avi", "wmv", "webm"],
            "audio" or "music" or "song" or "songs" => ["mp3", "wav", "flac", "m4a", "aac", "ogg", "wma"],
            "archive" or "archives" or "zip" or "zips" => ["zip", "rar", "7z", "tar", "gz"],
            "spreadsheet" or "spreadsheets" or "excel" => ["xlsx", "xls", "csv", "ods"],
            "presentation" or "presentations" or "slides" or "powerpoint" => ["pptx", "ppt", "odp"],
            "word" => ["docx", "doc"],
            _ => t.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimStart('.', '*')).ToList(),
        };
    }

    private static DateTimeOffset? Day(string? text, TimeZoneInfo zone, int addDays)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !DateTime.TryParseExact(text.Trim()[..Math.Min(10, text.Trim().Length)], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return null;
        }

        var local = DateTime.SpecifyKind(day.Date.AddDays(addDays), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>"1.2 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}

/// <summary>
/// Files that run code when opened (programs, scripts, shortcuts, installers): Mithu never opens these, since that
/// would be running a program on the AI's say-so.
/// </summary>
public static class RunnableFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "com", "bat", "cmd", "ps1", "psm1", "psd1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "msi", "msp",
        "msix", "msixbundle", "appx", "appxbundle", "appinstaller", "scr", "pif", "cpl", "msc", "lnk", "url", "reg", "inf", "jar", "py",
        "pyw", "sh", "application", "gadget", "ws", "scf", "chm", "dll", "sys", "drv", "ocx", "iso", "img", "vhd", "vhdx",
        "library-ms", "search-ms", "settingcontent-ms", "diagcab", "xll", "xlam", "ppam", "docm", "xlsm", "pptm",
    };

    public static bool IsRunnable(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path).TrimStart('.'));
}
