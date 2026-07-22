using System.Windows.Input;
using System.Windows.Threading;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task ConnectSensorCommand_TargetsCurrentScreenAndSensor()
    {
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);
        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");
        viewModel.SelectedSensor = viewModel.SelectedScreen.Sensors.Single(sensor => sensor.SensorId == "f2");

        await ExecuteAsync(viewModel.ConnectSensorCommand);

        Assert.Equal(("front", "f2"), runtime.LastConnectedSensor);
    }

    [Fact]
    public void SelectingScreen_SelectsItsFirstSensor()
    {
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), new TestRuntime());

        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "left");

        Assert.Equal("l1", viewModel.SelectedSensor?.SensorId);
    }

    [Fact]
    public async Task AddAndDeleteSensor_UpdatesTheSelectedScreenConfiguration()
    {
        var configuration = ThreeScreenFourSensorConfiguration();
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(configuration, runtime);
        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "left");

        await ExecuteAsync(viewModel.AddSensorCommand);
        var added = Assert.Single(viewModel.SelectedScreen.Sensors.Where(sensor => sensor.SensorId == "sensor-1"));
        viewModel.SelectedSensor = added;
        await ExecuteAsync(viewModel.DeleteSensorCommand);

        Assert.DoesNotContain(configuration.Screens.Single(screen => screen.ScreenId == "left").Sensors, sensor => sensor.SensorId == "sensor-1");
        Assert.Equal(("left", "sensor-1"), runtime.LastDisconnectedSensor);
    }

    [Fact]
    public void LogFilter_KeepsLatestFiveHundredMatchingEntries()
    {
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), new TestRuntime());
        viewModel.SelectedLogScreenId = "front";
        for (var index = 0; index < 650; index++) viewModel.ReceiveLogForTest($"[front/f1] event {index}");
        for (var index = 0; index < 20; index++) viewModel.ReceiveLogForTest($"[left/l1] event {index}");

        Assert.Equal(500, viewModel.VisibleLogEntries.Count);
        Assert.All(viewModel.VisibleLogEntries, entry => Assert.Contains("[front/", entry, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MoveLogs_AreThrottledWithoutSuppressingErrors()
    {
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), new TestRuntime());

        viewModel.ReceiveLogForTest("[front/P1] Move 1");
        viewModel.ReceiveLogForTest("[front/P1] Move 2");
        viewModel.ReceiveLogForTest("[front/f1] error: disconnected");

        Assert.Equal(2, viewModel.VisibleLogEntries.Count);
        Assert.Contains(viewModel.VisibleLogEntries, entry => entry.Contains("error", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FailedCommand_IsLoggedAndCannotRunConcurrently()
    {
        var runtime = new TestRuntime { ConnectSensorException = new InvalidOperationException("network unavailable"), ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);
        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");

        viewModel.ConnectSensorCommand.Execute(null);
        Assert.False(viewModel.ConnectSensorCommand.CanExecute(null));
        runtime.ConnectGate.SetResult();
        await Task.Delay(25);

        Assert.Contains(viewModel.VisibleLogEntries, entry => entry.Contains("network unavailable", StringComparison.Ordinal));
        Assert.True(viewModel.ConnectSensorCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_UnsubscribesRuntimeEvents()
    {
        var runtime = new TestRuntime();
        var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);

        viewModel.Dispose();
        runtime.PublishLog("[front/f1] after dispose");

        Assert.Empty(viewModel.VisibleLogEntries);
    }

    [Fact]
    public void CalibrationAndMaskUseMatchedPhysicalClusters_AndMissingDataDoesNotWrite()
    {
        var configuration = new RadarSensorConfiguration { SensorId = "sensor" };
        var sensor = new SensorItemViewModel(configuration);

        sensor.ApplySnapshot(Snapshot("front", "sensor", 7, new Point2(2f, 3f)));
        Assert.True(sensor.AddMaskedRegionAtCurrentTarget());
        var mask = Assert.Single(configuration.Range.MaskedPolygons);
        Assert.Equal(1.8f, mask[0].X);
        Assert.Equal(3.2f, mask[0].Y);

        sensor.BeginCalibration();
        foreach (var point in new[] { new Point2(-1f, 1f), new Point2(1f, 1f), new Point2(1f, -1f), new Point2(-1f, -1f) })
        {
            sensor.ApplySnapshot(Snapshot("front", "sensor", 7, point));
            Assert.True(sensor.CaptureCurrentTargetForCalibration());
        }
        Assert.True(sensor.SaveCalibration());
        Assert.Equal(new RadarPoint2(-1f, 1f), configuration.Calibration.PhysicalCorners[0]);
        Assert.Equal(new RadarPoint2(1f, -1f), configuration.Calibration.PhysicalCorners[2]);

        var masksBefore = configuration.Range.MaskedPolygons.Count;
        sensor.ApplySnapshot(Snapshot("front", "sensor", 9, new Point2(9f, 9f), detectionId: 7));
        Assert.False(sensor.AddMaskedRegionAtCurrentTarget());
        sensor.ApplySnapshot(Snapshot("front", "sensor", null, new Point2(0f, 0f)));
        Assert.False(sensor.AddMaskedRegionAtCurrentTarget());
        Assert.Equal(masksBefore, configuration.Range.MaskedPolygons.Count);
    }

    [Fact]
    public void SelectedSensorChangesRefreshCalibrationAndMaskCommandState()
    {
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), new TestRuntime());
        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");
        var selected = viewModel.SelectedSensor!;

        Assert.False(viewModel.CaptureCalibrationPointCommand.CanExecute(null));
        Assert.False(viewModel.AddMaskedRegionCommand.CanExecute(null));
        Assert.False(viewModel.DeleteMaskedRegionCommand.CanExecute(null));

        selected.ApplySnapshot(Snapshot("front", selected.SensorId, 2, new Point2(4f, 5f)));
        Assert.True(viewModel.CaptureCalibrationPointCommand.CanExecute(null));
        Assert.True(viewModel.AddMaskedRegionCommand.CanExecute(null));
        viewModel.AddMaskedRegionCommand.Execute(null);
        Assert.True(viewModel.DeleteMaskedRegionCommand.CanExecute(null));
        viewModel.DeleteMaskedRegionCommand.Execute(null);
        Assert.False(viewModel.DeleteMaskedRegionCommand.CanExecute(null));

        viewModel.SelectedSensor = viewModel.SelectedScreen!.Sensors.Single(sensor => sensor.SensorId == "f2");
        Assert.False(viewModel.CaptureCalibrationPointCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedSensorEditsNotifyEveryAdapterCommand()
    {
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), new TestRuntime());
        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");
        var commands = new[]
        {
            viewModel.ResetRegionCommand, viewModel.BeginCalibrationCommand, viewModel.CaptureCalibrationPointCommand,
            viewModel.UndoCalibrationPointCommand, viewModel.SaveCalibrationCommand, viewModel.ClearCalibrationCommand,
            viewModel.AddMaskedRegionCommand, viewModel.DeleteMaskedRegionCommand
        };
        var notifications = 0;
        foreach (var command in commands) command.CanExecuteChanged += (_, _) => notifications++;

        var sensor = viewModel.SelectedSensor!;
        sensor.ApplySnapshot(Snapshot("front", sensor.SensorId, 2, new Point2(4f, 5f)));
        var afterSnapshot = notifications;
        viewModel.ResetRegionCommand.Execute(null);
        var afterRegion = notifications;
        viewModel.AddMaskedRegionCommand.Execute(null);
        var afterMask = notifications;
        viewModel.BeginCalibrationCommand.Execute(null);

        Assert.True(afterSnapshot >= commands.Length);
        Assert.True(afterRegion > afterSnapshot);
        Assert.True(afterMask > afterRegion);
        Assert.True(notifications > afterMask);
    }

    [Fact]
    public async Task ReplayOnNonReplaySensorLeavesConfigurationAndRuntimeUntouched()
    {
        var configuration = ThreeScreenFourSensorConfiguration();
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(configuration, runtime);
        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");
        var sensor = viewModel.SelectedSensor!;
        sensor.ReplayFilePath = "existing.rd";
        sensor.ReplaySpeed = 1.5;
        sensor.ReplayLoop = true;

        await viewModel.ReplaySelectedSensorAsync("ignored.rd", 4d, false);

        Assert.Equal("existing.rd", sensor.ReplayFilePath);
        Assert.Equal(1.5, sensor.ReplaySpeed);
        Assert.True(sensor.ReplayLoop);
        Assert.Equal(0, runtime.ReplayCallCount);
    }

    [Fact]
    public async Task UnassociatedSensorCommandsDoNotReachRuntime()
    {
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);
        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "left");

        await ExecuteAsync(viewModel.ConnectSensorCommand);
        await viewModel.StartRecordingAsync("ignored.rd");

        Assert.Null(runtime.LastConnectedSensor);
        Assert.Equal(0, runtime.StartRecordingCallCount);
    }

    [Fact]
    public void NullContextWorkerSnapshotIsQueuedAndAppliedOnDispatcher()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var runtime = new TestRuntime();
            using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);
            viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");
            var snapshot = Snapshot("front", "f1", 3, new Point2(1f, 2f));

            var worker = new Thread(() => runtime.PublishSensorSnapshot(snapshot));
            worker.Start();
            worker.Join();
            Assert.Null(viewModel.SelectedSensor!.Snapshot);
            dispatcher.Invoke(() => { }, DispatcherPriority.Background);

            Assert.Same(snapshot, viewModel.SelectedSensor!.Snapshot);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        await Task.Delay(25);
    }

    private static RadarAppConfiguration ThreeScreenFourSensorConfiguration() => new()
    {
        Screens =
        [
            Screen("left", false, "l1"),
            Screen("front", true, "f1", "f2"),
            Screen("right", false, "r1")
        ]
    };

    private static RadarScreenConfiguration Screen(string screenId, bool associated, params string[] sensorIds) => new()
    {
        ScreenId = screenId,
        UnityDisplayName = screenId,
        IsAssociated = associated,
        Sensors = sensorIds.Select(sensorId => new RadarSensorConfiguration { SensorId = sensorId, DisplayName = sensorId }).ToList()
    };

    private static RadarSensorRuntimeSnapshot Snapshot(string screenId, string sensorId, int? clusterId, Point2 center, int? detectionId = null) => new(
        screenId,
        sensorId,
        1,
        DateTimeOffset.UtcNow,
        [],
        [],
        clusterId.HasValue ? [new RadarCluster(clusterId.Value, [], center.X, center.Y, 0.1f, 1f)] : [],
        detectionId.HasValue ? [new SensorDetection(detectionId.Value, 1024f, 512f, 1f)] : clusterId.HasValue ? [new SensorDetection(clusterId.Value, 1024f, 512f, 1f)] : [],
        20d,
        100d,
        0,
        0,
        0);

    private sealed class TestRuntime : IRadarBridgeRuntime
    {
        public event Action<RadarSensorRuntimeSnapshot>? SensorSnapshotUpdated;
        event Action<RadarScreenRuntimeSnapshot>? IRadarBridgeRuntime.ScreenSnapshotUpdated { add { } remove { } }
        public event Action<RadarRuntimeSnapshot>? SnapshotUpdated { add { } remove { } }
        public event Action<string>? LogReceived;
        public event Action<RadarConnectionState>? ConnectionStateChanged { add { } remove { } }
        public event Action<UnityClientStatus>? UnityStatusChanged { add { } remove { } }
        public (string ScreenId, string SensorId)? LastConnectedSensor { get; private set; }
        public (string ScreenId, string SensorId)? LastDisconnectedSensor { get; private set; }
        public int ReplayCallCount { get; private set; }
        public int StartRecordingCallCount { get; private set; }
        public Exception? ConnectSensorException { get; init; }
        public TaskCompletionSource? ConnectGate { get; init; }
        public RadarConnectionState ConnectionState => RadarConnectionState.Disconnected;
        public UnityClientStatus UnityStatus => UnityClientStatus.Disconnected;
        public Task StartInfrastructureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectSensorAsync(string screenId, string sensorId, CancellationToken cancellationToken = default)
        {
            LastConnectedSensor = (screenId, sensorId);
            return ConnectAsyncCore();
        }
        private async Task ConnectAsyncCore() { if (ConnectGate is not null) await ConnectGate.Task; if (ConnectSensorException is not null) throw ConnectSensorException; }
        public Task DisconnectSensorAsync(string screenId, string sensorId) { LastDisconnectedSensor = (screenId, sensorId); return Task.CompletedTask; }
        public Task ConnectScreenAsync(string screenId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectScreenAsync(string screenId) => Task.CompletedTask;
        public Task ConnectAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAllAsync() => Task.CompletedTask;
        public Task StartAllSimulationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartRecordingAsync(string screenId, string sensorId, string path, CancellationToken cancellationToken = default) { StartRecordingCallCount++; return Task.CompletedTask; }
        public Task StopRecordingAsync(string screenId, string sensorId) => Task.CompletedTask;
        public Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken = default) { ReplayCallCount++; return Task.CompletedTask; }
        public void PauseReplay(string screenId, string sensorId) { }
        public void ResumeReplay(string screenId, string sensorId) { }
        public void StepReplay(string screenId, string sensorId) { }
        public Task StopReplayAsync(string screenId, string sensorId) => Task.CompletedTask;
        public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void PublishLog(string entry) => LogReceived?.Invoke(entry);
        public void PublishSensorSnapshot(RadarSensorRuntimeSnapshot snapshot) => SensorSnapshotUpdated?.Invoke(snapshot);
    }
}
