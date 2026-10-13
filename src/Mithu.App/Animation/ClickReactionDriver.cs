using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Mithu.Core.Models;
using Mithu.Core.Mood;

namespace Mithu.App.Animation;

/// <summary>
/// Plays the click-reaction sequence (smile → punch → angry → calm) on the face.
/// <see cref="ClickReactionMachine"/> decides <i>what</i> and <i>when</i>; this class runs a single
/// one-shot timer to the machine's next transition (no polling) and applies the result to the face.
/// </summary>
public sealed class ClickReactionDriver : IDisposable
{
    private static readonly string[] SmileLines = ["Hi there!", "Hehe, hello!", "That's me!", "Boop!"];
    private static readonly string[] PunchLines = ["Ow!", "Hey!", "Ouch!", "Stop it!", "Owww!"];
    private static readonly string[] AngryLines = ["Hmph!", "Grr…", "Not cool!", "I'm annoyed now."];
    private static readonly string[] CalmLines = ["Okay… I forgive you.", "Fine, we're friends again.", "Deep breath. All good."];

    private readonly ClickReactionMachine _machine = new();
    private readonly FaceAnimationCoordinator _face;
    private readonly TimeProvider _time;
    private readonly ILogger<ClickReactionDriver> _logger;
    private readonly DispatcherTimer _timer;
    private int _lineIndex;

    public ClickReactionDriver(FaceAnimationCoordinator face, TimeProvider time, ILogger<ClickReactionDriver> logger)
    {
        _face = face;
        _time = time;
        _logger = logger;
        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += (_, _) => Advance();
        _machine.PhaseChanged += OnPhaseChanged;
    }

    /// <summary>Status text to show for the latest reaction (always paired with the face, for accessibility).</summary>
    public event Action<string>? StatusChanged;

    public ReactionPhase Phase => _machine.Phase;

    /// <summary>Handles a face click while Mithu is open.</summary>
    public void Click()
    {
        var reaction = _machine.Click(_time.GetUtcNow());
        if (reaction == ClickReaction.Punch)
        {
            // Every punch replays the squash and a new "ow" line, even mid-streak.
            _face.Punch();
            StatusChanged?.Invoke(Next(PunchLines));
            _logger.LogInformation("Face punched (streak {Count})", _machine.PunchCount);
        }
        else
        {
            _logger.LogInformation("Face clicked: smile");
        }

        Schedule();
    }

    /// <summary>Clears any reaction immediately (e.g. Mithu was tucked away).</summary>
    public void Reset()
    {
        _timer.Stop();
        _machine.Reset();
    }

    private void Advance()
    {
        _timer.Stop();
        _machine.Tick(_time.GetUtcNow());
        Schedule();
    }

    private void Schedule()
    {
        _timer.Stop();
        if (_machine.NextWakeUp is not { } wake)
        {
            return;
        }

        var due = wake - _time.GetUtcNow();
        _timer.Interval = due > TimeSpan.Zero ? due : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    private void OnPhaseChanged(ReactionPhase phase)
    {
        switch (phase)
        {
            case ReactionPhase.Smiling:
                _face.Hold(FaceMood.Happy);
                StatusChanged?.Invoke(Next(SmileLines));
                break;

            case ReactionPhase.BeingPunched:
                _face.Hold(FaceMood.Hurt);
                break;

            case ReactionPhase.Angry:
                _face.Hold(FaceMood.Angry);
                StatusChanged?.Invoke(Next(AngryLines));
                _logger.LogInformation("Face angry for {Seconds}s", ClickReactionMachine.AngryDuration.TotalSeconds);
                break;

            case ReactionPhase.Calm:
                var wasAngry = _face.CurrentMood == FaceMood.Angry;
                _face.Release();
                StatusChanged?.Invoke(wasAngry ? Next(CalmLines) : "Ready when you are");
                if (wasAngry)
                {
                    _logger.LogInformation("Face calm again");
                }

                break;
        }
    }

    private string Next(string[] lines) => lines[_lineIndex++ % lines.Length];

    public void Dispose()
    {
        _timer.Stop();
        _machine.PhaseChanged -= OnPhaseChanged;
    }
}
