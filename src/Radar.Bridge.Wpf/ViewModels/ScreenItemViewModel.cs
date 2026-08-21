using System.Collections.ObjectModel;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Bridge.Wpf.ViewModels;

/// <summary>Owns the editable configuration and derived state of one Unity screen.</summary>
public sealed class ScreenItemViewModel : ObservableObject, System.ComponentModel.IDataErrorInfo
{
    private readonly RadarScreenConfiguration _configuration;
    private RadarScreenRuntimeSnapshot? _latestSnapshot;

    public ScreenItemViewModel(RadarScreenConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Sensors = new ObservableCollection<SensorItemViewModel>(_configuration.Sensors.Select(sensor => new SensorItemViewModel(sensor)));
        foreach (var sensor in Sensors) sensor.PropertyChanged += OnSensorPropertyChanged;
    }

    public RadarScreenConfiguration Configuration => _configuration;
    public IReadOnlyList<RadarInteractionMode> AvailableInteractionModes { get; } = Enum.GetValues<RadarInteractionMode>();
    public string ScreenId => _configuration.ScreenId;
    public string UnityDisplayName => _configuration.UnityDisplayName;
    public int UnityOrder => _configuration.UnityOrder;
    public bool IsAssociated => _configuration.IsAssociated;
    public bool IsPrimary => _configuration.IsPrimary;
    public RadarResolutionMode ResolutionMode { get => _configuration.ResolutionMode; set => Set(value, () => _configuration.ResolutionMode, item => _configuration.ResolutionMode = item); }
    public int WidthPixels { get => _configuration.WidthPixels; set => Set(value, () => _configuration.WidthPixels, item => _configuration.WidthPixels = item); }
    public int HeightPixels { get => _configuration.HeightPixels; set => Set(value, () => _configuration.HeightPixels, item => _configuration.HeightPixels = item); }
    public int EffectiveWidthPixels => _configuration.EffectiveWidthPixels;
    public int EffectiveHeightPixels => _configuration.EffectiveHeightPixels;
    public string EffectiveResolutionText => $"{EffectiveWidthPixels} × {EffectiveHeightPixels}";
    public int OutputRateHz { get => _configuration.Fusion.OutputRateHz; set => Set(value, () => _configuration.Fusion.OutputRateHz, item => _configuration.Fusion.OutputRateHz = item); }
    public int SensorDataMaxAgeMilliseconds { get => _configuration.Fusion.SensorDataMaxAgeMilliseconds; set => Set(value, () => _configuration.Fusion.SensorDataMaxAgeMilliseconds, item => _configuration.Fusion.SensorDataMaxAgeMilliseconds = item); }
    public float FusionDistancePixels { get => _configuration.Fusion.FusionDistancePixels; set => Set(value, () => _configuration.Fusion.FusionDistancePixels, item => _configuration.Fusion.FusionDistancePixels = item); }
    public int ConfirmFrames { get => _configuration.Tracking.ConfirmFrames; set => Set(value, () => _configuration.Tracking.ConfirmFrames, item => _configuration.Tracking.ConfirmFrames = item); }
    public int LostFrames { get => _configuration.Tracking.LostFrames; set => Set(value, () => _configuration.Tracking.LostFrames, item => _configuration.Tracking.LostFrames = item); }
    public float MaximumAssociationDistancePixels { get => _configuration.Tracking.MaximumAssociationDistancePixels; set => Set(value, () => _configuration.Tracking.MaximumAssociationDistancePixels, item => _configuration.Tracking.MaximumAssociationDistancePixels = item); }
    public float SmoothingAlpha { get => _configuration.Tracking.SmoothingAlpha; set => Set(value, () => _configuration.Tracking.SmoothingAlpha, item => _configuration.Tracking.SmoothingAlpha = item); }
    public RadarInteractionMode InteractionMode { get => _configuration.Interaction.Mode; set => Set(value, () => _configuration.Interaction.Mode, item => _configuration.Interaction.Mode = item); }
    public int DwellMilliseconds { get => _configuration.Interaction.DwellMilliseconds; set => Set(value, () => _configuration.Interaction.DwellMilliseconds, item => _configuration.Interaction.DwellMilliseconds = item); }
    public ObservableCollection<SensorItemViewModel> Sensors { get; }
    public RadarScreenRuntimeSnapshot? LatestSnapshot { get => _latestSnapshot; private set => SetProperty(ref _latestSnapshot, value); }
    public int OnlineSensorCount => Sensors.Count(sensor => sensor.RuntimeState == Services.RadarSensorRuntimeState.Running);
    public int FusedTargetCount { get; private set; }
    public bool HasValidationErrors => !ValidateCopy(_configuration);
    public string Error => string.Empty;
    public string this[string columnName] => columnName switch
    {
        nameof(WidthPixels) or nameof(HeightPixels) when WidthPixels is < 1 or > 32768 || HeightPixels is < 1 or > 32768 => "Resolution must be between 1 and 32768 pixels.",
        nameof(OutputRateHz) when OutputRateHz is < 1 or > 240 => "Output rate must be between 1 and 240 Hz.",
        nameof(SensorDataMaxAgeMilliseconds) when SensorDataMaxAgeMilliseconds is < 10 or > 5000 => "Sensor data age must be between 10 and 5000 ms.",
        nameof(FusionDistancePixels) when !float.IsFinite(FusionDistancePixels) || FusionDistancePixels <= 0f => "Fusion distance must be positive.",
        nameof(MaximumAssociationDistancePixels) when !float.IsFinite(MaximumAssociationDistancePixels) || MaximumAssociationDistancePixels <= 0f => "Association distance must be positive.",
        nameof(SmoothingAlpha) when !float.IsFinite(SmoothingAlpha) || SmoothingAlpha is <= 0f or > 1f => "Smoothing alpha must be in (0, 1].",
        nameof(ConfirmFrames) or nameof(LostFrames) when ConfirmFrames < 1 || LostFrames < 1 => "Tracking frame counts must be positive.",
        nameof(DwellMilliseconds) when DwellMilliseconds < 0 => "Dwell duration cannot be negative.",
        _ => string.Empty
    };

