namespace Mithu.Core.Motion;

/// <summary>Visual values of the island at one instant. Sizes in DIPs; opacities 0–1.</summary>
public readonly record struct IslandFrame(
    double Width,
    double Height,
    double Radius,
    double HeaderOpacity,
    double HeaderOffsetY,
    double PanelOpacity,
    double PanelOffsetY,
    double NotchEyesOpacity,
    double FaceOffsetY,
    double FaceScale,
    double Bubble = 0)
{
    /// <summary>The pose this frame represents (used to start a new transition mid-flight without a jump).</summary>
    public IslandPose ToPose() => new(Width, Height, Radius, HeaderOpacity, PanelOpacity, NotchEyesOpacity, Bubble);
}

/// <summary>
/// Geometry plus how visible each layer is (0 hidden, 1 shown). Values may be fractional when a
/// transition is interrupted, so the next one continues smoothly from where the last one was.
/// </summary>
/// <param name="Bubble">
/// Floating mode only: 1 = tucked into the face bubble (face centred), 0 = open (face in its place in the bar).
/// It moves with the island's width, so the face glides between the two instead of jumping.
/// </param>
public readonly record struct IslandPose(
    double Width, double Height, double Radius, double Header, double Panel, double NotchEyes, double Bubble = 0);

/// <summary>
/// Choreographs a transition between two island poses as a pure function of time.
/// <para>
/// Opening: width springs out first, height follows, the face slides down out of the notch,
/// then the header and panel fade up in a slight cascade.
/// Closing: content leaves first (quick fade + lift), then height, then width; notch eyes return last.
/// </para>
/// Keeping this in Core makes the motion deterministic and unit-testable; the UI just samples it each frame.
/// </summary>
public sealed class IslandChoreography
{
    /// <summary>How far the face sits up inside the notch when the header is hidden (DIPs).</summary>
    public const double FaceTuckOffset = -14;

    /// <summary>Face size inside the notch relative to normal.</summary>
    public const double FaceTuckScale = 0.6;

    // Stagger between width and height (seconds). Width leads when growing, height leads when shrinking.
    private const double AxisStagger = 0.06;

    // While closing, the shape starts once the content is about half faded (overlap = one continuous motion).
    private const double CloseShapeHold = 0.05;

    // Content timings (seconds).
    private const double HeaderEnterDelay = 0.20, HeaderEnterDuration = 0.22;
    private const double PanelEnterDelay = 0.26, PanelEnterDuration = 0.26;
    // Eyes return when the notch is nearly its final size, so they never float in a big empty shape.
    private const double EyesEnterDelayOpening = 0.10, EyesEnterDelayClosing = 0.24, EyesEnterDuration = 0.18;
    private const double ContentExitDuration = 0.20, EyesExitDuration = 0.12;

    private readonly IslandPose _from;
    private readonly IslandPose _to;
    private readonly Spring _spring;
    private readonly bool _growing;
    private readonly bool _simple;

    /// <param name="spring">
    /// Optional spring for small, single-stage motions (e.g. hover swell): no stagger, no hold.
    /// When omitted, the full open/close choreography is used.
    /// </param>
    public IslandChoreography(IslandPose from, IslandPose to, Spring? spring = null)
    {
        _from = from;
        _to = to;
        _growing = (to.Width * to.Height) >= (from.Width * from.Height);
        _simple = spring is not null;
        _spring = spring ?? (_growing ? Spring.Open : Spring.Close);

        var shapeEnd = _simple
            ? _spring.SettleTime
            : (_growing ? 0 : CloseShapeHold) + AxisStagger + _spring.SettleTime;
        var contentEnd = _simple ? 0 : (_growing ? PanelEnterDelay + PanelEnterDuration : EyesEnterDelayClosing + EyesEnterDuration);
        Duration = Math.Max(shapeEnd, contentEnd);
    }

    /// <summary>Total time until everything has settled (seconds).</summary>
    public double Duration { get; }

    public bool IsGrowing => _growing;

