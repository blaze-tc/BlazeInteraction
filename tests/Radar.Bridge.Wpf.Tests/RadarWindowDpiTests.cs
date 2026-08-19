using System.Xml.Linq;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarWindowDpiTests
{
    [Fact]
    public void Bridge_DeclaresPerMonitorV2DpiAwareness()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Radar.Bridge.Wpf", "Radar.Bridge.Wpf.csproj"));
        var manifestName = project.Descendants("ApplicationManifest").Single().Value;
        var manifest = XDocument.Load(Path.Combine(root, "src", "Radar.Bridge.Wpf", manifestName));

        Assert.Contains(manifest.Descendants(), element =>
            element.Name.LocalName == "dpiAwareness" &&
            element.Value.Contains("PerMonitorV2", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplicationTheme_RoundsLayoutAndUsesClearTypeText()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Radar.Bridge.Wpf", "App.xaml"));
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var windowStyle = document.Descendants(presentation + "Style").Single(element =>
            (string?)element.Attribute("TargetType") == "Window");
        var setters = windowStyle.Elements(presentation + "Setter").ToArray();

        Assert.Contains(setters, setter =>
            (string?)setter.Attribute("Property") == "UseLayoutRounding" &&
            (string?)setter.Attribute("Value") == "True");
        Assert.Contains(setters, setter =>
            (string?)setter.Attribute("Property") == "SnapsToDevicePixels" &&
            (string?)setter.Attribute("Value") == "True");
        Assert.Contains(setters, setter =>
            (string?)setter.Attribute("Property") == "TextOptions.TextRenderingMode" &&
            (string?)setter.Attribute("Value") == "ClearType");
    }

    [Fact]
    public void Bridge_ForcesSoftwareRenderingBeforeCreatingAnyWindow()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Radar.Bridge.Wpf",
            "App.xaml.cs"));

        const string softwareRendering = "RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;";
        var renderingIndex = source.IndexOf(softwareRendering, StringComparison.Ordinal);
        var startupIndex = source.IndexOf("base.OnStartup(eventArgs);", StringComparison.Ordinal);

        Assert.True(renderingIndex >= 0, "RadarBridge must force WPF software rendering on projector PCs.");
        Assert.True(renderingIndex < startupIndex, "Software rendering must be selected before WPF creates a window.");
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