    public SensorItemViewModel AddSensor(RadarSensorConfiguration sensor)
    {
        _configuration.Sensors.Add(sensor);
        var item = new SensorItemViewModel(sensor);
        Sensors.Add(item);
        item.PropertyChanged += OnSensorPropertyChanged;
        NotifySensorChanges();
        return item;
    }

    public void RemoveSensor(SensorItemViewModel sensor)
    {
        _configuration.Sensors.Remove(sensor.Configuration);
        sensor.PropertyChanged -= OnSensorPropertyChanged;
        Sensors.Remove(sensor);
        NotifySensorChanges();
    }

    public void ApplyFusedTargetCount(int count) { FusedTargetCount = count; OnPropertyChanged(nameof(FusedTargetCount)); }
    public void ApplySnapshot(RadarScreenRuntimeSnapshot snapshot)
    {
        LatestSnapshot = snapshot;
        ApplyFusedTargetCount(snapshot.Targets.Count);
    }
    public void NotifySensorChanges() { OnPropertyChanged(nameof(OnlineSensorCount)); OnPropertyChanged(nameof(HasValidationErrors)); }
    private void OnSensorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SensorItemViewModel.RuntimeState) or nameof(SensorItemViewModel.HasValidationErrors)) NotifySensorChanges();
    }
    private static bool ValidateCopy(RadarScreenConfiguration configuration)
    {
        var copy = new RadarAppConfiguration { Screens = [RadarConfigurationStore.CloneRuntime(configuration)] };
        return ConfigurationValidator.ValidateAndNormalize(copy).IsValid;
    }

    private void Set<T>(T value, Func<T> get, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(get(), value)) return;
        assign(value);
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(EffectiveWidthPixels)); OnPropertyChanged(nameof(EffectiveHeightPixels)); OnPropertyChanged(nameof(EffectiveResolutionText)); OnPropertyChanged(nameof(HasValidationErrors));
    }
}
