using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using RadarPixelRect = Yuexin.Radar.Configuration.RadarPixelRect;

namespace Yuexin.Radar.Bridge.Wpf.ViewModels;

/// <summary>Owns the editable configuration and live state of one sensor.</summary>
public sealed class SensorItemViewModel : ObservableObject
{
    private readonly RadarSensorConfiguration _configuration;
    private RadarSensorRuntimeSnapshot? _snapshot;
    private RadarSensorRuntimeState _runtimeState;
    private string _calibrationStatus = "Not calibrated";
    private string _calibrationStep = "Not started";

    public SensorItemViewModel(RadarSensorConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public RadarSensorConfiguration Configuration => _configuration;
    public string SensorId => _configuration.SensorId;
    public string DisplayName { get => _configuration.DisplayName; set => Set(value, () => _configuration.DisplayName, item => _configuration.DisplayName = item); }
    public bool Enabled { get => _configuration.Enabled; set => Set(value, () => _configuration.Enabled, item => _configuration.Enabled = item); }
    public RadarSensorSourceMode SourceMode { get => _configuration.SourceMode; set => Set(value, () => _configuration.SourceMode, item => _configuration.SourceMode = item); }
    public RadarModel DeviceModel { get => _configuration.Device.DeviceModel; set => Set(value, () => _configuration.Device.DeviceModel, item => _configuration.Device.DeviceModel = item); }
    public string RadarIp { get => _configuration.Device.RadarIp; set => Set(value, () => _configuration.Device.RadarIp, item => _configuration.Device.RadarIp = item); }
    public int Port { get => _configuration.Device.Port; set => Set(value, () => _configuration.Device.Port, item => _configuration.Device.Port = item); }
    public string LocalIp { get => _configuration.Device.LocalIp; set => Set(value, () => _configuration.Device.LocalIp, item => _configuration.Device.LocalIp = item); }
    public bool AutoReconnect { get => _configuration.Device.AutoReconnect; set => Set(value, () => _configuration.Device.AutoReconnect, item => _configuration.Device.AutoReconnect = item); }
    public float RotationDegrees { get => _configuration.Transform.RotationDegrees; set => Set(value, () => _configuration.Transform.RotationDegrees, item => _configuration.Transform.RotationDegrees = item); }
    public bool FlipX { get => _configuration.Transform.FlipX; set => Set(value, () => _configuration.Transform.FlipX, item => _configuration.Transform.FlipX = item); }
    public bool FlipY { get => _configuration.Transform.FlipY; set => Set(value, () => _configuration.Transform.FlipY, item => _configuration.Transform.FlipY = item); }
    public float OffsetXMeters { get => _configuration.Transform.OffsetXMeters; set => Set(value, () => _configuration.Transform.OffsetXMeters, item => _configuration.Transform.OffsetXMeters = item); }
    public float OffsetYMeters { get => _configuration.Transform.OffsetYMeters; set => Set(value, () => _configuration.Transform.OffsetYMeters, item => _configuration.Transform.OffsetYMeters = item); }
    public float MinimumDistanceMeters { get => _configuration.Range.MinimumDistanceMeters; set => Set(value, () => _configuration.Range.MinimumDistanceMeters, item => _configuration.Range.MinimumDistanceMeters = item); }
    public float MaximumDistanceMeters { get => _configuration.Range.MaximumDistanceMeters; set => Set(value, () => _configuration.Range.MaximumDistanceMeters, item => _configuration.Range.MaximumDistanceMeters = item); }
    public float VisualizationRangeMeters { get => _configuration.Range.VisualizationRangeMeters; set => Set(value, () => _configuration.Range.VisualizationRangeMeters, item => _configuration.Range.VisualizationRangeMeters = item); }
    public float MinimumAngleDegrees { get => _configuration.Range.MinimumAngleDegrees; set => Set(value, () => _configuration.Range.MinimumAngleDegrees, item => _configuration.Range.MinimumAngleDegrees = item); }
    public float MaximumAngleDegrees { get => _configuration.Range.MaximumAngleDegrees; set => Set(value, () => _configuration.Range.MaximumAngleDegrees, item => _configuration.Range.MaximumAngleDegrees = item); }
    public IReadOnlyList<RadarPoint2> ActivePolygon => _configuration.Range.ActivePolygon;
    public IReadOnlyList<IReadOnlyList<RadarPoint2>> MaskedPolygons => _configuration.Range.MaskedPolygons;
    public float BaseGapMeters { get => _configuration.Clustering.BaseGapMeters; set => Set(value, () => _configuration.Clustering.BaseGapMeters, item => _configuration.Clustering.BaseGapMeters = item); }
    public float DistanceScale { get => _configuration.Clustering.DistanceScale; set => Set(value, () => _configuration.Clustering.DistanceScale, item => _configuration.Clustering.DistanceScale = item); }
    public int MinimumClusterPointCount { get => _configuration.Clustering.MinimumClusterPointCount; set => Set(value, () => _configuration.Clustering.MinimumClusterPointCount, item => _configuration.Clustering.MinimumClusterPointCount = item); }
    public float MaximumClusterWidthMeters { get => _configuration.Clustering.MaximumClusterWidthMeters; set => Set(value, () => _configuration.Clustering.MaximumClusterWidthMeters, item => _configuration.Clustering.MaximumClusterWidthMeters = item); }
    public RadarCalibrationConfiguration Calibration => _configuration.Calibration;
    public RadarPixelRect OutputRectPixels { get => _configuration.OutputRectPixels; set => Set(value, () => _configuration.OutputRectPixels, item => _configuration.OutputRectPixels = item); }
    public int OutputX { get => OutputRectPixels.X; set => OutputRectPixels = OutputRectPixels with { X = value }; }
    public int OutputY { get => OutputRectPixels.Y; set => OutputRectPixels = OutputRectPixels with { Y = value }; }
    public int OutputWidth { get => OutputRectPixels.Width; set => OutputRectPixels = OutputRectPixels with { Width = value }; }
    public int OutputHeight { get => OutputRectPixels.Height; set => OutputRectPixels = OutputRectPixels with { Height = value }; }
    public string ReplayFilePath { get => _configuration.ReplayFilePath; set => Set(value, () => _configuration.ReplayFilePath, item => _configuration.ReplayFilePath = item); }
    public double ReplaySpeed { get => _configuration.ReplaySpeed; set => Set(value, () => _configuration.ReplaySpeed, item => _configuration.ReplaySpeed = item); }
    public bool ReplayLoop { get => _configuration.ReplayLoop; set => Set(value, () => _configuration.ReplayLoop, item => _configuration.ReplayLoop = item); }
    public RadarSensorRuntimeState RuntimeState { get => _runtimeState; private set => SetProperty(ref _runtimeState, value); }
    public RadarSensorRuntimeSnapshot? Snapshot { get => _snapshot; private set => SetProperty(ref _snapshot, value); }
    public string FrequencyText => $"{Snapshot?.ScanFrequencyHz ?? 0d:0.0} Hz";
    public long CrcErrorCount => Snapshot?.CrcErrorCount ?? 0;
    public long DroppedInputFrameCount => Snapshot?.DroppedInputFrameCount ?? 0;
    public string CalibrationStatus { get => _calibrationStatus; set => SetProperty(ref _calibrationStatus, value); }
    public string CalibrationStep { get => _calibrationStep; set => SetProperty(ref _calibrationStep, value); }
    public bool HasValidationErrors => !ValidateCopy(new RadarAppConfiguration { Screens = [new RadarScreenConfiguration { IsAssociated = false, Sensors = [_configuration] }] });

    public void ApplySnapshot(RadarSensorRuntimeSnapshot snapshot)
    {
        Snapshot = snapshot;
        OnPropertyChanged(nameof(FrequencyText));
        OnPropertyChanged(nameof(CrcErrorCount));
        OnPropertyChanged(nameof(DroppedInputFrameCount));
    }

    public void ApplyRuntimeState(RadarSensorRuntimeState state) => RuntimeState = state;
    public void NotifyRegionChanged() => OnPropertyChanged(nameof(ActivePolygon));
    private static bool ValidateCopy(RadarAppConfiguration configuration)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(configuration);
        var copy = System.Text.Json.JsonSerializer.Deserialize<RadarAppConfiguration>(json) ?? throw new InvalidOperationException("Could not clone configuration for validation.");
        return ConfigurationValidator.ValidateAndNormalize(copy).IsValid;
    }

    private void Set<T>(T value, Func<T> get, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(get(), value)) return;
        assign(value);
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(HasValidationErrors));
        if (name == nameof(OutputRectPixels))
        {
            OnPropertyChanged(nameof(OutputX)); OnPropertyChanged(nameof(OutputY)); OnPropertyChanged(nameof(OutputWidth)); OnPropertyChanged(nameof(OutputHeight));
        }
    }
}
