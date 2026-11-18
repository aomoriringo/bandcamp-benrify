using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BandcampBenrify.Infrastructure;

/// <summary>
/// サポーター数の棒の長さ: [サポーター数, 一覧での最大値] → 幅 (px)。ConverterParameter が最大の幅。
/// 未取得 (負の値) や最大値が分からないときは 0。
/// </summary>
public sealed class SupporterBarWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var maxWidth = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 40;
        if (values is [double value, double max] && value > 0 && max > 0)
        {
            return Math.Max(2, Math.Round(maxWidth * Math.Min(1, value / max)));
        }

        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
