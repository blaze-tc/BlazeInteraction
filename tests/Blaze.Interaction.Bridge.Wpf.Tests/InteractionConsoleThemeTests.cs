namespace Blaze.Interaction.Bridge.Wpf.Tests;

public sealed class InteractionConsoleThemeTests
{
    [Fact]
    public void SharedTheme_DefinesApprovedPaletteAndControlStyles()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Blaze.Interaction.Bridge.Wpf",
            "Resources",
            "InteractionConsoleTheme.xaml");

        Assert.True(File.Exists(path), $"Shared theme was not found at '{path}'.");
        var xaml = File.ReadAllText(path);
        foreach (var value in new[]
                 {
                     "#08111D", "#0E1B2B", "#132337", "#263B52", "#38D3D6",
                     "#FBA84C", "#EAF2FA", "#93A8BC", "#F55D5B"
                 })
        {
            Assert.Contains(value, xaml, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var key in new[]
                 {
                     "CardStyle", "SectionTitleStyle", "CaptionStyle", "PrimaryButtonStyle"
                 })
        {
            Assert.Contains($"x:Key=\"{key}\"", xaml, StringComparison.Ordinal);
        }

        foreach (var target in new[]
                 {
                     "Window", "Button", "ComboBox", "TextBox", "CheckBox",
                     "TabControl", "ListBox"
                 })
        {
            Assert.Contains($"TargetType=\"{target}\"", xaml, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(
        "providers/CameraVision/Blaze.Provider.CameraVision/Blaze.Provider.CameraVision.csproj",
        "..\\..\\..\\src\\Blaze.Interaction.Bridge.Wpf\\Resources\\InteractionConsoleTheme.xaml")]
    [InlineData(
        "src/Radar.Bridge.Wpf/Radar.Bridge.Wpf.csproj",
        "..\\Blaze.Interaction.Bridge.Wpf\\Resources\\InteractionConsoleTheme.xaml")]
    public void ProviderProjects_LinkTheSharedThemeSource(
        string relativeProjectPath,
        string expectedInclude)
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
        var project = File.ReadAllText(projectPath);

        Assert.Contains(
            $"<Page Include=\"{expectedInclude}\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "Link=\"Resources\\InteractionConsoleTheme.xaml\"",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RadarTheme_KeepsRadarConverterAndMergesSharedControls()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Radar.Bridge.Wpf",
            "RadarTheme.xaml");

        Assert.True(File.Exists(path), $"Radar wrapper theme was not found at '{path}'.");
        var xaml = File.ReadAllText(path);
        Assert.Contains(
            "/RadarBridge;component/Resources/InteractionConsoleTheme.xaml",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("FlexibleNumericTextConverter", xaml, StringComparison.Ordinal);
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

        throw new DirectoryNotFoundException(
            "Unable to locate RadarControl.sln from the test output directory.");
    }
}
