namespace Mithu.Core.Models;

/// <summary>Facial expressions of Mithu (blueprint §4).</summary>
public enum FaceMood
{
    Idle,
    Hover,
    Listening,
    Thinking,
    Happy,
    Celebrating,
    Concerned,
    Sleepy,

    /// <summary>Being poked repeatedly: eyes squeezed shut "&gt; &lt;".</summary>
    Hurt,

    /// <summary>Annoyed after being poked too much: brows settle into a slant, eyes narrow, a small pout, a slight tilt looking away.</summary>
    Angry,

    /// <summary>Concentrating with you during a focus round: calm, lowered eyes, small content smile.</summary>
    Focused,

    /// <summary>Taking a break with you: content curved eyes, soft smile.</summary>
    Relaxed,
}
