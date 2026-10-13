using Mithu.Core.Desktop;

namespace Mithu.Core.Interfaces;

/// <summary>What Desktop control changed (file moves, copies, renames, new folders), for Undo. Paths only.</summary>
public interface IActionLog
{
    /// <summary>Records an action and returns its id.</summary>
    long Add(string chatId, string callId, string summary, IReadOnlyList<FileOp> ops, DateTimeOffset nowUtc);

    IReadOnlyList<ActionRecord> ForChat(string chatId);

    ActionRecord? Get(long id);

    void MarkUndone(long id);
}
