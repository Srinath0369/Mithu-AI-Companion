using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mithu.Infrastructure;

/// <summary>Shared helpers for Mithu's small JSON files (settings, focus).</summary>
internal static class JsonFile
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "Ctrl+Shift" readable
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes via a temp file + rename, so a crash can never leave half a file. Throws on I/O failure.</summary>
    public static void WriteAtomic(string path, string contents)
    {
        var temp = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Keeps an unreadable file as <c>name.corrupt-timestamp</c> (best effort) and returns that path.</summary>
    public static string MoveAside(string path)
    {
        var backup = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        try
        {
            File.Move(path, backup, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return backup;
    }
}