    /// <summary>The exact resting frame of the target pose.</summary>
    public IslandFrame Final => Compose(_to.Width, _to.Height, _to.Radius, _to.Header, 0, _to.Panel, 0, _to.NotchEyes, _to.Bubble);

    /// <summary>The visual values at <paramref name="t"/> seconds into the transition.</summary>
    public IslandFrame Sample(double t)
    {
        // Land exactly on target: no sub-pixel residue (= no 1 px jump at the end).
        if (t >= Duration)
        {
            return Final;
        }

        // --- Shape ---------------------------------------------------------
        double w, h, r, widthProgress;
        if (_simple)
        {
            var p = _spring.Value(t);
            widthProgress = p;
            w = Lerp(_from.Width, _to.Width, p);
            h = Lerp(_from.Height, _to.Height, p);
            r = Lerp(_from.Radius, _to.Radius, p);
        }
        else
        {
            var widthDelay = _growing ? 0 : AxisStagger;
            var heightDelay = _growing ? AxisStagger : 0;
            var hold = _growing ? 0 : CloseShapeHold;
            widthProgress = _spring.Value(t - hold - widthDelay);
            w = Lerp(_from.Width, _to.Width, widthProgress);
            h = Lerp(_from.Height, _to.Height, _spring.Value(t - hold - heightDelay));
            r = Lerp(_from.Radius, _to.Radius, _spring.Value(t - hold - heightDelay));
        }

        // --- Content -------------------------------------------------------
        var (header, headerY) = Layer(t, _from.Header, _to.Header, HeaderEnterDelay, HeaderEnterDuration, ContentExitDuration, distance: 8);
        var (panel, panelY) = Layer(t, _from.Panel, _to.Panel, PanelEnterDelay, PanelEnterDuration, ContentExitDuration, distance: 12);
        var eyesDelay = _growing ? EyesEnterDelayOpening : EyesEnterDelayClosing;
        var (eyes, _) = Layer(t, _from.NotchEyes, _to.NotchEyes, eyesDelay, EyesEnterDuration, EyesExitDuration, distance: 0);

        // The floating face glides with the width (clamped: the spring's small overshoot must not push it past its spot).
        var bubble = Math.Clamp(Lerp(_from.Bubble, _to.Bubble, widthProgress), 0, 1);

        return Compose(w, Math.Max(1, h), Math.Max(0, r), header, headerY, panel, panelY, eyes, bubble);
    }

    private static IslandFrame Compose(
        double w, double h, double r, double header, double headerY, double panel, double panelY, double eyes, double bubble) =>
        new(
            Width: w,
            Height: h,
            Radius: r,
            HeaderOpacity: header,
            HeaderOffsetY: headerY,
            PanelOpacity: panel,
            PanelOffsetY: panelY,
            NotchEyesOpacity: eyes,
            // The face rides with the header: tucked up and small in the notch, full size when open.
            FaceOffsetY: (1 - header) * FaceTuckOffset,
            FaceScale: FaceTuckScale + ((1 - FaceTuckScale) * header),
            Bubble: bubble);

    private static double Lerp(double a, double b, double p) => a + ((b - a) * p);

    /// <summary>
    /// Opacity and vertical offset of a content layer. Entering content rises into place with a smooth
    /// deceleration; leaving content lifts away with a gentle acceleration so it never "slams".
    /// </summary>
    private static (double Opacity, double OffsetY) Layer(
        double t, double from, double to, double enterDelay, double enterDuration, double exitDuration, double distance)
    {
        if (Math.Abs(to - from) < 1e-9)
        {
            return (to, 0);
        }

        if (to > from)
        {
            var p = Ease.OutCubic(Ease.Stage(t, enterDelay, enterDuration));
            var opacity = Lerp(from, to, p);
            return (opacity, (1 - opacity) * distance);
        }

        // Ease-out: responds immediately to the click, then fades softly to nothing (no pop at either end).
        var q = Ease.OutCubic(Ease.Stage(t, 0, exitDuration));
        var leaving = Lerp(from, to, q);
        return (leaving, -(1 - leaving) * distance * 0.5);
    }
}
