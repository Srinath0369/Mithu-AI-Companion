using Mithu.Core.Focus;

namespace Mithu.Core.Interfaces;

/// <summary>Loads and saves the focus timer's state and stats (JSON now, SQLite in Phase 2.2).</summary>
public interface IFocusStore
{
    /// <summary>Saved data, or defaults when there is none or it can't be read.</summary>
    FocusData Load();

    /// <summary>Persists the data. Failures are logged, never thrown (the timer keeps working in memory).</summary>
    void Save(FocusData data);
}
