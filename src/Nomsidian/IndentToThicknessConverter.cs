using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Nomsidian;

/// <summary>アウトラインの見出しレベルに応じたインデント幅(HeadingItem.IndentWidth)を左マージンに変換する。</summary>
public sealed class IndentToThicknessConverter : IValueConverter
{
    public static readonly IndentToThicknessConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness((value as double? ?? 0) + 14, 4, 14, 4);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
