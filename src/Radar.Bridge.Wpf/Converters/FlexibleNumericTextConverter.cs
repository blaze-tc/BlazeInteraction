using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yuexin.Radar.Bridge.Wpf.Converters;

/// <summary>Allows continuous decimal editing and accepts either decimal separator on field PCs.</summary>
public sealed class FlexibleNumericTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IFormattable number
            ? number.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        if (text.EndsWith(".", StringComparison.Ordinal) || text.EndsWith(",", StringComparison.Ordinal))
        {
            return Binding.DoNothing;
        }

        var normalized = text
            .Replace('。', '.')
            .Replace('．', '.')
            .Replace('，', ',')
            .Replace(',', '.');
        var numericType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (numericType == typeof(float) && float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
        {
            return floatValue;
        }

        if (numericType == typeof(double) && double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        if (numericType == typeof(decimal) && decimal.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var decimalValue))
        {
            return decimalValue;
        }

        throw new FormatException("请输入有效数字，例如 0.15。");
    }
}
