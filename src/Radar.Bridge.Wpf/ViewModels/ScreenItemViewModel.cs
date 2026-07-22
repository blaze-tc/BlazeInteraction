using System.Collections.ObjectModel;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Bridge.Wpf.ViewModels;

/// <summary>Owns the editable configuration and derived state of one Unity screen.</summary>
public sealed class ScreenItemViewModel : ObservableObject
{
    private readonly RadarScreenConfiguration _configuration;

    public ScreenItemViewModel(RadarScreenConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Sensors = new ObservableCollection<SensorItemViewModel>(_configuration.Sensors.Select(sensor => new SensorItemViewModel(sensor)));
        foreach (var sensor in Sensors) sensor.PropertyChanged += OnSensorPropertyChanged;
    }

    public RadarScreenConfiguration Configuration => _configuration;
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
    public int OnlineSensorCount => Sensors.Count(sensor => sensor.RuntimeState == Services.RadarSensorRuntimeState.Running);
    public int FusedTargetCount { get; private set; }
    public bool HasValidationErrors => !ValidateCopy(new RadarAppConfiguration { Screens = [_configuration] });

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
    public void NotifySensorChanges() { OnPropertyChanged(nameof(OnlineSensorCount)); OnPropertyChanged(nameof(HasValidationErrors)); }
    private void OnSensorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SensorItemViewModel.RuntimeState) or nameof(SensorItemViewModel.HasValidationErrors)) NotifySensorChanges();
    }
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
        OnPropertyChanged(nameof(EffectiveWidthPixels)); OnPropertyChanged(nameof(EffectiveHeightPixels)); OnPropertyChanged(nameof(EffectiveResolutionText)); OnPropertyChanged(nameof(HasValidationErrors));
    }
}
