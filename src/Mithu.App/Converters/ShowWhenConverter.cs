using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Mithu.App.Converters;

/// <summary>
/// Visible when the bound value's text equals the converter parameter (e.g. an enum name such as "Peek"),
/// collapsed otherwise. <see cref="Invert"/> flips it.
/// </summary>
public sealed class ShowWhenConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var match = string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal);
        return match != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
