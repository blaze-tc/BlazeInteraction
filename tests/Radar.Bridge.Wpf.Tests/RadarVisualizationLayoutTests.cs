using System.Xml.Linq;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarVisualizationLayoutTests
{
    [Fact]
    public void MainWindow_ContainsScreenSensorListsAndScopedParameterTabs()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Radar.Bridge.Wpf", "MainWindow.xaml"));
        var presentation = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        var controls = XNamespace.Get("clr-namespace:Yuexin.Radar.Bridge.Wpf.Controls");

        var lists = document.Descendants(presentation + "ListBox").ToArray();
        var tabs = document.Descendants(presentation + "TabItem").ToArray();
        var raw = document.Descendants(controls + "RadarPointCloudView").Single();
        var fusion = document.Descendants(controls + "RadarScreenFusionView").Single();

        Assert.Contains(lists, element => (string?)element.Attribute("ItemsSource") == "{Binding Screens}");
        Assert.Contains(lists, element => (string?)element.Attribute("ItemsSource") == "{Binding SelectedScreen.Sensors}");
        Assert.Contains(tabs, element => (string?)element.Attribute("Header") == "屏幕参数");
        Assert.Contains(tabs, element => (string?)element.Attribute("Header") == "雷达参数");
        Assert.Equal("{Binding SelectedSensor.Snapshot}", (string?)raw.Attribute("Snapshot"));
        Assert.Equal("{Binding SelectedScreen.LatestSnapshot}", (string?)fusion.Attribute("Snapshot"));
        Assert.Equal("{Binding SelectedScreen.Sensors}", (string?)fusion.Attribute("Sensors"));
        Assert.Equal("{Binding SelectedSensor.SensorId}", (string?)fusion.Attribute("SelectedSensorId"));
        Assert.Equal("{Binding SelectedSensor.RegionVertices}", (string?)raw.Attribute("RegionVertices"));
        Assert.Equal("{Binding SelectedSensor.MaskedPolygons}", (string?)raw.Attribute("MaskedRegions"));
        Assert.Equal("True", (string?)raw.Attribute("IsRegionEditable"));
        Assert.Equal("OnRegionVertexMoved", (string?)raw.Attribute("RegionVertexMoved"));
    }

    [Fact]
    public void FusionView_UsesScreenSpaceAndStableMarkers()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Radar.Bridge.Wpf", "Controls", "RadarScreenFusionView.cs"));

        Assert.Contains("MarkerRadius = 2.5", source);
        Assert.Contains("SnapsToDevicePixels = true", source);
        Assert.Contains("ToView(float pixelX, float pixelY)", source);
        Assert.DoesNotContain("DispatcherPriority.Render", source);
    }

    [Fact]
    public void MainViewModel_DoesNotExposeTemporaryFlatUiAdaptersOrLegacySnapshotDto()
    {
        var root = FindRepositoryRoot();
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "Radar.Bridge.Wpf", "ViewModels", "MainViewModel.cs"));
        var runtime = File.ReadAllText(Path.Combine(root, "src", "Radar.Bridge.Wpf", "Services", "IRadarBridgeRuntime.cs"));

        Assert.DoesNotContain("Temporary compatibility surface", viewModel);
        Assert.DoesNotContain("RadarRuntimeSnapshot", viewModel);
        Assert.DoesNotContain("public string RadarIp", viewModel);
        Assert.DoesNotContain("record RadarRuntimeSnapshot", runtime);
    }

    [Fact]
    public void MainWindow_ExposesScopedConfigurationAndFileCommands()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Radar.Bridge.Wpf", "MainWindow.xaml"));
        foreach (var binding in new[]
        {
            "SelectedSensor.Enabled", "SelectedSensor.SourceMode", "SelectedSensor.LocalIp",
            "SelectedSensor.BaseGapMeters", "SelectedSensor.OutputX", "SelectedScreen.InteractionMode",
            "SaveConfigurationCommand", "BeginCalibrationCommand", "UndoCalibrationPointCommand",
            "ClearCalibrationCommand", "DeleteMaskedRegionCommand", "StartRecordingCommand",
            "SelectReplayFileCommand", "PauseReplayCommand", "StepReplayCommand", "StopReplayCommand"
        })
        {
            Assert.Contains(binding, xaml);
        }
        Assert.DoesNotContain("Click=\"OnReplayClick\"", xaml);
        Assert.DoesNotContain("Click=\"OnStartRecordingClick\"", xaml);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate RadarControl.sln from the test output directory.");
    }
}
