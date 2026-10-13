using System.Text.Json;
using Microsoft.Extensions.Logging;
using Mithu.Core.Focus;
using Mithu.Core.Interfaces;

namespace Mithu.Infrastructure.Focus;

/// <summary>
/// Focus timer state and stats in a small JSON file (atomic writes; a corrupt file is kept as a backup).
/// Moves into SQLite (<c>timer_sessions</c>) in Phase 2.2. Privacy: never log the intention text.
/// </summary>
public sealed class JsonFocusStore(string path, ILogger<JsonFocusStore> logger) : IFocusStore
{
    public FocusData Load()
    {
        if (!File.Exists(path))
        {
            return new FocusData();
        }

        try
        {
            return JsonSerializer.Deserialize<FocusData>(File.ReadAllText(path), JsonFile.Options) ?? new FocusData();
        }
        catch (JsonException ex)
        {
            var backup = JsonFile.MoveAside(path);
            logger.LogWarning(ex, "Focus file was corrupt; starting fresh. Backup: {Backup}", backup);
            return new FocusData();
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not read focus file; starting fresh");
            return new FocusData();
        }
    }

    public void Save(FocusData data)
    {
        try
        {
            JsonFile.WriteAtomic(path, JsonSerializer.Serialize(data, JsonFile.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not save focus data");
        }
    }
}
