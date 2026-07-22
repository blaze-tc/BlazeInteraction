using System.Windows.Input;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;

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
        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");

        await ExecuteAsync(viewModel.AddSensorCommand);
        var added = Assert.Single(viewModel.SelectedScreen.Sensors.Where(sensor => sensor.SensorId == "sensor-1"));
        viewModel.SelectedSensor = added;
        await ExecuteAsync(viewModel.DeleteSensorCommand);

        Assert.DoesNotContain(configuration.Screens.Single(screen => screen.ScreenId == "front").Sensors, sensor => sensor.SensorId == "sensor-1");
        Assert.Equal(("front", "sensor-1"), runtime.LastDisconnectedSensor);
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

    private static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        await Task.Delay(25);
    }

    private static RadarAppConfiguration ThreeScreenFourSensorConfiguration() => new()
    {
        Screens =
        [
            Screen("left", "l1"),
            Screen("front", "f1", "f2"),
            Screen("right", "r1")
        ]
    };

    private static RadarScreenConfiguration Screen(string screenId, params string[] sensorIds) => new()
    {
        ScreenId = screenId,
        UnityDisplayName = screenId,
        IsAssociated = false,
        Sensors = sensorIds.Select(sensorId => new RadarSensorConfiguration { SensorId = sensorId, DisplayName = sensorId }).ToList()
    };

    private sealed class TestRuntime : IRadarBridgeRuntime
    {
        public event Action<RadarSensorRuntimeSnapshot>? SensorSnapshotUpdated;
        public event Action<RadarScreenRuntimeSnapshot>? ScreenSnapshotUpdated;
        public event Action<RadarRuntimeSnapshot>? SnapshotUpdated { add { } remove { } }
        public event Action<string>? LogReceived;
        public event Action<RadarConnectionState>? ConnectionStateChanged { add { } remove { } }
        public event Action<UnityClientStatus>? UnityStatusChanged { add { } remove { } }
        public (string ScreenId, string SensorId)? LastConnectedSensor { get; private set; }
        public (string ScreenId, string SensorId)? LastDisconnectedSensor { get; private set; }
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
        public Task StartRecordingAsync(string screenId, string sensorId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopRecordingAsync(string screenId, string sensorId) => Task.CompletedTask;
        public Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PauseReplay(string screenId, string sensorId) { }
        public void ResumeReplay(string screenId, string sensorId) { }
        public void StepReplay(string screenId, string sensorId) { }
        public Task StopReplayAsync(string screenId, string sensorId) => Task.CompletedTask;
        public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void PublishLog(string entry) => LogReceived?.Invoke(entry);
    }
}
