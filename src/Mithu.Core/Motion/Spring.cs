namespace Mithu.Core.Motion;

/// <summary>
/// A critically-tunable damped spring, evaluated analytically (no per-frame integration error).
/// Gives natural, physically plausible motion: fast start, gentle settle, optional small overshoot.
/// </summary>
/// <param name="Response">
/// Roughly how long the spring takes to get "most of the way" (seconds). Larger = slower, softer.
/// </param>
/// <param name="DampingRatio">
/// 1.0 = no overshoot (critically damped). 0.75–0.9 = a small, friendly overshoot. Below ~0.6 feels wobbly.
/// </param>
public readonly record struct Spring(double Response, double DampingRatio)
{
    /// <summary>Opening the island: soft and slightly bouncy (≈ 3 % overshoot).</summary>
    public static Spring Open { get; } = new(0.42, 0.78);

    /// <summary>Closing the island: no bounce, settles calmly.</summary>
    public static Spring Close { get; } = new(0.30, 1.0);

    /// <summary>Small UI reactions (hover swell, button feedback).</summary>
    public static Spring Snappy { get; } = new(0.22, 0.85);

    /// <summary>Distance below which the spring is considered settled (fraction of the full move).</summary>
    public const double SettleTolerance = 0.001;

    private double Omega => 2 * Math.PI / Response;

    /// <summary>
    /// Progress from 0 (start) to 1 (target) at time <paramref name="t"/> seconds.
    /// May exceed 1 briefly when <see cref="DampingRatio"/> &lt; 1.
    /// </summary>
    public double Value(double t)
    {
        if (t <= 0)
        {
            return 0;
        }

        var w = Omega;
        var z = DampingRatio;

        if (z >= 1)
        {
            // Critically damped: x(t) = 1 - (1 + w t) e^{-w t}
            return 1 - ((1 + (w * t)) * Math.Exp(-w * t));
        }

        // Under-damped.
        var wd = w * Math.Sqrt(1 - (z * z));
        var envelope = Math.Exp(-z * w * t);
        return 1 - (envelope * (Math.Cos(wd * t) + (z * w / wd * Math.Sin(wd * t))));
    }

    /// <summary>Time (seconds) after which the spring stays within <see cref="SettleTolerance"/> of the target.</summary>
    public double SettleTime
    {
        get
        {
            // Scan backwards from a safe upper bound for the last moment the error exceeds the tolerance.
            // (The analytic envelope underestimates under-damped springs because of the sine term.)
            const double step = 0.001;
            var upper = 10 * Response;
            for (var t = upper; t > 0; t -= step)
            {
                if (Math.Abs(1 - Value(t)) > SettleTolerance)
                {
                    return t + step;
                }
            }

            return step;
        }
    }

    /// <summary>Largest value reached (1.0 = no overshoot). Sampled; good enough for design checks.</summary>
    public double PeakValue
    {
        get
        {
            var peak = 0.0;
            var end = SettleTime;
            for (var t = 0.0; t <= end; t += 0.002)
            {
                peak = Math.Max(peak, Value(t));
            }

            return peak;
        }
    }
}

/// <summary>Standard easing curves for things that should not bounce (opacity, small offsets).</summary>
public static class Ease
{
    /// <summary>Smooth deceleration (cubic). Good for things appearing.</summary>
    public static double OutCubic(double p)
    {
        p = Math.Clamp(p, 0, 1);
        var inv = 1 - p;
        return 1 - (inv * inv * inv);
    }

    /// <summary>Gentle acceleration (quadratic). Good for things leaving: they don't "slam" at the end.</summary>
    public static double InQuad(double p)
    {
        p = Math.Clamp(p, 0, 1);
        return p * p;
    }

    /// <summary>Slow start, fast middle, slow end. Good for fades that must not pop at either end.</summary>
    public static double InOutCubic(double p)
    {
        p = Math.Clamp(p, 0, 1);
        return p < 0.5 ? 4 * p * p * p : 1 - (Math.Pow((-2 * p) + 2, 3) / 2);
    }

    /// <summary>Progress (0–1) of a stage that starts at <paramref name="delay"/> and lasts <paramref name="duration"/> seconds.</summary>
    public static double Stage(double t, double delay, double duration) =>
        duration <= 0 ? (t >= delay ? 1 : 0) : Math.Clamp((t - delay) / duration, 0, 1);
}
