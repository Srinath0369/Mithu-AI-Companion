namespace Mithu.Core.Models;

/// <summary>
/// Sizes of the companion "island". Arrows move between Hidden ⇄ Compact ⇄ Expanded.
/// </summary>
public enum WindowMode
{
    /// <summary>
    /// Tucked away. Stuck to the top edge: a slim notch with two eyes (hover 300 ms to open).
    /// Floating: a small face bubble (click to open).
    /// </summary>
    Hidden,

    /// <summary>Face, greeting/status and arrow buttons.</summary>
    Compact,

    /// <summary>Compact header plus the productivity panel.</summary>
    Expanded,

    /// <summary>One timely message (reminders/timers, Phase 2). Same size as Compact.</summary>
    Peek,
}
