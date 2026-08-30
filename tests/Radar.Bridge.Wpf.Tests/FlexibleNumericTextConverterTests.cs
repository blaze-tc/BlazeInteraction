using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class FlexibleNumericTextConverterTests
{
    [Fact]
    public void ConvertBack_AcceptsDotAndCommaDecimalsAcrossWindowsCultures()
    {
        var converter = CreateConverter();
        var french = CultureInfo.GetCultureInfo("fr-FR");

        Assert.Equal(0.125f, Assert.IsType<float>(converter.ConvertBack("0.125", typeof(float), null, french)));
        Assert.Equal(0.125f, Assert.IsType<float>(converter.ConvertBack("0,125", typeof(float), null, french)));
        Assert.Equal(1.75d, Assert.IsType<double>(converter.ConvertBack("1.75", typeof(double), null, french)));
    }

    [Theory]
    [InlineData("0.")]
    [InlineData("0,")]
    public void ConvertBack_KeepsIncompleteEditingTextWithoutOverwritingTheTextBox(string text)
    {
        var converter = CreateConverter();

        var result = converter.ConvertBack(text, typeof(float), null, CultureInfo.GetCultureInfo("zh-CN"));

        Assert.Same(Binding.DoNothing, result);
    }

    private static IValueConverter CreateConverter()
    {
        var type = typeof(MainWindow).Assembly.GetType("Yuexin.Radar.Bridge.Wpf.Converters.FlexibleNumericTextConverter");
        Assert.NotNull(type);
        return Assert.IsAssignableFrom<IValueConverter>(Activator.CreateInstance(type));
    }
}
