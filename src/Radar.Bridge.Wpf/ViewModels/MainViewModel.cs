using System.Collections.ObjectModel;
using System.Windows.Input;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Processing;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;
using RadarPixelRect = Yuexin.Radar.Configuration.RadarPixelRect;
using System.Windows.Threading;

namespace Yuexin.Radar.Bridge.Wpf.ViewModels;

/// <summary>Coordinates selected screen/sensor UI state; processing remains in the runtime.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaximumLogEntries = 500;
    private readonly RadarAppConfiguration _configuration;
    private readonly IRadarBridgeRuntime _runtime;
    private readonly SynchronizationContext _uiContext;
    private readonly Queue<string> _rawLogs = [];
    private readonly Dictionary<string, DateTimeOffset> _lastMoveLogAt = new(StringComparer.OrdinalIgnoreCase);
    private ScreenItemViewModel? _selectedScreen;
    private SensorItemViewModel? _selectedSensor;
    private string _selectedLogScreenId = "*";
    private string _selectedLogSensorId = "*";
    private UnityClientStatus _unityStatus;
    private bool _disposed;

    public MainViewModel(RadarAppConfiguration configuration, IRadarBridgeRuntime runtime)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _uiContext = SynchronizationContext.Current ?? new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        _unityStatus = runtime.UnityStatus;
        Screens = new ObservableCollection<ScreenItemViewModel>(_configuration.Screens.Select(screen => new ScreenItemViewModel(screen)));
        SelectedScreen = Screens.FirstOrDefault();

        AddSensorCommand = CreateCommand(AddSensorAsync, () => CanEditSelectedScreen);
        DeleteSensorCommand = CreateCommand(DeleteSensorAsync, () => CanEditSelectedScreen && SelectedSensor is not null && SelectedScreen!.Sensors.Count > 1);
        DeleteOrphanedScreenConfigurationCommand = CreateCommand(DeleteOrphanedScreenConfigurationAsync, () => SelectedScreen is { IsAssociated: false });
        RestoreUnityResolutionCommand = new RelayCommand(() => { if (SelectedScreen is not null) SelectedScreen.ResolutionMode = RadarResolutionMode.FollowUnityDefault; }, () => SelectedScreen is not null);
        ConnectSensorCommand = CreateCommand(token => WithSelectedSensorAsync((screen, sensor) => _runtime.ConnectSensorAsync(screen.ScreenId, sensor.SensorId, token)), CanOperateSelectedSensor);
        DisconnectSensorCommand = CreateCommand(_ => WithSelectedSensorAsync((screen, sensor) => _runtime.DisconnectSensorAsync(screen.ScreenId, sensor.SensorId)), CanOperateSelectedSensor);
        ConnectScreenCommand = CreateCommand(token => SelectedScreen is null || !SelectedScreen.IsAssociated ? Task.CompletedTask : _runtime.ConnectScreenAsync(SelectedScreen.ScreenId, token), () => SelectedScreen is { IsAssociated: true });
        DisconnectScreenCommand = CreateCommand(_ => SelectedScreen is null || !SelectedScreen.IsAssociated ? Task.CompletedTask : _runtime.DisconnectScreenAsync(SelectedScreen.ScreenId), () => SelectedScreen is { IsAssociated: true });
        ConnectAllCommand = CreateCommand(token => _runtime.ConnectAllAsync(token));
        DisconnectAllCommand = CreateCommand(_ => _runtime.DisconnectAllAsync());
        StartAllSimulationCommand = CreateCommand(token => _runtime.StartAllSimulationAsync(token));
        StartReplayCommand = CreateCommand(token => WithSelectedReplaySensorAsync((screen, sensor) => _runtime.ReplaySensorAsync(screen.ScreenId, sensor.SensorId, sensor.ReplayFilePath, sensor.ReplaySpeed, sensor.ReplayLoop, token)));
        PauseReplayCommand = new RelayCommand(() => WithSelectedReplaySensor((screen, sensor) => _runtime.PauseReplay(screen.ScreenId, sensor.SensorId)), HasSelectedReplaySensor);
        ResumeReplayCommand = new RelayCommand(() => WithSelectedReplaySensor((screen, sensor) => _runtime.ResumeReplay(screen.ScreenId, sensor.SensorId)), HasSelectedReplaySensor);
        StepReplayCommand = new RelayCommand(() => WithSelectedReplaySensor((screen, sensor) => _runtime.StepReplay(screen.ScreenId, sensor.SensorId)), HasSelectedReplaySensor);
        StopReplayCommand = CreateCommand(_ => WithSelectedReplaySensorAsync((screen, sensor) => _runtime.StopReplayAsync(screen.ScreenId, sensor.SensorId)), HasSelectedReplaySensor);
        SaveConfigurationCommand = CreateCommand(SaveConfigurationAsync, () => _configuration.CanPersist);
        ConnectCommand = ConnectSensorCommand;
        DisconnectCommand = DisconnectSensorCommand;
        StartSimulationCommand = StartAllSimulationCommand;
        StopSimulationCommand = DisconnectAllCommand;
        ResetRegionCommand = new RelayCommand(ResetSelectedRegion, () => SelectedSensor is not null);
        BeginCalibrationCommand = new RelayCommand(() => SelectedSensor?.BeginCalibration(), () => SelectedSensor is not null);
        CaptureCalibrationPointCommand = new RelayCommand(() => SelectedSensor?.CaptureCurrentTargetForCalibration(), () => SelectedSensor?.HasMatchedPhysicalTarget == true);
        UndoCalibrationPointCommand = new RelayCommand(() => SelectedSensor?.UndoCalibrationPoint(), () => SelectedSensor is not null);
        SaveCalibrationCommand = new RelayCommand(() => SelectedSensor?.SaveCalibration(), () => SelectedSensor is not null);
        ClearCalibrationCommand = new RelayCommand(() => SelectedSensor?.ClearCalibration(), () => SelectedSensor is not null);
        AddMaskedRegionCommand = new RelayCommand(() => SelectedSensor?.AddMaskedRegionAtCurrentTarget(), () => SelectedSensor?.HasMatchedPhysicalTarget == true);
        DeleteMaskedRegionCommand = new RelayCommand(DeleteSelectedMaskedRegion, () => SelectedSensor?.Configuration.Range.MaskedPolygons.Count > 0);

        _runtime.SensorSnapshotUpdated += OnSensorSnapshotUpdated;
        _runtime.ScreenSnapshotUpdated += OnScreenSnapshotUpdated;
        _runtime.SensorStateChanged += OnSensorStateChanged;
        _runtime.LogReceived += OnLogReceived;
        _runtime.UnityStatusChanged += OnUnityStatusChanged;
    }

    public ObservableCollection<ScreenItemViewModel> Screens { get; }
    public ObservableCollection<string> VisibleLogEntries { get; } = [];
    public ScreenItemViewModel? SelectedScreen
    {
        get => _selectedScreen;
        set
        {
            if (!SetProperty(ref _selectedScreen, value)) return;
            SelectedSensor = value?.Sensors.FirstOrDefault();
            NotifyCommandState();
        }
    }
    public SensorItemViewModel? SelectedSensor
    {
        get => _selectedSensor;
        set
        {
            var owned = value is null || SelectedScreen?.Sensors.Contains(value) == true ? value : null;
            if (ReferenceEquals(_selectedSensor, owned)) return;
            if (_selectedSensor is not null) _selectedSensor.PropertyChanged -= OnSelectedSensorPropertyChanged;
            if (!SetProperty(ref _selectedSensor, owned)) return;
            if (_selectedSensor is not null) _selectedSensor.PropertyChanged += OnSelectedSensorPropertyChanged;
            OnPropertyChanged(string.Empty);
            NotifyCommandState();
        }
    }
    public string SelectedLogScreenId { get => _selectedLogScreenId; set { if (SetProperty(ref _selectedLogScreenId, NormalizeFilter(value))) RebuildVisibleLogs(); } }
    public string SelectedLogSensorId { get => _selectedLogSensorId; set { if (SetProperty(ref _selectedLogSensorId, NormalizeFilter(value))) RebuildVisibleLogs(); } }
    public UnityClientStatus UnityStatus { get => _unityStatus; private set => SetProperty(ref _unityStatus, value); }
    public bool CanEditSelectedScreen => SelectedScreen is { IsAssociated: false };

    // Temporary compatibility surface for MainWindow.xaml. It maps only the selected item and must be removed when Task 7 rewrites the XAML; do not persist or reintroduce root flat configuration here.
    public IReadOnlyList<RadarModel> AvailableModels { get; } = [RadarModel.F10, RadarModel.F20];
    public IReadOnlyList<RadarInteractionMode> AvailableInteractionModes { get; } = Enum.GetValues<RadarInteractionMode>();
    public IReadOnlyList<string> AvailableLocalIps { get; } = [string.Empty];
    public RadarModel SelectedModel { get => SelectedSensor?.DeviceModel ?? RadarModel.F10; set { if (SelectedSensor is not null) SelectedSensor.DeviceModel = value; } }
    public string ConnectionStatus => SelectedSensor?.RuntimeState.ToString() ?? "No sensor selected";
    public string UnityConnectionText => UnityStatus.IsConnected ? "Unity connected" : "Unity disconnected";
    public string UnityResolution => SelectedScreen?.EffectiveResolutionText ?? "—";
    public string ModelDisplayName => RadarModelProfileFactory.Create(SelectedModel).DisplayName;
    public float ModelMaximumDistanceMeters => RadarModelProfileFactory.Create(SelectedModel).MaximumDistanceMeters;
    public string ScanFrequencyDescription => $"{RadarModelProfileFactory.Create(SelectedModel).MinimumScanFrequencyHz}-{RadarModelProfileFactory.Create(SelectedModel).MaximumScanFrequencyHz} Hz";
    public string AngularResolutionDescription => $"{RadarModelProfileFactory.Create(SelectedModel).DefaultAngularResolutionDegrees:0.##}°";
    public string RadarIp { get => SelectedSensor?.RadarIp ?? string.Empty; set { if (SelectedSensor is not null) SelectedSensor.RadarIp = value; } }
    public int Port { get => SelectedSensor?.Port ?? 0; set { if (SelectedSensor is not null) SelectedSensor.Port = value; } }
    public string LocalIp { get => SelectedSensor?.LocalIp ?? string.Empty; set { if (SelectedSensor is not null) SelectedSensor.LocalIp = value; } }
    public bool AutoReconnect { get => SelectedSensor?.AutoReconnect ?? false; set { if (SelectedSensor is not null) SelectedSensor.AutoReconnect = value; } }
    public float MinimumDistanceMeters { get => SelectedSensor?.MinimumDistanceMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.MinimumDistanceMeters = value; } }
    public float MaximumDistanceMeters { get => SelectedSensor?.MaximumDistanceMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.MaximumDistanceMeters = value; } }
    public float VisualizationRangeMeters { get => SelectedSensor?.VisualizationRangeMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.VisualizationRangeMeters = value; } }
    public float RotationDegrees { get => SelectedSensor?.RotationDegrees ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.RotationDegrees = value; } }
    public bool FlipX { get => SelectedSensor?.FlipX ?? false; set { if (SelectedSensor is not null) SelectedSensor.FlipX = value; } }
    public bool FlipY { get => SelectedSensor?.FlipY ?? false; set { if (SelectedSensor is not null) SelectedSensor.FlipY = value; } }
    public float OffsetXMeters { get => SelectedSensor?.OffsetXMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.OffsetXMeters = value; } }
    public float OffsetYMeters { get => SelectedSensor?.OffsetYMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.OffsetYMeters = value; } }
    public RadarInteractionMode InteractionMode { get => SelectedScreen?.InteractionMode ?? RadarInteractionMode.Touch; set { if (SelectedScreen is not null) SelectedScreen.InteractionMode = value; } }
    public int DwellMilliseconds { get => SelectedScreen?.DwellMilliseconds ?? 0; set { if (SelectedScreen is not null) SelectedScreen.DwellMilliseconds = value; } }
    public float BaseGapMeters { get => SelectedSensor?.BaseGapMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.BaseGapMeters = value; } }
    public float DistanceScale { get => SelectedSensor?.DistanceScale ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.DistanceScale = value; } }
    public float MinimumAngleDegrees { get => SelectedSensor?.MinimumAngleDegrees ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.MinimumAngleDegrees = value; } }
    public float MaximumAngleDegrees { get => SelectedSensor?.MaximumAngleDegrees ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.MaximumAngleDegrees = value; } }
    public float LeftEdgeDeadZoneMeters { get => SelectedSensor?.Configuration.Range.EdgeDeadZones.LeftMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.Configuration.Range.EdgeDeadZones.LeftMeters = value; } }
    public float RightEdgeDeadZoneMeters { get => SelectedSensor?.Configuration.Range.EdgeDeadZones.RightMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.Configuration.Range.EdgeDeadZones.RightMeters = value; } }
    public float TopEdgeDeadZoneMeters { get => SelectedSensor?.Configuration.Range.EdgeDeadZones.TopMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.Configuration.Range.EdgeDeadZones.TopMeters = value; } }
    public float BottomEdgeDeadZoneMeters { get => SelectedSensor?.Configuration.Range.EdgeDeadZones.BottomMeters ?? 0f; set { if (SelectedSensor is not null) SelectedSensor.Configuration.Range.EdgeDeadZones.BottomMeters = value; } }
    public string CalibrationStatus => SelectedSensor?.CalibrationStatus ?? "Not calibrated";
    public string CalibrationStep => SelectedSensor?.CalibrationStep ?? "Not started";
    public long LastFrameSequence => SelectedSensor?.Snapshot?.Sequence ?? 0;
    public RadarSensorRuntimeSnapshot? LatestSnapshot => SelectedSensor?.Snapshot;
    public string ActualScanFrequency => SelectedSensor?.FrequencyText ?? "0.0 Hz";
    public string ReceiveRate => $"{SelectedSensor?.Snapshot?.ReceivedBytesPerSecond ?? 0d:0} B/s";
    public int RawPointCount => SelectedSensor?.Snapshot?.RawPoints.Count ?? 0;
    public int ValidPointCount => SelectedSensor?.Snapshot?.ValidPoints.Count ?? 0;
    public int TargetCount => SelectedScreen?.FusedTargetCount ?? 0;
    public long CrcErrorCount => SelectedSensor?.CrcErrorCount ?? 0;
    public long DiscardedByteCount => SelectedSensor?.Snapshot?.DiscardedByteCount ?? 0;
    public ObservableCollection<string> LogEntries => VisibleLogEntries;
    public ObservableCollection<Point2> RegionVertices => SelectedSensor?.RegionVertices ?? [];
    public int MaskedRegionCount => SelectedSensor?.Configuration.Range.MaskedPolygons.Count ?? 0;
    public IReadOnlyList<IReadOnlyList<RadarPoint2>> MaskedRegions => SelectedSensor?.MaskedPolygons ?? [];

    public ICommand AddSensorCommand { get; }
    public ICommand DeleteSensorCommand { get; }
    public ICommand DeleteOrphanedScreenConfigurationCommand { get; }
    public ICommand RestoreUnityResolutionCommand { get; }
    public ICommand ConnectSensorCommand { get; }
    public ICommand DisconnectSensorCommand { get; }
    public ICommand ConnectScreenCommand { get; }
    public ICommand DisconnectScreenCommand { get; }
    public ICommand ConnectAllCommand { get; }
    public ICommand DisconnectAllCommand { get; }
    public ICommand StartAllSimulationCommand { get; }
    public ICommand StartReplayCommand { get; }
    public ICommand PauseReplayCommand { get; }
    public ICommand ResumeReplayCommand { get; }
    public ICommand StepReplayCommand { get; }
    public ICommand StopReplayCommand { get; }
    public ICommand SaveConfigurationCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand StartSimulationCommand { get; }
    public ICommand StopSimulationCommand { get; }
    public ICommand ResetRegionCommand { get; }
    public ICommand BeginCalibrationCommand { get; }
    public ICommand CaptureCalibrationPointCommand { get; }
    public ICommand UndoCalibrationPointCommand { get; }
    public ICommand SaveCalibrationCommand { get; }
    public ICommand ClearCalibrationCommand { get; }
    public ICommand AddMaskedRegionCommand { get; }
    public ICommand DeleteMaskedRegionCommand { get; }

    public Task StartRecordingAsync(string path, CancellationToken cancellationToken = default) => WithSelectedSensorAsync((screen, sensor) => _runtime.StartRecordingAsync(screen.ScreenId, sensor.SensorId, path, cancellationToken));
    public Task StopRecordingAsync() => WithSelectedSensorAsync((screen, sensor) => _runtime.StopRecordingAsync(screen.ScreenId, sensor.SensorId));
    public Task ReplaySelectedSensorAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default)
    {
        if (!HasSelectedReplaySensor()) return Task.CompletedTask;
        SelectedSensor!.ReplayFilePath = path; SelectedSensor.ReplaySpeed = speed; SelectedSensor.ReplayLoop = loop;
        return WithSelectedReplaySensorAsync((screen, sensor) => _runtime.ReplaySensorAsync(screen.ScreenId, sensor.SensorId, path, speed, loop, cancellationToken));
    }
    public void PauseSelectedReplay() => WithSelectedReplaySensor((screen, sensor) => _runtime.PauseReplay(screen.ScreenId, sensor.SensorId));
    public void ResumeSelectedReplay() => WithSelectedReplaySensor((screen, sensor) => _runtime.ResumeReplay(screen.ScreenId, sensor.SensorId));
    public void StepSelectedReplay() => WithSelectedReplaySensor((screen, sensor) => _runtime.StepReplay(screen.ScreenId, sensor.SensorId));
    public Task StopSelectedReplayAsync() => WithSelectedReplaySensorAsync((screen, sensor) => _runtime.StopReplayAsync(screen.ScreenId, sensor.SensorId));
    public void UpdateRegionVertex(int index, Point2 value)
    {
        if (SelectedSensor is null || index < 0 || index >= SelectedSensor.Configuration.Range.ActivePolygon.Count) return;
        SelectedSensor.UpdateRegionVertex(index, value);
    }

    private void ResetSelectedRegion()
    {
        if (SelectedSensor is null) return;
        SelectedSensor.ResetRegion();
    }
    private void AddSelectedMaskedRegion()
    {
        SelectedSensor?.AddMaskedRegionAtCurrentTarget();
    }
    private void DeleteSelectedMaskedRegion()
    {
        SelectedSensor?.DeleteLastMaskedRegion();
    }
    private void OnSelectedSensorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { OnPropertyChanged(string.Empty); NotifyCommandState(); }
    public void ReceiveLogForTest(string entry) => ReceiveLog(entry);

    private async Task AddSensorAsync(CancellationToken _)
    {
        if (SelectedScreen is null) return;
        var ids = new HashSet<string>(SelectedScreen.Sensors.Select(sensor => sensor.SensorId), StringComparer.OrdinalIgnoreCase);
        var next = 1;
        while (!ids.Add($"sensor-{next}")) next++;
        SelectedSensor = SelectedScreen.AddSensor(new RadarSensorConfiguration { SensorId = $"sensor-{next}", DisplayName = $"Radar {next}", OutputRectPixels = new RadarPixelRect(0, 0, SelectedScreen.EffectiveWidthPixels, SelectedScreen.EffectiveHeightPixels) });
        await Task.CompletedTask;
    }

    private Task DeleteSensorAsync(CancellationToken _)
    {
        if (SelectedScreen is null || SelectedSensor is null || SelectedScreen.Sensors.Count <= 1) return Task.CompletedTask;
        var sensor = SelectedSensor;
        SelectedScreen.RemoveSensor(sensor);
        SelectedSensor = SelectedScreen.Sensors.FirstOrDefault();
        return Task.CompletedTask;
    }

    private Task DeleteOrphanedScreenConfigurationAsync(CancellationToken _)
    {
        if (SelectedScreen is { IsAssociated: false } screen)
        {
            _configuration.Screens.Remove(screen.Configuration);
            Screens.Remove(screen);
            SelectedScreen = Screens.FirstOrDefault();
        }
        return Task.CompletedTask;
    }

    private async Task SaveConfigurationAsync(CancellationToken token)
    {
        if (!_configuration.CanPersist) { AddLog("Configuration save skipped: rejected load state."); return; }
        var validation = ConfigurationValidator.ValidateAndNormalize(_configuration);
        if (!validation.IsValid) { AddLog($"Configuration validation failed: {string.Join(" | ", validation.Errors)}"); return; }
        await _runtime.ApplyConfigurationAsync(token).ConfigureAwait(true);
        RebuildItemsAfterApply();
        AddLog("Configuration applied.");
    }

    private AsyncRelayCommand CreateCommand(Func<CancellationToken, Task> action, Func<bool>? canExecute = null)
    {
        var command = new AsyncRelayCommand(() => action(CancellationToken.None), canExecute);
        command.ExecutionFailed += exception => AddLog($"Operation failed: {exception.Message}");
        return command;
    }

    private Task WithSelectedSensorAsync(Func<ScreenItemViewModel, SensorItemViewModel, Task> action) => CanOperateSelectedSensor() ? action(SelectedScreen!, SelectedSensor!) : Task.CompletedTask;
    private Task WithSelectedReplaySensorAsync(Func<ScreenItemViewModel, SensorItemViewModel, Task> action) => HasSelectedReplaySensor() ? action(SelectedScreen!, SelectedSensor!) : Task.CompletedTask;
    private void WithSelectedReplaySensor(Action<ScreenItemViewModel, SensorItemViewModel> action) { if (HasSelectedReplaySensor()) action(SelectedScreen!, SelectedSensor!); }
    private bool HasSelectedReplaySensor() => SelectedScreen is not null && SelectedSensor?.SourceMode == RadarSensorSourceMode.Replay;
    private bool CanOperateSelectedSensor() => SelectedScreen is { IsAssociated: true } && SelectedSensor is not null && SelectedScreen.Sensors.Contains(SelectedSensor);

    private void OnSensorSnapshotUpdated(RadarSensorRuntimeSnapshot snapshot) => Dispatch(() =>
    {
        var sensor = FindSensor(snapshot.ScreenId, snapshot.SensorId);
        sensor?.ApplySnapshot(snapshot);
        FindScreen(snapshot.ScreenId)?.NotifySensorChanges();
    });
    private void OnScreenSnapshotUpdated(RadarScreenRuntimeSnapshot snapshot) => Dispatch(() => FindScreen(snapshot.Screen.ScreenId)?.ApplyFusedTargetCount(snapshot.Targets.Count));
    private void OnSensorStateChanged(RadarSensorRuntimeStateChanged state) => Dispatch(() => FindSensor(state.ScreenId, state.SensorId)?.ApplyRuntimeState(state.State));
    private void OnLogReceived(string entry) => Dispatch(() => ReceiveLog(entry));
    private void OnUnityStatusChanged(UnityClientStatus status) => Dispatch(() => UnityStatus = status);
    private ScreenItemViewModel? FindScreen(string id) => Screens.FirstOrDefault(screen => string.Equals(screen.ScreenId, id, StringComparison.OrdinalIgnoreCase));
    private SensorItemViewModel? FindSensor(string screenId, string sensorId) => FindScreen(screenId)?.Sensors.FirstOrDefault(sensor => string.Equals(sensor.SensorId, sensorId, StringComparison.OrdinalIgnoreCase));

    private void ReceiveLog(string entry)
    {
        if (IsThrottledMove(entry)) return;
        _rawLogs.Enqueue(entry);
        while (_rawLogs.Count > MaximumLogEntries) _rawLogs.Dequeue();
        if (MatchesFilter(entry))
        {
            VisibleLogEntries.Add(entry);
            while (VisibleLogEntries.Count > MaximumLogEntries) VisibleLogEntries.RemoveAt(0);
        }
    }

    private void AddLog(string entry) => ReceiveLog(entry);
    private void RebuildVisibleLogs()
    {
        VisibleLogEntries.Clear();
        foreach (var entry in _rawLogs.Where(MatchesFilter).TakeLast(MaximumLogEntries)) VisibleLogEntries.Add(entry);
    }
    private bool MatchesFilter(string entry)
    {
        if (_selectedLogScreenId == "*" && _selectedLogSensorId == "*") return true;
        var tag = $"[{_selectedLogScreenId}/{_selectedLogSensorId}]";
        if (_selectedLogScreenId != "*" && _selectedLogSensorId == "*") return entry.StartsWith($"[{_selectedLogScreenId}/", StringComparison.OrdinalIgnoreCase);
        if (_selectedLogScreenId == "*" && _selectedLogSensorId != "*") return entry.Contains($"/{_selectedLogSensorId}]", StringComparison.OrdinalIgnoreCase);
        return entry.StartsWith(tag, StringComparison.OrdinalIgnoreCase);
    }
    private bool IsThrottledMove(string entry)
    {
        if (!entry.Contains("move", StringComparison.OrdinalIgnoreCase) || entry.Contains("error", StringComparison.OrdinalIgnoreCase) || entry.Contains("warning", StringComparison.OrdinalIgnoreCase)) return false;
        var match = System.Text.RegularExpressions.Regex.Match(entry, @"^\[([a-z0-9_-]+)/P([a-z0-9_-]+)\].*\bMove\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        var key = $"{match.Groups[1].Value}/{match.Groups[2].Value}";
        var now = DateTimeOffset.UtcNow;
        if (_lastMoveLogAt.TryGetValue(key, out var last) && now - last < TimeSpan.FromMilliseconds(100)) return true;
        _lastMoveLogAt[key] = now;
        return false;
    }
    private static string NormalizeFilter(string? value) => string.IsNullOrWhiteSpace(value) ? "*" : value.Trim();
    private void Dispatch(Action action) { if (_disposed) return; if (SynchronizationContext.Current == _uiContext) action(); else _uiContext.Post(_ => { if (!_disposed) action(); }, null); }
    private void NotifyCommandState()
    {
        foreach (var command in new[] { AddSensorCommand, DeleteSensorCommand, DeleteOrphanedScreenConfigurationCommand, RestoreUnityResolutionCommand, ConnectSensorCommand, DisconnectSensorCommand, ConnectScreenCommand, DisconnectScreenCommand, StartReplayCommand, PauseReplayCommand, ResumeReplayCommand, StepReplayCommand, StopReplayCommand, SaveConfigurationCommand, ResetRegionCommand, BeginCalibrationCommand, CaptureCalibrationPointCommand, UndoCalibrationPointCommand, SaveCalibrationCommand, ClearCalibrationCommand, AddMaskedRegionCommand, DeleteMaskedRegionCommand })
            if (command is RelayCommand relay) relay.NotifyCanExecuteChanged(); else if (command is AsyncRelayCommand asyncRelay) asyncRelay.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditSelectedScreen));
    }
    private void RebuildItemsAfterApply()
    {
        var screenId = SelectedScreen?.ScreenId;
        var sensorId = SelectedSensor?.SensorId;
        Screens.Clear();
        foreach (var screen in _configuration.Screens) Screens.Add(new ScreenItemViewModel(screen));
        SelectedScreen = Screens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.OrdinalIgnoreCase)) ?? Screens.FirstOrDefault();
        SelectedSensor = SelectedScreen?.Sensors.FirstOrDefault(sensor => string.Equals(sensor.SensorId, sensorId, StringComparison.OrdinalIgnoreCase)) ?? SelectedScreen?.Sensors.FirstOrDefault();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.SensorSnapshotUpdated -= OnSensorSnapshotUpdated;
        _runtime.ScreenSnapshotUpdated -= OnScreenSnapshotUpdated;
        _runtime.SensorStateChanged -= OnSensorStateChanged;
        _runtime.LogReceived -= OnLogReceived;
        _runtime.UnityStatusChanged -= OnUnityStatusChanged;
        if (_selectedSensor is not null) _selectedSensor.PropertyChanged -= OnSelectedSensorPropertyChanged;
    }
}
