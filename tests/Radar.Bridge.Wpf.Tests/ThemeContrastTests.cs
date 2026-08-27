using System.Xml.Linq;
using System.Windows.Media;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class ThemeContrastTests
{
    [Theory]
    [InlineData("ListBox")]
    [InlineData("ListBoxItem")]
    [InlineData("TabControl")]
    [InlineData("TabItem")]
    [InlineData("CheckBox")]
    [InlineData("Button")]
    [InlineData("TextBox")]
    public void ApplicationTheme_GivesInteractiveControlsExplicitDarkSurfaces(string targetType)
    {
        var document = XDocument.Load(SharedThemePath());
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var style = document.Descendants(presentation + "Style").Single(element =>
            (string?)element.Attribute("TargetType") == targetType &&
            element.Attribute(xaml + "Key") is null);
        var setters = style.Elements(presentation + "Setter").ToArray();

        Assert.Contains(setters, setter => (string?)setter.Attribute("Property") == "Foreground");
        Assert.Contains(setters, setter => (string?)setter.Attribute("Property") == "Background");
        Assert.Contains(style.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsEnabled" &&
            (string?)trigger.Attribute("Value") == "False");
    }

    [Fact]
    public void ApplicationTheme_GivesTextAndComboBoxesReadableForegrounds()
    {
        var document = XDocument.Load(SharedThemePath());
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

        var implicitTextStyle = document.Descendants(presentation + "Style").Single(element =>
            (string?)element.Attribute("TargetType") == "TextBlock" &&
            element.Attribute(xaml + "Key") is null);
        Assert.Contains(
            implicitTextStyle.Elements(presentation + "Setter"),
            setter => (string?)setter.Attribute("Property") == "Foreground" &&
                      (string?)setter.Attribute("Value") == "{StaticResource TextPrimaryBrush}");

        var comboStyle = document.Descendants(presentation + "Style").Single(element =>
            (string?)element.Attribute("TargetType") == "ComboBox");
        Assert.Contains(comboStyle.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Foreground" &&
            (string?)setter.Attribute("Value") == "{StaticResource InputForegroundBrush}");
        Assert.Contains(comboStyle.Elements(presentation + "Setter"), setter =>
            (string?)setter.Attribute("Property") == "Background" &&
            (string?)setter.Attribute("Value") == "{StaticResource InputBackgroundBrush}");

        var comboResources = comboStyle.Element(presentation + "Style.Resources");
        Assert.NotNull(comboResources);
        Assert.Contains(comboResources.Elements(presentation + "SolidColorBrush"), brush =>
            (string?)brush.Attribute(xaml + "Key") == "{x:Static SystemColors.WindowBrushKey}" &&
            (string?)brush.Attribute("Color") == "{StaticResource InputBackgroundColor}");
        Assert.Contains(comboResources.Elements(presentation + "SolidColorBrush"), brush =>
            (string?)brush.Attribute(xaml + "Key") == "{x:Static SystemColors.WindowTextBrushKey}" &&
            (string?)brush.Attribute("Color") == "{StaticResource InputForegroundColor}");
        Assert.Contains(comboResources.Elements(presentation + "SolidColorBrush"), brush =>
            (string?)brush.Attribute(xaml + "Key") == "{x:Static SystemColors.GrayTextBrushKey}" &&
            (string?)brush.Attribute("Color") == "{StaticResource TextSecondaryColor}");

        var comboTemplate = comboStyle.Descendants(presentation + "ControlTemplate").First();
        Assert.Contains(comboTemplate.Descendants(presentation + "TextBox"), element =>
            (string?)element.Attribute(xaml + "Name") == "PART_EditableTextBox" &&
            (string?)element.Attribute("Foreground") == "{TemplateBinding Foreground}");
        Assert.Contains(comboTemplate.Descendants(presentation + "Popup"), element =>
            (string?)element.Attribute(xaml + "Name") == "PART_Popup");

        var foreground = ReadColor(document, presentation, xaml, "InputForegroundColor");
        var background = ReadColor(document, presentation, xaml, "InputBackgroundColor");
        Assert.True(
            ContrastRatio(foreground, background) >= 4.5,
            $"ComboBox contrast was {ContrastRatio(foreground, background):0.00}:1.");
    }

    [Fact]
    public void ApplicationTheme_CheckBoxHasVisibleDarkThemeStatesAndCheckedGlyph()
    {
        var document = XDocument.Load(SharedThemePath());
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var style = document.Descendants(presentation + "Style").Single(element =>
            (string?)element.Attribute("TargetType") == "CheckBox" && element.Attribute(xaml + "Key") is null);
        var template = style.Descendants(presentation + "ControlTemplate").Single();
        var indicator = template.Descendants(presentation + "Border").Single(element =>
            (string?)element.Attribute(xaml + "Name") == "CheckBoxIndicator");
        var checkMark = template.Descendants(presentation + "Path").Single(element =>
            (string?)element.Attribute(xaml + "Name") == "CheckMark");

        Assert.Equal("{StaticResource InputBackgroundBrush}", (string?)indicator.Attribute("Background"));
        Assert.Equal("Collapsed", (string?)checkMark.Attribute("Visibility"));
        Assert.Contains(template.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsChecked" &&
            (string?)trigger.Attribute("Value") == "True" &&
            trigger.Descendants(presentation + "Setter").Any(setter =>
                (string?)setter.Attribute("TargetName") == "CheckBoxIndicator" &&
                (string?)setter.Attribute("Property") == "Background" &&
                (string?)setter.Attribute("Value") == "{StaticResource PrimaryBrush}") &&
            trigger.Descendants(presentation + "Setter").Any(setter =>
                (string?)setter.Attribute("TargetName") == "CheckMark" &&
                (string?)setter.Attribute("Property") == "Visibility" &&
                (string?)setter.Attribute("Value") == "Visible"));
        Assert.Contains(template.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsMouseOver" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(template.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsEnabled" &&
            (string?)trigger.Attribute("Value") == "False");
    }

    [Fact]
    public void ApplicationTheme_TabItemOwnsEveryVisualStateAndKeepsHeaderForegroundReadable()
    {
        var document = XDocument.Load(SharedThemePath());
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var style = document.Descendants(presentation + "Style").Single(element =>
            (string?)element.Attribute("TargetType") == "TabItem" && element.Attribute(xaml + "Key") is null);
        var template = style.Descendants(presentation + "ControlTemplate").Single();
        var headerBorder = template.Descendants(presentation + "Border").Single(element =>
            (string?)element.Attribute(xaml + "Name") == "TabHeaderBorder");
        var headerContent = template.Descendants(presentation + "ContentPresenter").Single(element =>
            (string?)element.Attribute(xaml + "Name") == "TabHeaderContent");
        var styleSetters = style.Elements(presentation + "Setter").ToArray();

        Assert.Equal("{TemplateBinding Background}", (string?)headerBorder.Attribute("Background"));
        Assert.Equal("{TemplateBinding Foreground}", (string?)headerContent.Attribute("TextElement.Foreground"));
        Assert.Equal("Center", (string?)headerContent.Attribute("HorizontalAlignment"));
        Assert.Equal("Center", (string?)headerContent.Attribute("VerticalAlignment"));
        Assert.Contains(styleSetters, setter =>
            (string?)setter.Attribute("Property") == "HorizontalContentAlignment" &&
            (string?)setter.Attribute("Value") == "Stretch");
        Assert.Contains(styleSetters, setter =>
            (string?)setter.Attribute("Property") == "VerticalContentAlignment" &&
            (string?)setter.Attribute("Value") == "Stretch");

        Assert.Contains(template.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsSelected" &&
            (string?)trigger.Attribute("Value") == "True" &&
            trigger.Descendants(presentation + "Setter").Any(setter =>
                (string?)setter.Attribute("TargetName") == "TabHeaderBorder" &&
                (string?)setter.Attribute("Property") == "Background" &&
                (string?)setter.Attribute("Value") == "#173047") &&
            trigger.Descendants(presentation + "Setter").Any(setter =>
                (string?)setter.Attribute("Property") == "Foreground" &&
                (string?)setter.Attribute("Value") == "{StaticResource PrimaryBrush}"));
        Assert.Contains(template.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsMouseOver" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(template.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsKeyboardFocusWithin" &&
            (string?)trigger.Attribute("Value") == "True");
        Assert.Contains(template.Descendants(presentation + "Trigger"), trigger =>
            (string?)trigger.Attribute("Property") == "IsEnabled" &&
            (string?)trigger.Attribute("Value") == "False");
    }

    private static Color ReadColor(XDocument document, XNamespace presentation, XNamespace xaml, string key)
    {
        var text = document.Descendants(presentation + "Color")
            .Single(element => (string?)element.Attribute(xaml + "Key") == key)
            .Value;
        return (Color)ColorConverter.ConvertFromString(text)!;
    }

    private static string SharedThemePath() => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "Blaze.Interaction.Bridge.Wpf",
        "Resources",
        "InteractionConsoleTheme.xaml");

    private static double ContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
        var darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) +
               0.7152 * Linearize(color.G) +
               0.0722 * Linearize(color.B);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate RadarControl.sln from the test output directory.");
    }
}
