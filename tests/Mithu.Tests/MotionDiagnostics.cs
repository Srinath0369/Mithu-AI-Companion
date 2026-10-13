using Mithu.Core.Motion;
using Xunit.Abstractions;

namespace Mithu.Tests;

/// <summary>Prints frame-by-frame values so motion can be inspected. Run with --logger "console;verbosity=detailed".</summary>
public sealed class MotionDiagnostics(ITestOutputHelper output)
{
    private static readonly IslandPose Notch = new(132, 24, 12, Header: 0, Panel: 0, NotchEyes: 1);
    private static readonly IslandPose Compact = new(372, 60, 24, Header: 1, Panel: 0, NotchEyes: 0);
    private static readonly IslandPose Expanded = new(420, 356, 26, Header: 1, Panel: 1, NotchEyes: 0);

    [Fact]
    public void Print_frames()
    {
        foreach (var (name, from, to) in new[]
                 {
                     ("Notch->Expanded", Notch, Expanded), ("Expanded->Notch", Expanded, Notch),
                     ("Compact->Expanded", Compact, Expanded), ("Expanded->Compact", Expanded, Compact),
                 })
        {
            var c = new IslandChoreography(from, to);
            output.WriteLine($"== {name}  duration {c.Duration:F3}s");
            var prev = c.Sample(0);
            for (var t = 0.0; t <= c.Duration + (1 / 60.0); t += 1 / 60.0)
            {
                var f = c.Sample(t);
                var flag = Math.Abs(f.PanelOpacity - prev.PanelOpacity) >= 0.25 ? "  <-- JUMP" : string.Empty;
                output.WriteLine($"t={t:F3} w={f.Width,6:F1} h={f.Height,6:F1} hdr={f.HeaderOpacity:F2} pnl={f.PanelOpacity:F2} eyes={f.NotchEyesOpacity:F2}{flag}");
                prev = f;
            }
        }
    }
}
