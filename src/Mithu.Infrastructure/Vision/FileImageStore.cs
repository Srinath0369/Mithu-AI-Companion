using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Vision;

/// <summary>
/// Screenshots as PNG files in <c>%LOCALAPPDATA%\Mithu\images</c>, named by id (32 hex characters, so an id can
/// never point outside the folder). Kept with their chats; deleted with them.
/// </summary>
public sealed class FileImageStore(string directory) : IImageStore
{
    public string Save(byte[] png)
    {
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, id + ".png");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, png);
        File.Move(temp, path, overwrite: true);
        return id;
    }

    public byte[]? Load(string id) => PathOf(id) is { } path ? File.ReadAllBytes(path) : null;

    public string? PathOf(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var path = Path.Combine(directory, id + ".png");
        return File.Exists(path) ? path : null;
    }

    public void Delete(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (PathOf(id) is { } path)
            {
                TryDelete(path);
            }
        }
    }

    public void DeleteAll()
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
        {
            TryDelete(file);
        }
    }

    internal static bool IsValidId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
