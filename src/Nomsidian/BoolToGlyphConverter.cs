using System.Globalization;
using System.Windows.Data;

namespace Nomsidian;

public sealed class BoolToGlyphConverter : IValueConverter
{
    public static readonly BoolToGlyphConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "\U0001F4C1" : "\U0001F4C4";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
