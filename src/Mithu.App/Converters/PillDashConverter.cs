using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Mithu.App.Converters;

/// <summary>
/// Progress (0–1) as a stroke that traces a rounded rectangle's outline clockwise from the middle of its top edge.
/// Bind it to both <c>StrokeDashArray</c> (the dash) and <c>StrokeDashOffset</c> (where it starts): the target type picks which.
/// Measured for a pill (radius = height / 2): WPF starts its stroke at the middle of the left edge and goes clockwise.
/// ConverterParameter = "width,height,radius,thickness" of the <c>Rectangle</c>; dash lengths are in units of thickness.
/// </summary>
public sealed class PillDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = (parameter as string ?? "76,60,30,3").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var (width, height, radius, thickness) = (p[0], p[1], Math.Min(p[2], Math.Min(p[0], p[1]) / 2), p[3]);

        var perimeter = (2 * (width - (2 * radius))) + (2 * (height - (2 * radius))) + (2 * Math.PI * radius);
        var lead = (Math.PI * radius / 2) + (width / 2) - radius; // stroke start → over the top-left corner → top centre

        if (targetType == typeof(double))
        {
            // The pattern repeats once per outline, so a dash past the end wraps round to the start by itself.
            return (perimeter - lead) / thickness;
        }

        var done = Math.Clamp(value is double v ? v : 0, 0, 1) * perimeter;
        return new DoubleCollection { done / thickness, (perimeter - done) / thickness };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
