using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Mithu.App.Converters;

/// <summary>
/// Turns progress (0–1) into a <c>StrokeDashArray</c> that draws that fraction of a circle's outline.
/// ConverterParameter = "diameter,thickness" of the <c>Ellipse</c> (dash lengths are in units of stroke thickness).
/// </summary>
public sealed class RingDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "100,8").Split(',');
        var diameter = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var thickness = double.Parse(parts[1], CultureInfo.InvariantCulture);

        // WPF strokes an ellipse on a path inset by half the thickness.
        var circumference = Math.PI * (diameter - thickness) / thickness;
        var progress = Math.Clamp(value is double p ? p : 0, 0, 1);
        return new DoubleCollection { progress * circumference, circumference * 2 };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
