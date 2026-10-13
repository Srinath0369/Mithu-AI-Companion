using System.Globalization;
using System.Windows.Media;
using Mithu.App.Converters;

namespace Mithu.Tests;

public class PillDashConverterTests
{
    // A 40×20 pill with radius 10: two 20-long straights plus a full circle.
    private const string Pill = "40,20,10,2";
    private static readonly double Perimeter = 40 + (2 * Math.PI * 10);
    private static readonly double Lead = (Math.PI * 10 / 2) + 10; // left middle → over the corner → top centre

    private static double[] Dashes(double progress) =>
        ((DoubleCollection)new PillDashConverter().Convert(progress, typeof(DoubleCollection), Pill, CultureInfo.InvariantCulture)).ToArray();

    private static double Offset() =>
        (double)new PillDashConverter().Convert(0.5, typeof(double), Pill, CultureInfo.InvariantCulture);

    [Fact]
    public void Dash_is_the_done_part_of_one_outline()
    {
        var d = Dashes(0.25);
        Assert.Equal(2, d.Length);
        Assert.Equal(Perimeter * 0.25 / 2, d[0], 6);
        Assert.Equal(Perimeter / 2, d[0] + d[1], 6); // pattern repeats once per outline, so it wraps at the start
    }

    [Fact]
    public void Offset_moves_the_dash_start_to_the_top_centre()
    {
        // Pattern position at outline distance s is s + offset; the dash must begin (≡ 0) at s = lead.
        var at = ((Lead / 2) + Offset()) % (Perimeter / 2);
        Assert.True(at < 1e-6 || Perimeter / 2 - at < 1e-6);
    }

    [Fact]
    public void Out_of_range_is_clamped()
    {
        Assert.Equal(Dashes(1), Dashes(3));
        Assert.Equal(Dashes(0), Dashes(-1));
    }
}
