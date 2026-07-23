using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Nomsidian;

/// <summary>アウトラインペインの「見出しがありません」プレースホルダ用。件数が0の時だけ表示する。</summary>
public sealed class ZeroToVisibleConverter : IValueConverter
{
    public static readonly ZeroToVisibleConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
