namespace Mithu.Core.Motion;

/// <summary>
/// "Punch" reaction when the face is clicked: a quick squash (wider, shorter) followed by a jelly-like
/// spring back to normal with a small wobble. Pure function of time so it can be unit-tested and sampled
/// into keyframes by the UI.
/// </summary>
public static class PunchCurve
{
    /// <summary>Time to reach the squash (seconds). Short = feels like an impact.</summary>
    public const double SquashTime = 0.07;

    /// <summary>Squash amount: wider and shorter, roughly preserving volume.</summary>
    public const double SquashX = 1.16, SquashY = 0.84;

    /// <summary>Total length (seconds). The spring is visually settled well before this.</summary>
    public const double Duration = 0.55;

    /// <summary>Bouncy recovery: ~16 % overshoot of the squash, i.e. a gentle wobble.</summary>
    public static readonly Spring Recover = new(0.30, 0.5);

    /// <summary>Horizontal and vertical scale at time <paramref name="t"/> seconds.</summary>
    public static (double X, double Y) Sample(double t)
    {
        if (t <= 0 || t >= Duration)
        {
            return (1, 1);
        }

        if (t < SquashTime)
        {
            var p = Ease.OutCubic(t / SquashTime);
            return (1 + ((SquashX - 1) * p), 1 + ((SquashY - 1) * p));
        }

        var s = Recover.Value(t - SquashTime);
        return (SquashX + ((1 - SquashX) * s), SquashY + ((1 - SquashY) * s));
    }
}
