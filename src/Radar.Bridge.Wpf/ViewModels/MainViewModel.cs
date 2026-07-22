using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.ViewModels;

/// <summary>Coordinates selection and user commands; configuration remains owned by screen and sensor items.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int MaximumLogEntries = 500;
    private readonly RadarAppConfiguration _configuration;
    private readonly IRadarBridgeRuntime _runtime;
    private readonly IFileDialogService _fileDialogs;
    private readonly SynchronizationContext _uiContext;
    private readonly Queue<string> _rawLogs = [];
    private readonly Dictionary<string, DateTimeOffset> _lastMoveLogAt = new(StringComparer.OrdinalIgnoreCase);
    private ScreenItemViewModel? _selectedScreen;
    private SensorItemViewModel? _selectedSensor;
    private string _selectedLogScreenId = "*";
    private string _selectedLogSensorId = "*";
    private UnityClientStatus _unityStatus;
    private bool _disposed;

    public MainViewModel(RadarAppConfiguration configuration, IRadarBridgeRuntime runtime, IFileDialogService? fileDialogs = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _fileDialogs = fileDialogs ?? new WpfFileDialogService();
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
        ConnectScreenCommand = CreateCommand(token => SelectedScreen is { IsAssociated: true } screen ? _runtime.ConnectScreenAsync(screen.ScreenId, token) : Task.CompletedTask, () => SelectedScreen is { IsAssociated: true });
        DisconnectScreenCommand = CreateCommand(_ => SelectedScreen is { IsAssociated: true } screen ? _runtime.DisconnectScreenAsync(screen.ScreenId) : Task.CompletedTask, () => SelectedScreen is { IsAssociated: true });
        ConnectAllCommand = CreateCommand(token => _runtime.ConnectAllAsync(token));
        DisconnectAllCommand = CreateCommand(_ => _runtime.DisconnectAllAsync());
        StartAllSimulationCommand = CreateCommand(token => _runtime.StartAllSimulationAsync(token));
        StartReplayCommand = CreateCommand(token => WithSelectedReplaySensorAsync((screen, sensor) => _runtime.ReplaySensorAsync(screen.ScreenId, sensor.SensorId, sensor.ReplayFilePath, sensor.ReplaySpeed, sensor.ReplayLoop, token)));
        PauseReplayCommand = new RelayCommand(() => WithSelectedReplaySensor((screen, sensor) => _runtime.PauseReplay(screen.ScreenId, sensor.SensorId)), HasSelectedReplaySensor);
        ResumeReplayCommand = new RelayCommand(() => WithSelectedReplaySensor((screen, sensor) => _runtime.ResumeReplay(screen.ScreenId, sensor.SensorId)), HasSelectedReplaySensor);
        StepReplayCommand = new RelayCommand(() => WithSelectedReplaySensor((screen, sensor) => _runtime.StepReplay(screen.ScreenId, sensor.SensorId)), HasSelectedReplaySensor);
        StopReplayCommand = CreateCommand(_ => WithSelectedReplaySensorAsync((screen, sensor) => _runtime.StopReplayAsync(screen.ScreenId, sensor.SensorId)), HasSelectedReplaySensor);
        SaveConfigurationCommand = CreateCommand(SaveConfigurationAsync, () => _configuration.CanPersist);
        StartRecordingCommand = CreateCommand(StartRecordingFromDialogAsync, CanOperateSelectedSensor);
        StopRecordingCommand = CreateCommand(_ => StopRecordingAsync(), CanOperateSelectedSensor);
        SelectReplayFileCommand = CreateCommand(ReplayFromDialogAsync, HasSelectedReplaySensor);
        ResetRegionCommand = new RelayCommand(() => SelectedSensor?.ResetRegion(), () => SelectedSensor is not null);
        BeginCalibrationCommand = new RelayCommand(() => SelectedSensor?.BeginCalibration(), () => SelectedSensor is not null);
        CaptureCalibrationPointCommand = new RelayCommand(() => SelectedSensor?.CaptureCurrentTargetForCalibration(), () => SelectedSensor?.HasMatchedPhysicalTarget == true);
        UndoCalibrationPointCommand = new RelayCommand(() => SelectedSensor?.UndoCalibrationPoint(), () => SelectedSensor is not null);
        SaveCalibrationCommand = new RelayCommand(() => SelectedSensor?.SaveCalibration(), () => SelectedSensor is not null);
        ClearCalibrationCommand = new RelayCommand(() => SelectedSensor?.ClearCalibration(), () => SelectedSensor is not null);
        AddMaskedRegionCommand = new RelayCommand(() => SelectedSensor?.AddMaskedRegionAtCurrentTarget(), () => SelectedSensor?.HasMatchedPhysicalTarget == true);
        DeleteMaskedRegionCommand = new RelayCommand(() => SelectedSensor?.DeleteLastMaskedRegion(), () => SelectedSensor?.Configuration.Range.MaskedPolygons.Count > 0);

        _runtime.SensorSnapshotUpdated += OnSensorSnapshotUpdated;
        _runtime.ScreenSnapshotUpdated += OnScreenSnapshotUpdated;
        _runtime.SensorStateChanged += OnSensorStateChanged;
        _runtime.LogReceived += OnLogReceived;
        _runtime.UnityStatusChanged += OnUnityStatusChanged;
    }

    public ObservableCollection<ScreenItemViewModel> Screens { get; }
    public ObservableCollection<string> VisibleLogEntries { get; } = [];
    public UnityClientStatus UnityStatus { get => _unityStatus; private set => SetProperty(ref _unityStatus, value); }
    public bool CanEditSelectedScreen => SelectedScreen is { IsAssociated: false };

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

    public string SelectedLogScreenId
    {
        get => _selectedLogScreenId;
        set { if (SetProperty(ref _selectedLogScreenId, NormalizeFilter(value))) RebuildVisibleLogs(); }
    }

    public string SelectedLogSensorId
    {
        get => _selectedLogSensorId;
        set { if (SetProperty(ref _selectedLogSensorId, NormalizeFilter(value))) RebuildVisibleLogs(); }
    }

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
    public ICommand StartRecordingCommand { get; }
    public ICommand StopRecordingCommand { get; }
    public ICommand SelectReplayFileCommand { get; }
    public ICommand ResetRegionCommand { get; }
    public ICommand BeginCalibrationCommand { get; }
    public ICommand CaptureCalibrationPointCommand { get; }
    public ICommand UndoCalibrationPointCommand { get; }
    public ICommand SaveCalibrationCommand { get; }
    public ICommand ClearCalibrationCommand { get; }
    public ICommand AddMaskedRegionCommand { get; }
    public ICommand DeleteMaskedRegionCommand { get; }

    public Task StartRecordingAsync(string path, CancellationToken cancellationToken = default) =>
        WithSelectedSensorAsync((screen, sensor) => _runtime.StartRecordingAsync(screen.ScreenId, sensor.SensorId, path, cancellationToken));
    public Task StopRecordingAsync() => WithSelectedSensorAsync((screen, sensor) => _runtime.StopRecordingAsync(screen.ScreenId, sensor.SensorId));
    public Task ReplaySelectedSensorAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default)
    {
        if (!HasSelectedReplaySensor()) return Task.CompletedTask;
        SelectedSensor!.ReplayFilePath = path;
        SelectedSensor.ReplaySpeed = speed;
        SelectedSensor.ReplayLoop = loop;
        return WithSelectedReplaySensorAsync((screen, sensor) => _runtime.ReplaySensorAsync(screen.ScreenId, sensor.SensorId, path, speed, loop, cancellationToken));
    }
    public void PauseSelectedReplay() => WithSelectedReplaySensor((screen, sensor) => _runtime.PauseReplay(screen.ScreenId, sensor.SensorId));
    public void ResumeSelectedReplay() => WithSelectedReplaySensor((screen, sensor) => _runtime.ResumeReplay(screen.ScreenId, sensor.SensorId));
    public void StepSelectedReplay() => WithSelectedReplaySensor((screen, sensor) => _runtime.StepReplay(screen.ScreenId, sensor.SensorId));
    public Task StopSelectedReplayAsync() => WithSelectedReplaySensorAsync((screen, sensor) => _runtime.StopReplayAsync(screen.ScreenId, sensor.SensorId));
    public void UpdateRegionVertex(int index, Point2 value)
    {
        if (SelectedSensor is null || index < 0 || index >= SelectedSensor.RegionVertices.Count) return;
        SelectedSensor.UpdateRegionVertex(index, value);
    }
    public void ReceiveLogForTest(string entry) => ReceiveLog(entry);

    private Task StartRecordingFromDialogAsync(CancellationToken token)
    {
        var path = _fileDialogs.SelectRecordingPath();
        return string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : StartRecordingAsync(path, token);
    }
    private Task ReplayFromDialogAsync(CancellationToken token)
    {
        var path = _fileDialogs.SelectReplayPath();
        return string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : ReplaySelectedSensorAsync(path, 1d, false, token);
    }

    private AsyncRelayCommand CreateCommand(Func<CancellationToken, Task> action, Func<bool>? canExecute = null)
    {
        var command = new AsyncRelayCommand(() => action(CancellationToken.None), canExecute);
        command.ExecutionFailed += exception => ReceiveLog($"{CurrentLogTag()} Operation failed: {exception.Message}");
        return command;
    }

    private async Task AddSensorAsync(CancellationToken _)
    {
        if (SelectedScreen is null) return;
        var ids = new HashSet<string>(SelectedScreen.Sensors.Select(sensor => sensor.SensorId), StringComparer.OrdinalIgnoreCase);
        var next = 1;
        while (!ids.Add($"sensor-{next}")) next++;
        SelectedSensor = SelectedScreen.AddSensor(new RadarSensorConfiguration
        {
            SensorId = $"sensor-{next}",
            DisplayName = $"Radar {next}",
            OutputRectPixels = new RadarPixelRect(0, 0, SelectedScreen.EffectiveWidthPixels, SelectedScreen.EffectiveHeightPixels)
        });
        await Task.CompletedTask;
    }

    private Task DeleteSensorAsync(CancellationToken _)
    {
        if (SelectedScreen is null || SelectedSensor is null || SelectedScreen.Sensors.Count <= 1) return Task.CompletedTask;
        SelectedScreen.RemoveSensor(SelectedSensor);
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
        if (!_configuration.CanPersist) { ReceiveLog("[SYSTEM] Configuration save skipped: rejected load state."); return; }
        var validation = ConfigurationValidator.ValidateAndNormalize(_configuration);
        if (!validation.IsValid) { ReceiveLog("[SYSTEM] Configuration validation failed: " + string.Join(" | ", validation.Errors)); return; }
        await _runtime.ApplyConfigurationAsync(token).ConfigureAwait(true);
        ReceiveLog(SelectedScreen is null ? "[SYSTEM] Configuration applied." : $"[{SelectedScreen.ScreenId}/FUSION] Configuration applied.");
    }

    private bool CanOperateSelectedSensor() => SelectedScreen is { IsAssociated: true } && SelectedSensor is not null && SelectedScreen.Sensors.Contains(SelectedSensor);
    private bool HasSelectedReplaySensor() => CanOperateSelectedSensor() && SelectedSensor?.SourceMode == RadarSensorSourceMode.Replay;
    private Task WithSelectedSensorAsync(Func<ScreenItemViewModel, SensorItemViewModel, Task> action) => CanOperateSelectedSensor() ? action(SelectedScreen!, SelectedSensor!) : Task.CompletedTask;
    private Task WithSelectedReplaySensorAsync(Func<ScreenItemViewModel, SensorItemViewModel, Task> action) => HasSelectedReplaySensor() ? action(SelectedScreen!, SelectedSensor!) : Task.CompletedTask;
    private void WithSelectedReplaySensor(Action<ScreenItemViewModel, SensorItemViewModel> action) { if (HasSelectedReplaySensor()) action(SelectedScreen!, SelectedSensor!); }

    private void OnSensorSnapshotUpdated(RadarSensorRuntimeSnapshot snapshot) => Dispatch(() =>
    {
        FindScreen(snapshot.ScreenId)?.Sensors.FirstOrDefault(sensor => string.Equals(sensor.SensorId, snapshot.SensorId, StringComparison.OrdinalIgnoreCase))?.ApplySnapshot(snapshot);
    });
    private void OnScreenSnapshotUpdated(RadarScreenRuntimeSnapshot snapshot) => Dispatch(() => FindScreen(snapshot.Screen.ScreenId)?.ApplySnapshot(snapshot));
    private void OnSensorStateChanged(RadarSensorRuntimeStateChanged state) => Dispatch(() => FindScreen(state.ScreenId)?.Sensors.FirstOrDefault(sensor => string.Equals(sensor.SensorId, state.SensorId, StringComparison.OrdinalIgnoreCase))?.ApplyRuntimeState(state.State));
    private void OnLogReceived(string entry) => Dispatch(() => ReceiveLog(entry));
    private void OnUnityStatusChanged(UnityClientStatus status) => Dispatch(() => UnityStatus = status);
    private ScreenItemViewModel? FindScreen(string screenId) => Screens.FirstOrDefault(screen => string.Equals(screen.ScreenId, screenId, StringComparison.OrdinalIgnoreCase));
    private void OnSelectedSensorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { OnPropertyChanged(string.Empty); NotifyCommandState(); }

    private void ReceiveLog(string entry)
    {
        if (IsThrottledMove(entry)) return;
        _rawLogs.Enqueue(entry);
        while (_rawLogs.Count > MaximumLogEntries) _rawLogs.Dequeue();
        if (!MatchesFilter(entry)) return;
        VisibleLogEntries.Add(entry);
        while (VisibleLogEntries.Count > MaximumLogEntries) VisibleLogEntries.RemoveAt(0);
    }
    private void RebuildVisibleLogs()
    {
        VisibleLogEntries.Clear();
        foreach (var entry in _rawLogs.Where(MatchesFilter).TakeLast(MaximumLogEntries)) VisibleLogEntries.Add(entry);
    }
    private bool MatchesFilter(string entry)
    {
        if (_selectedLogScreenId == "*" && _selectedLogSensorId == "*") return true;
        if (_selectedLogScreenId != "*" && _selectedLogSensorId == "*") return entry.StartsWith($"[{_selectedLogScreenId}/", StringComparison.OrdinalIgnoreCase);
        if (_selectedLogScreenId == "*" && _selectedLogSensorId != "*") return entry.Contains($"/{_selectedLogSensorId}]", StringComparison.OrdinalIgnoreCase);
        return entry.StartsWith($"[{_selectedLogScreenId}/{_selectedLogSensorId}]", StringComparison.OrdinalIgnoreCase);
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
    private void Dispatch(Action action)
    {
        if (_disposed) return;
        if (SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => { if (!_disposed) action(); }, null);
    }
    private void NotifyCommandState()
    {
        foreach (var command in new ICommand[] { AddSensorCommand, DeleteSensorCommand, DeleteOrphanedScreenConfigurationCommand, RestoreUnityResolutionCommand, ConnectSensorCommand, DisconnectSensorCommand, ConnectScreenCommand, DisconnectScreenCommand, StartReplayCommand, PauseReplayCommand, ResumeReplayCommand, StepReplayCommand, StopReplayCommand, SaveConfigurationCommand, StartRecordingCommand, StopRecordingCommand, SelectReplayFileCommand, ResetRegionCommand, BeginCalibrationCommand, CaptureCalibrationPointCommand, UndoCalibrationPointCommand, SaveCalibrationCommand, ClearCalibrationCommand, AddMaskedRegionCommand, DeleteMaskedRegionCommand })
        {
            if (command is RelayCommand relay) relay.NotifyCanExecuteChanged();
            else if (command is AsyncRelayCommand asyncRelay) asyncRelay.NotifyCanExecuteChanged();
        }
        OnPropertyChanged(nameof(CanEditSelectedScreen));
    }
    private string CurrentLogTag() => SelectedScreen is null ? "[SYSTEM]" : SelectedSensor is null ? $"[{SelectedScreen.ScreenId}/FUSION]" : $"[{SelectedScreen.ScreenId}/{SelectedSensor.SensorId}]";

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
