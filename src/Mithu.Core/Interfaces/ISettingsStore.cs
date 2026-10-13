using Mithu.Core.Models;

namespace Mithu.Core.Interfaces;

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    AppSettings Current { get; }

    /// <summary>Raised after settings changed and were saved.</summary>
    event EventHandler<AppSettings>? Changed;

    /// <summary>Applies a change, persists it and raises <see cref="Changed"/> (no-op if nothing changed).</summary>
    void Update(Func<AppSettings, AppSettings> change);
}
