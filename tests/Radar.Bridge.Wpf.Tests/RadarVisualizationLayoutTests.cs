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
        var pointViews = document.Descendants(controls + "RadarPointCloudView").ToArray();
        var xaml = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var raw = pointViews.Single(element => (string?)element.Attribute(xaml + "Name") == "RawRadarView");
        var filtered = pointViews.Single(element => (string?)element.Attribute(xaml + "Name") == "FilteredRadarView");
        var fusion = document.Descendants(controls + "RadarScreenFusionView").Single();

        Assert.Contains(lists, element => (string?)element.Attribute("ItemsSource") == "{Binding Screens}");
        Assert.Contains(lists, element => (string?)element.Attribute("ItemsSource") == "{Binding SelectedScreen.Sensors}");
        Assert.Contains(tabs, element => (string?)element.Attribute("Header") == "屏幕参数");
        Assert.Contains(tabs, element => (string?)element.Attribute("Header") == "雷达参数");
        Assert.Equal("{Binding SelectedSensor.Snapshot}", (string?)raw.Attribute("Snapshot"));
        Assert.Equal("{Binding SelectedSensor.Snapshot}", (string?)filtered.Attribute("Snapshot"));
        Assert.Equal("{Binding SelectedScreen.LatestSnapshot}", (string?)fusion.Attribute("Snapshot"));
        Assert.Equal("{Binding SelectedScreen.Sensors}", (string?)fusion.Attribute("Sensors"));
        Assert.Equal("{Binding SelectedSensor.SensorId}", (string?)fusion.Attribute("SelectedSensorId"));
        Assert.Equal("True", (string?)raw.Attribute("ShowRawPoints"));
        Assert.Equal("False", (string?)raw.Attribute("ShowValidPoints"));
        Assert.Equal("False", (string?)raw.Attribute("ShowFilterOverlay"));
        Assert.Equal("False", (string?)raw.Attribute("IsRegionEditable"));
        Assert.Equal("False", (string?)filtered.Attribute("ShowRawPoints"));
        Assert.Equal("True", (string?)filtered.Attribute("ShowValidPoints"));
        Assert.Equal("True", (string?)filtered.Attribute("ShowFilterOverlay"));
        Assert.Equal("{Binding SelectedSensor.RegionVertices}", (string?)filtered.Attribute("RegionVertices"));
        Assert.Equal("{Binding SelectedSensor.MaskedPolygons}", (string?)filtered.Attribute("MaskedRegions"));
        Assert.Equal("{Binding SelectedSensor.LeftEdgeDeadZoneMeters}", (string?)filtered.Attribute("LeftEdgeDeadZoneMeters"));
        Assert.Equal("{Binding SelectedSensor.RightEdgeDeadZoneMeters}", (string?)filtered.Attribute("RightEdgeDeadZoneMeters"));
        Assert.Equal("{Binding SelectedSensor.TopEdgeDeadZoneMeters}", (string?)filtered.Attribute("TopEdgeDeadZoneMeters"));
        Assert.Equal("{Binding SelectedSensor.BottomEdgeDeadZoneMeters}", (string?)filtered.Attribute("BottomEdgeDeadZoneMeters"));
        Assert.Equal("True", (string?)filtered.Attribute("IsRegionEditable"));
        Assert.Equal("OnRegionVertexMoved", (string?)filtered.Attribute("RegionVertexMoved"));
        Assert.Contains(document.Descendants(presentation + "TextBlock"), element =>
            ((string?)element.Attribute("Text"))?.Contains("原始点观察", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(presentation + "TextBlock"), element =>
            ((string?)element.Attribute("Text"))?.Contains("拉框过滤结果", StringComparison.Ordinal) == true);
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
            "SelectedSensor.LeftEdgeDeadZoneMeters", "SelectedSensor.RightEdgeDeadZoneMeters",
            "SelectedSensor.TopEdgeDeadZoneMeters", "SelectedSensor.BottomEdgeDeadZoneMeters", "ApplyFastMotionPresetCommand",
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

    [Fact]
    public void MainWindow_AllEditableNumericBindingsUseConversionAndDataErrorValidation()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Radar.Bridge.Wpf", "MainWindow.xaml"));
        foreach (var property in new[]
        {
            "WidthPixels", "HeightPixels", "OutputRateHz", "SensorDataMaxAgeMilliseconds", "FusionDistancePixels",
            "ConfirmFrames", "LostFrames", "MaximumAssociationDistancePixels", "SmoothingAlpha", "DwellMilliseconds",
            "Port", "MinimumDistanceMeters", "MaximumDistanceMeters", "VisualizationRangeMeters", "RotationDegrees",
            "LeftEdgeDeadZoneMeters", "RightEdgeDeadZoneMeters", "TopEdgeDeadZoneMeters", "BottomEdgeDeadZoneMeters",
            "BaseGapMeters", "DistanceScale", "MinimumClusterPointCount", "MaximumClusterWidthMeters",
            "OutputX", "OutputY", "OutputWidth", "OutputHeight", "ReplaySpeed"
        })
        {
            var binding = xaml.Split('\n').First(line => line.Contains("<TextBox", StringComparison.Ordinal) && (line.Contains($"SelectedScreen.{property}", StringComparison.Ordinal) || line.Contains($"SelectedSensor.{property}", StringComparison.Ordinal)));
            Assert.Contains("ValidatesOnExceptions=True", binding);
            Assert.Contains("ValidatesOnDataErrors=True", binding);
        }
    }

    [Fact]
    public void BridgeGlobalLogs_UseTwoPartTags()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Radar.Bridge.Wpf", "Services", "RadarBridgeCoordinator.cs"));
        Assert.Contains("PublishLog($\"[IPC] batch=", source, StringComparison.Ordinal);
        Assert.Contains("latencyMs=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[SYSTEM]", source);
        Assert.Contains("[GLOBAL/IPC]", source);
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
