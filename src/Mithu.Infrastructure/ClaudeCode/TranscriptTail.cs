using System.IO;
using System.Text;

namespace Mithu.Infrastructure.ClaudeCode;

/// <summary>
/// Follows Claude Code transcript files (<c>.jsonl</c>) and hands back only the lines added since the last read, so
/// the token count can be added up. The first look at a file starts at its end (earlier requests don't count). Reads
/// only; nothing is kept but the read position.
/// </summary>
public sealed class TranscriptTail
{
    private const int MaxChunk = 16 * 1024 * 1024;
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Complete lines added since the last call (none the first time, or for anything but a .jsonl file).</summary>
    public IReadOnlyList<string> ReadNew(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path) || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var known = _offsets.TryGetValue(path, out var offset);
            if (!known || offset > file.Length || file.Length - offset > MaxChunk)
            {
                _offsets[path] = file.Length; // new to Mithu (or too far behind): count from here on
                return [];
            }

            var bytes = new byte[file.Length - offset];
            file.Position = offset;
            file.ReadExactly(bytes);
            var end = Array.LastIndexOf(bytes, (byte)'\n');
            if (end < 0)
            {
                return []; // a line still being written
            }

            _offsets[path] = offset + end + 1;
            return Encoding.UTF8.GetString(bytes, 0, end).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (FileNotFoundException)
        {
            _offsets.TryAdd(path, 0); // not written yet: it all counts once it is
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
