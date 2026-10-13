using System.Diagnostics;
using System.Windows.Media;
using Mithu.Core.Motion;

namespace Mithu.App.Animation;

/// <summary>
/// Plays an <see cref="IslandChoreography"/> in sync with WPF's render loop.
/// <para>
/// One frame callback drives every property together (shape, face, content), so nothing drifts out of
/// step. It subscribes to <see cref="CompositionTarget.Rendering"/> only while a transition runs:
/// zero cost when idle.
/// </para>
/// Starting a new transition mid-flight continues from the current frame, so reversals never jump.
/// </summary>
public sealed class IslandAnimator
{
    private readonly Action<IslandFrame> _apply;
    private readonly Stopwatch _clock = new();
    private IslandChoreography? _current;
    private Action? _completed;
    private bool _subscribed;
    private bool _clockStarted;
    private TimeSpan _lastRenderTime;

    public IslandAnimator(Action<IslandFrame> apply, IslandFrame initial)
    {
        _apply = apply;
        Frame = initial;
    }

    /// <summary>The most recently applied frame (= current on-screen state).</summary>
    public IslandFrame Frame { get; private set; }

    public bool IsRunning => _current is not null;

    /// <summary>Animates from the current frame to <paramref name="target"/>.</summary>
    public void AnimateTo(IslandPose target, Action? completed = null, Spring? spring = null)
    {
        _current = new IslandChoreography(Frame.ToPose(), target, spring);
        _completed = completed;

        // The clock starts on the first rendered frame, not now: the first open builds and lays out the panel
        // before anything is drawn, and that time must not eat the start of the motion.
        _clockStarted = false;
        _lastRenderTime = TimeSpan.Zero;

        if (!_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
    }

    /// <summary>Jumps straight to <paramref name="target"/> (reduced motion, first layout).</summary>
    public void SnapTo(IslandPose target)
    {
        var choreography = new IslandChoreography(target, target);
        Stop();
        Apply(choreography.Final);
    }

    public void Stop()
    {
        _current = null;
        _completed = null;
        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering can fire more than once per frame; only advance on a new frame.
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderTime)
            {
                return;
            }

            _lastRenderTime = args.RenderingTime;
        }

        if (_current is not { } choreography)
        {
            Stop();
            return;
        }

        if (!_clockStarted)
        {
            _clock.Restart();
            _clockStarted = true;
        }

        var t = _clock.Elapsed.TotalSeconds;
        Apply(choreography.Sample(t));

        if (t >= choreography.Duration)
        {
            var completed = _completed;
            Stop();
            completed?.Invoke();
        }
    }

    private void Apply(IslandFrame frame)
    {
        Frame = frame;
        _apply(frame);
    }
}
