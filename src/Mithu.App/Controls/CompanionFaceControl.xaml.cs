using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mithu.Core.Models;
using Mithu.Core.Motion;

namespace Mithu.App.Controls;

/// <summary>
/// Vector face with mood visuals and motion primitives (blink, ponder, punch, bounce, breathing, thought dots).
/// It only knows <i>how</i> to look; <see cref="Animation.FaceAnimationCoordinator"/> decides <i>when</i>.
/// </summary>
public partial class CompanionFaceControl : UserControl
{
    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(FaceMood), typeof(CompanionFaceControl),
        new PropertyMetadata(FaceMood.Idle, (d, _) => ((CompanionFaceControl)d).ApplyMood(animate: true)));

    /// <summary>A thin rim round the face plate (⋯ → Show border lines).</summary>
    public static readonly DependencyProperty ShowRimProperty = DependencyProperty.Register(
        nameof(ShowRim), typeof(bool), typeof(CompanionFaceControl),
        new PropertyMetadata(false, (d, e) => ((CompanionFaceControl)d).Plate.BorderThickness = new Thickness((bool)e.NewValue ? 1.5 : 0)));

    public bool ShowRim
    {
        get => (bool)GetValue(ShowRimProperty);
        set => SetValue(ShowRimProperty, value);
    }

    public static readonly DependencyProperty ReducedMotionProperty = DependencyProperty.Register(
        nameof(ReducedMotion), typeof(bool), typeof(CompanionFaceControl), new PropertyMetadata(false));


    private static readonly Geometry SmileSmall = Frozen("M 28,40 Q 36,46 44,40");
    private static readonly Geometry SmileBig = Frozen("M 25,38 Q 36,50 47,38");
    private static readonly Geometry MouthFlat = Frozen("M 30,42 L 42,42");
    private static readonly Geometry MouthConcerned = Frozen("M 29,44 Q 36,39 43,44");
    private static readonly Geometry MouthOpen = Frozen("M 32,40 Q 36,47 40,40 Z");
    private static readonly Geometry MouthWince = Frozen("M 27,43 L 31,40 L 36,43 L 41,40 L 45,43");
    private static readonly Geometry MouthPout = Frozen("M 31,44.5 Q 36,40.5 41,44.5");
    private static readonly Geometry MouthFocused = Frozen("M 32,41.6 Q 36,42.8 40,41.6");
    private static readonly Geometry MouthThinking = Frozen("M 33.5,42.6 Q 37,42.4 40.5,41.4");
    private static readonly Geometry MouthSmirk = Frozen("M 29,41.5 Q 35,45 43,39");
    private static readonly Geometry MouthAsleep = Frozen("M 34,42 A 2,2.3 0 1 1 38,42 A 2,2.3 0 1 1 34,42 Z");

    private static readonly Duration Quick = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration Settle = new(TimeSpan.FromMilliseconds(420));
    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });

    /// <summary>How long the sleeping face breathes before lying still (10 s in the MITHU_SLEEP_IDLE_SECONDS test mode).</summary>
    private static readonly TimeSpan DozeFor = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("MITHU_SLEEP_IDLE_SECONDS") is null ? 90 : 10);

    private readonly SolidColorBrush _accent;
    private System.Windows.Threading.DispatcherTimer? _deepSleep;
    private bool _ponderAlt;

    /// <summary>Thinking: eyes up and to the left (the thought dots are up on the right).</summary>
    private static readonly (double X, double Y) ThinkingRest = (-2.5, -2.2);

    /// <summary>Focused: how far the upper lids come down (of the eye's 16 px).</summary>
    private const double FocusedLid = 3.2;
    private bool _wasAnnoyed;

    public CompanionFaceControl()
    {
        InitializeComponent();
        _accent = (SolidColorBrush)Resources["FaceAccent"];
        ApplyMood(animate: false);
    }

    public FaceMood Mood
    {
        get => (FaceMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    /// <summary>When true, mood changes are instant and decorative motion is skipped.</summary>
    public bool ReducedMotion
    {
        get => (bool)GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    /// <summary>One natural blink (~140 ms). Skipped while eyes are already closed/curved.</summary>
    public void Blink()
    {
        if (Mood is FaceMood.Happy or FaceMood.Celebrating or FaceMood.Relaxed or FaceMood.Sleepy or FaceMood.Hurt || ReducedMotion)
        {
            return;
        }

        var open = EyeOpenScale;
        var blink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(140) };
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(0.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))));
        blink.KeyFrames.Add(new EasingDoubleKeyFrame(open, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        blink.Freeze();

        LeftEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, blink, HandoffBehavior.SnapshotAndReplace);
        RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, blink, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// "Punch" reaction: squash on impact, then a jelly spring back with a small wobble (<see cref="PunchCurve"/>).
    /// Rapid punches restart from the current squash (SnapshotAndReplace), never jump.
    /// </summary>
    public void Punch()
    {
        if (ReducedMotion)
        {
            return;
        }

        const double step = 1 / 60.0;
        var duration = TimeSpan.FromSeconds(PunchCurve.Duration);
        var x = new DoubleAnimationUsingKeyFrames { Duration = duration };
        var y = new DoubleAnimationUsingKeyFrames { Duration = duration };
        for (var t = 0.0; t < PunchCurve.Duration; t += step)
        {
            var (sx, sy) = PunchCurve.Sample(t);
            var key = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t));
            x.KeyFrames.Add(new LinearDoubleKeyFrame(sx, key));
            y.KeyFrames.Add(new LinearDoubleKeyFrame(sy, key));
        }

        x.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(duration)));
        y.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(duration)));
        x.Freeze();
        y.Freeze();

        PunchScale.BeginAnimation(ScaleTransform.ScaleXProperty, x, HandoffBehavior.SnapshotAndReplace);
        PunchScale.BeginAnimation(ScaleTransform.ScaleYProperty, y, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Annoyed, smoothly: the brows settle down into a slant, the eyes narrow, the face tilts a little and looks
    /// away ("hmph"). Leaving it eases everything back the same way. No colour change, no shaking.
    /// </summary>
    private void SetAnnoyed(bool annoyed, bool animate)
    {
        To(AngryBrowsShift, TranslateTransform.YProperty, annoyed ? 0 : -3, animate);
        To(TiltRotate, RotateTransform.AngleProperty, annoyed ? -4 : 0, animate);
    }

    /// <summary>
    /// Where the eyes rest: up and to the side while thinking, a little lowered (on the work) while focused, away
    /// while annoyed, straight ahead otherwise. Always eased, never jumped.
    /// </summary>
    private void SetEyeRest(FaceMood mood, bool animate)
    {
        var (x, y) = mood switch
        {
            FaceMood.Thinking => ThinkingRest,
            FaceMood.Focused => (0, 1.4),
            FaceMood.Angry => (-2.5, 0),
            _ => (0.0, 0.0),
        };
        _ponderAlt = false;
        To(GlanceTransform, TranslateTransform.XProperty, x, animate);
        To(GlanceTransform, TranslateTransform.YProperty, y, animate);
    }

    /// <summary>Thinking for a while: the eyes drift slowly between two spots up and to the side.</summary>
    public void Ponder()
    {
        if (ReducedMotion || Mood != FaceMood.Thinking)
        {
            return;
        }

        _ponderAlt = !_ponderAlt;
        var (x, y) = _ponderAlt ? (-0.6, -2.8) : ThinkingRest;
        Drift(TranslateTransform.XProperty, x);
        Drift(TranslateTransform.YProperty, y);

        void Drift(DependencyProperty property, double to) =>
            GlanceTransform.BeginAnimation(property, new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(900))) { EasingFunction = EaseInOut }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Upper lids: <paramref name="lowered"/> px of each eye's top cut flat (0 = open).</summary>
    private void SetLids(double lowered, bool animate)
    {
        var rect = new Rect(0, lowered, 12, 16 - lowered);
        foreach (var lid in new[] { LeftLid, RightLid })
        {
            if (!animate)
            {
                lid.BeginAnimation(RectangleGeometry.RectProperty, null);
                lid.Rect = rect;
            }
            else
            {
                lid.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(rect, Settle) { EasingFunction = EaseInOut }, HandoffBehavior.SnapshotAndReplace);
            }
        }
    }

    /// <summary>Thought dots: shown while thinking, pulsing one after another (12 fps); still dots with reduced motion.</summary>
    private void SetThinking(bool thinking, bool animate)
    {
        Fade(ThoughtDots, thinking ? 1 : 0, animate && !ReducedMotion);
        var dots = new UIElement[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i].BeginAnimation(OpacityProperty, null);
            dots[i].Opacity = 0.35 + (0.3 * i);
            if (thinking && animate && !ReducedMotion)
            {
                dots[i].BeginAnimation(OpacityProperty, Loop(TimeSpan.FromSeconds(1.5), 0.25 * i, 12, (0, 0.25), (0.3, 1), (0.65, 0.25), (1, 0.25)));
            }
        }
    }

    private static void To(Animatable target, DependencyProperty property, double value, bool animate)
    {
        if (!animate)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, value);
            return;
        }

        target.BeginAnimation(property, new DoubleAnimation(value, Settle) { EasingFunction = EaseInOut }, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Starts the ongoing motion of the current mood again (after a pause): breathing, thought dots.</summary>
    public void ResumeAnimations()
    {
        SetAsleep(Mood == FaceMood.Sleepy, animate: true);
        SetThinking(Mood == FaceMood.Thinking, animate: true);
    }

    /// <summary>
    /// Asleep: slow breathing (the face swells a little every ~4 s) and two "z"s drifting up, one after the other.
    /// Rendered at a low frame rate (it's slow, soft motion) and only for the first <see cref="DozeFor"/>: then Mithu
    /// lies still (closed eyes, a still "z"), so being away for hours costs nothing. Stopped the moment Mithu wakes
    /// or is hidden. Reduced motion: the still pose straight away.
    /// </summary>
    private void SetAsleep(bool asleep, bool animate)
    {
        _deepSleep?.Stop();
        BreatheScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BreatheScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        foreach (var z in new UIElement[] { ZSmall, ZBig })
        {
            z.BeginAnimation(OpacityProperty, null);
        }

        ZSmallShift.BeginAnimation(TranslateTransform.YProperty, null);
        ZBigShift.BeginAnimation(TranslateTransform.YProperty, null);
        Fade(Zzz, asleep ? 1 : 0, animate && !ReducedMotion);
        ZSmall.Opacity = asleep ? 0.55 : 0;
        ZBig.Opacity = asleep ? 0.85 : 0;
        if (!asleep || !animate || ReducedMotion)
        {
            return;
        }

        ZSmall.Opacity = 0; // the loops fade them in (the big one starts later)
        ZBig.Opacity = 0;
        var breatheY = Loop(TimeSpan.FromSeconds(4.2), 0, (0, 1), (0.45, 1.035), (1, 1));
        var breatheX = Loop(TimeSpan.FromSeconds(4.2), 0, (0, 1), (0.45, 1.012), (1, 1));
        BreatheScale.BeginAnimation(ScaleTransform.ScaleYProperty, breatheY);
        BreatheScale.BeginAnimation(ScaleTransform.ScaleXProperty, breatheX);
        if (_deepSleep is null)
        {
            _deepSleep = new System.Windows.Threading.DispatcherTimer { Interval = DozeFor };
            _deepSleep.Tick += (_, _) =>
            {
                _deepSleep.Stop();
                SetAsleep(Mood == FaceMood.Sleepy, animate: false); // fast asleep: lie still
            };
        }

        _deepSleep.Start();

        // Each "z" rises ~5 px while fading in and out; the big one follows the small one.
        var cycle = TimeSpan.FromSeconds(3.2);
        ZSmall.BeginAnimation(OpacityProperty, Loop(cycle, 0, (0, 0), (0.25, 0.7), (0.7, 0.5), (1, 0)));
        ZSmallShift.BeginAnimation(TranslateTransform.YProperty, Loop(cycle, 0, (0, 2), (1, -3)));
        ZBig.BeginAnimation(OpacityProperty, Loop(cycle, 1.6, (0, 0), (0.25, 0.9), (0.7, 0.7), (1, 0)));
        ZBigShift.BeginAnimation(TranslateTransform.YProperty, Loop(cycle, 1.6, (0, 2), (1, -4)));
    }

    /// <summary>A repeating, eased key-frame loop at 8 fps starting after <paramref name="beginAt"/> s; keys are (fraction of the cycle, value).</summary>
    private static DoubleAnimationUsingKeyFrames Loop(TimeSpan cycle, double beginAt, params (double At, double Value)[] keys) => Loop(cycle, beginAt, 8, keys);

    private static DoubleAnimationUsingKeyFrames Loop(TimeSpan cycle, double beginAt, int fps, params (double At, double Value)[] keys)
    {
        var loop = new DoubleAnimationUsingKeyFrames
        {
            Duration = cycle,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromSeconds(beginAt),
        };
        foreach (var (at, value) in keys)
        {
            loop.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(at), EaseInOut));
        }

        Timeline.SetDesiredFrameRate(loop, fps);
        loop.Freeze();
        return loop;
    }

    /// <summary>Stops every running animation (used when the window is hidden).</summary>
    public void StopAnimations()
    {
        PunchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PunchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        TiltRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        AngryBrowsShift.BeginAnimation(TranslateTransform.YProperty, null);
        LeftEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        GlanceTransform.BeginAnimation(TranslateTransform.XProperty, null);
        GlanceTransform.BeginAnimation(TranslateTransform.YProperty, null);
        BounceTransform.BeginAnimation(TranslateTransform.YProperty, null);
        ApplyMood(animate: false);
    }

    private double EyeOpenScale => Mood switch
    {
        FaceMood.Sleepy => 0.35,
        FaceMood.Angry => 0.68, // narrowed

        FaceMood.Listening => 1.12,
        FaceMood.Focused => 0.94,
        FaceMood.Hover => 1.05,
        _ => 1.0,
    };

    private void ApplyMood(bool animate)
    {
        var mood = Mood;
        var motion = animate && !ReducedMotion;

        // Eye brightness: brighter on hover/listening, dimmer when sleepy.
        var eyeOpacity = mood switch
        {
            FaceMood.Sleepy => 0.7,
            FaceMood.Hover or FaceMood.Listening or FaceMood.Angry => 1.0,
            _ => 0.92,
        };

        // Which eye shape is showing.
        var happyEyes = mood is FaceMood.Happy or FaceMood.Celebrating or FaceMood.Relaxed;
        var squeezed = mood == FaceMood.Hurt;
        var asleep = mood == FaceMood.Sleepy;
        var openEyes = !happyEyes && !squeezed && !asleep;

        Fade(LeftEye, openEyes ? eyeOpacity : 0, motion);
        Fade(RightEye, openEyes ? eyeOpacity : 0, motion);
        Fade(HappyEyes, happyEyes ? 1 : 0, motion);
        Fade(SqueezedEyes, squeezed ? 1 : 0, motion, fast: true);
        Fade(SleepyEyes, asleep ? 0.85 : 0, motion);
        SetAsleep(asleep, motion);
        Fade(Brows, mood == FaceMood.Concerned ? 1 : 0, motion);
        var annoyed = mood == FaceMood.Angry;
        Fade(AngryBrows, annoyed ? 1 : 0, motion, slow: true);
        SetAnnoyed(annoyed, motion);

        // Eye openness (Sleepy half-closed, Angry narrowed, Listening wide); in and out of annoyed slowly.
        Scale(LeftEyeScale, EyeOpenScale, motion, slow: annoyed || _wasAnnoyed);
        Scale(RightEyeScale, EyeOpenScale, motion, slow: annoyed || _wasAnnoyed);
        _wasAnnoyed = annoyed;

        Mouth.Data = MouthFor(mood);
        Mouth.Fill = mood == FaceMood.Listening ? _accent : null;

        if (mood == FaceMood.Celebrating && motion)
        {
            Bounce();
        }

        SetEyeRest(mood, motion);
        SetLids(mood == FaceMood.Focused ? FocusedLid : 0, motion);
        SetThinking(mood == FaceMood.Thinking, motion);

        // Accessibility: never rely on the face alone, but describe it for screen readers.
        AutomationProperties.SetHelpText(this, Describe(mood));
    }

    private static Geometry MouthFor(FaceMood mood) => mood switch
    {
        FaceMood.Happy or FaceMood.Celebrating => SmileBig,
        FaceMood.Concerned => MouthConcerned,
        FaceMood.Thinking => MouthThinking,
        FaceMood.Listening => MouthOpen,
        FaceMood.Sleepy => MouthAsleep,
        FaceMood.Hurt => MouthWince,
        FaceMood.Angry => MouthPout,
        FaceMood.Focused => MouthFocused,
        _ => SmileSmall,
    };

    /// <summary>
    /// A wink (the AI's [face:wink]): the right eye closes for a beat with a lopsided smirk, then both come back.
    /// Skipped with reduced motion or when the eyes are already closed/curved.
    /// </summary>
    public void Wink()
    {
        if (ReducedMotion || Mood is FaceMood.Happy or FaceMood.Celebrating or FaceMood.Relaxed or FaceMood.Sleepy or FaceMood.Hurt)
        {
            return;
        }

        var open = EyeOpenScale;
        var wink = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
        wink.KeyFrames.Add(new EasingDoubleKeyFrame(0.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), Ease));
        wink.KeyFrames.Add(new EasingDoubleKeyFrame(0.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(470))));
        wink.KeyFrames.Add(new EasingDoubleKeyFrame(open, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), EaseInOut));
        wink.Freeze();
        RightEyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, wink, HandoffBehavior.SnapshotAndReplace);

        Mouth.Data = MouthSmirk;
        var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        back.Tick += (_, _) =>
        {
            back.Stop();
            Mouth.Data = MouthFor(Mood);
        };
        back.Start();
    }

    /// <summary>Small happy double hop (~600 ms). Skipped with reduced motion.</summary>
    public void Bounce()
    {
        if (ReducedMotion)
        {
            return;
        }

        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(600) };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), Ease));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), Ease));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), Ease));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), Ease));
        bounce.Freeze();
        BounceTransform.BeginAnimation(TranslateTransform.YProperty, bounce, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Fade(UIElement element, double to, bool animate, bool fast = false, bool slow = false)
    {
        if (!animate)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = to;
            return;
        }

        var duration = fast ? new Duration(TimeSpan.FromMilliseconds(70)) : slow ? Settle : Quick;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, duration) { EasingFunction = slow ? EaseInOut : Ease }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void Scale(ScaleTransform transform, double to, bool animate, bool slow = false)
    {
        if (!animate)
        {
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            transform.ScaleY = to;
            return;
        }

        transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, slow ? Settle : Quick) { EasingFunction = slow ? EaseInOut : Ease }, HandoffBehavior.SnapshotAndReplace);
    }

    private static string Describe(FaceMood mood) => mood switch
    {
        FaceMood.Happy => $"{Companion.Name} is smiling",
        FaceMood.Celebrating => $"{Companion.Name} is celebrating",
        FaceMood.Concerned => $"{Companion.Name} looks concerned",
        FaceMood.Thinking => $"{Companion.Name} is thinking",
        FaceMood.Listening => $"{Companion.Name} is listening",
        FaceMood.Sleepy => $"{Companion.Name} is resting",
        FaceMood.Hurt => $"{Companion.Name} is wincing",
        FaceMood.Angry => $"{Companion.Name} is annoyed",
        FaceMood.Focused => $"{Companion.Name} is focusing with you",
        FaceMood.Relaxed => $"{Companion.Name} is relaxing",
        _ => $"{Companion.Name} is ready",
    };

    private static Geometry Frozen(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
