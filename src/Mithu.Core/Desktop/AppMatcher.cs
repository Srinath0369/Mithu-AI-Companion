using System.Text;

namespace Mithu.Core.Desktop;

/// <summary>An installed app: its Start menu name and how to start it (an app id or a path).</summary>
public sealed record AppEntry(string Name, string LaunchId);

/// <summary>A window that's open now: the app's names (product name, process name) and the window title.</summary>
public sealed record RunningApp(string Name, string ProcessName, string Title, nint Handle);

/// <summary>
/// Finds the app the user means from what they typed ("VS Code", "chrome", "calc"): exact names first, then name
/// starts, word starts, initials and a few common nicknames. Pure; unit-tested.
/// </summary>
public static class AppMatcher
{
    /// <summary>Good enough to act on without asking.</summary>
    public const int Confident = 70;

    /// <summary>Everyday nicknames → words in the real name.</summary>
    private static readonly Dictionary<string, string> Nicknames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vscode"] = "visual studio code",
        ["vs code"] = "visual studio code",
        ["code"] = "visual studio code",
        ["chrome"] = "google chrome",
        ["edge"] = "microsoft edge",
        ["calc"] = "calculator",
        ["explorer"] = "file explorer",
        ["files"] = "file explorer",
        ["terminal"] = "terminal",
        ["settings"] = "settings",
        ["store"] = "microsoft store",
        ["teams"] = "microsoft teams",
        ["ppt"] = "powerpoint",
        ["notepad++"] = "notepad++",
    };

    /// <summary>Start menu entries that aren't apps to open.</summary>
    private static readonly string[] NotApps = ["uninstall", "readme", "read me", "release notes", "documentation", "help", "website", "license", "manual"];

    public static bool IsLaunchable(string name) =>
        name.Trim().Length > 0 && !NotApps.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>The best app for the words, or null when nothing (or nothing clear) matches.</summary>
    public static AppEntry? Best(string query, IEnumerable<AppEntry> apps)
    {
        var ranked = Rank(query, apps, a => [a.Name]);
        return ranked.Count > 0 && ranked[0].Score >= Confident ? ranked[0].Item : null;
    }

    /// <summary>The open window that best matches (by product name, process name or title), or null.</summary>
    public static RunningApp? BestRunning(string query, IEnumerable<RunningApp> running)
    {
        var ranked = Rank(query, running, r => [r.Name, r.ProcessName]);
        return ranked.Count > 0 && ranked[0].Score >= Confident ? ranked[0].Item : null;
    }

    /// <summary>A few names close to the words (to suggest when nothing matched well).</summary>
    public static IReadOnlyList<string> Suggestions(string query, IEnumerable<AppEntry> apps, int count = 3) =>
        Rank(query, apps, a => [a.Name]).Where(r => r.Score > 0).Take(count).Select(r => r.Item.Name).ToList();

    public static List<(T Item, int Score)> Rank<T>(string query, IEnumerable<T> items, Func<T, IEnumerable<string>> names)
    {
        var queries = new List<string> { Clean(query) };
        if (Nicknames.TryGetValue(query.Trim(), out var real) || Nicknames.TryGetValue(Clean(query), out real))
        {
            queries.Add(Clean(real));
        }

        return items
            .Select(item => (Item: item, Score: names(item).Max(n => queries.Max(q => Score(q, Clean(n))))))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => names(r.Item).First().Length) // "Word" before "WordPad" for "word"
            .ToList();
    }

    /// <summary>How well cleaned words match a cleaned name (0 = not at all, 100 = the same).</summary>
    internal static int Score(string query, string name)
    {
        if (query.Length == 0 || name.Length == 0)
        {
            return 0;
        }

        if (name == query)
        {
            return 100;
        }

        var compactQuery = query.Replace(" ", string.Empty, StringComparison.Ordinal);
        var compactName = name.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compactName == compactQuery)
        {
            return 95;
        }

        var nameWords = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var queryWords = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // "spotify" → "Spotify Music"; "google ch" → "Google Chrome"
        if (name.StartsWith(query, StringComparison.Ordinal) && (name.Length == query.Length || name[query.Length] == ' ' || query.Length >= 4))
        {
            return 85;
        }

        // Every typed word starts a word of the name, in order: "studio code" → "Visual Studio Code"
        if (WordsInOrder(queryWords, nameWords))
        {
            return 80;
        }

        // Initials: "vsc" → "Visual Studio Code"
        if (compactQuery.Length >= 2 && nameWords.Length >= 2 && string.Concat(nameWords.Select(w => w[0])) == compactQuery)
        {
            return 75;
        }

        return query.Length >= 4 && name.Contains(query, StringComparison.Ordinal) ? 65 : 0;
    }

    private static bool WordsInOrder(string[] query, string[] name)
    {
        var at = 0;
        foreach (var word in query)
        {
            while (at < name.Length && !name[at].StartsWith(word, StringComparison.Ordinal))
            {
                at++;
            }

            if (at == name.Length)
            {
                return false;
            }

            at++;
        }

        return query.Length > 0;
    }

    /// <summary>Lower case, letters/digits/+/# only, single spaces.</summary>
    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '+' or '#' ? c : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
