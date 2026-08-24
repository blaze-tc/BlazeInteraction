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
    public void SensorEdgeDeadZoneEditors_WriteThePersistedFourSideConfiguration()
    {
        var configuration = new RadarSensorConfiguration();
        var sensor = new SensorItemViewModel(configuration);
        var values = new Dictionary<string, float>
        {
            ["LeftEdgeDeadZoneMeters"] = 0.08f,
            ["RightEdgeDeadZoneMeters"] = 0.09f,
            ["TopEdgeDeadZoneMeters"] = 0.04f,
            ["BottomEdgeDeadZoneMeters"] = 0.12f
        };

        foreach (var (propertyName, value) in values)
        {
            var property = typeof(SensorItemViewModel).GetProperty(propertyName);
            Assert.NotNull(property);
            property.SetValue(sensor, value);
        }

        Assert.Equal(0.08f, configuration.Range.EdgeDeadZones.LeftMeters);
        Assert.Equal(0.09f, configuration.Range.EdgeDeadZones.RightMeters);
        Assert.Equal(0.04f, configuration.Range.EdgeDeadZones.TopMeters);
        Assert.Equal(0.12f, configuration.Range.EdgeDeadZones.BottomMeters);
    }

    [Fact]
    public void FastMotionPreset_UsesResolutionAwareAssociationAndSparsePointTracking()
    {
        var configuration = new RadarAppConfiguration { Screens = [Screen("front", true, "f1")] };
        configuration.Screens[0].IsPrimary = true;
        configuration.Screens[0].ResolutionMode = RadarResolutionMode.Override;
        configuration.Screens[0].WidthPixels = 4096;
        configuration.Screens[0].HeightPixels = 1536;
        using var viewModel = new MainViewModel(configuration, new TestRuntime());
        var commandProperty = typeof(MainViewModel).GetProperty("ApplyFastMotionPresetCommand");
        Assert.NotNull(commandProperty);
        var command = Assert.IsAssignableFrom<ICommand>(commandProperty.GetValue(viewModel));

        command.Execute(null);

        Assert.Equal(30, configuration.Screens[0].Fusion.OutputRateHz);
        Assert.Equal(220, configuration.Screens[0].Fusion.SensorDataMaxAgeMilliseconds);
        Assert.Equal(1, configuration.Screens[0].Tracking.ConfirmFrames);
        Assert.Equal(5, configuration.Screens[0].Tracking.LostFrames);
        Assert.Equal(409.6f, configuration.Screens[0].Tracking.MaximumAssociationDistancePixels, 2);
        Assert.Equal(0.8f, configuration.Screens[0].Tracking.SmoothingAlpha);
        Assert.Equal(1, configuration.Screens[0].Sensors[0].Clustering.MinimumClusterPointCount);
    }

    [Fact]
    public async Task ReplayDialogCommand_OnlyOpensForAssociatedReplaySensor()
    {
        var dialogs = new FakeDialogs { ReplayPath = "sample.radarrec" };
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime, dialogs);

        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "left");
        Assert.False(viewModel.SelectReplayFileCommand.CanExecute(null));
        Assert.Equal(0, dialogs.ReplayDialogCount);

        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");
        viewModel.SelectedSensor!.SourceMode = RadarSensorSourceMode.Replay;
        viewModel.SelectedSensor.ReplaySpeed = 4.25d;
        viewModel.SelectedSensor.ReplayLoop = true;
        Assert.True(viewModel.SelectReplayFileCommand.CanExecute(null));
        viewModel.SelectReplayFileCommand.Execute(null);
        await Task.Delay(25);

        Assert.Equal(1, dialogs.ReplayDialogCount);
        Assert.Equal(1, runtime.ReplayCallCount);
        Assert.Equal(("sample.radarrec", 4.25d, true), runtime.LastReplay);
    }
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
    public async Task StopAllSimulationCommand_RoutesToRuntime()
    {
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);
        var commandProperty = typeof(MainViewModel).GetProperty("StopAllSimulationCommand");
        Assert.NotNull(commandProperty);
        var command = Assert.IsAssignableFrom<ICommand>(commandProperty.GetValue(viewModel));

        await ExecuteAsync(command);

        Assert.Equal(1, runtime.StopAllSimulationCallCount);
    }

    [Fact]
    public void SelectingScreen_SelectsItsFirstSensor()
    {
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), new TestRuntime());

        viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "left");

        Assert.Equal("l1", viewModel.SelectedSensor?.SensorId);
    }

    [Fact]
    public async Task DeleteSensorFromUnassociatedScreen_RemovesConfigurationWithoutRuntimeDisconnect()
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
        Assert.Equal(0, runtime.DisconnectSensorCallCount);
    }

    [Fact]
    public async Task AssociatedScreen_AddsNewSensorDisabledByDefault()
    {
        var configuration = new RadarAppConfiguration { Screens = [Screen("front", true, "f1")] };
        configuration.Screens[0].IsPrimary = true;
        using var viewModel = new MainViewModel(configuration, new TestRuntime());

        Assert.True(viewModel.AddSensorCommand.CanExecute(null));
        await ExecuteAsync(viewModel.AddSensorCommand);

        var added = Assert.Single(viewModel.SelectedScreen!.Sensors.Where(sensor => sensor.SensorId == "sensor-1"));
        Assert.False(added.Enabled);
        Assert.False(configuration.Screens[0].Sensors.Single(sensor => sensor.SensorId == "sensor-1").Enabled);
    }

    [Fact]
    public async Task AssociatedScreen_CanRemoveItsLastSensor()
    {
        var configuration = new RadarAppConfiguration { Screens = [Screen("front", true, "f1")] };
        configuration.Screens[0].IsPrimary = true;
        using var viewModel = new MainViewModel(configuration, new TestRuntime());

        Assert.True(viewModel.DeleteSensorCommand.CanExecute(null));
        await ExecuteAsync(viewModel.DeleteSensorCommand);

        Assert.Empty(viewModel.SelectedScreen!.Sensors);
        Assert.Empty(configuration.Screens[0].Sensors);
        Assert.Null(viewModel.SelectedSensor);
    }

    [Fact]
    public async Task EnablingSensorWithoutRunnableMappingRejectsApply()
    {
        var configuration = new RadarAppConfiguration { Screens = [Screen("front", true, "f1")] };
        configuration.Screens[0].IsPrimary = true;
        configuration.Screens[0].Sensors[0].Enabled = false;
        configuration.Screens[0].Sensors[0].Range.ActivePolygon = [];
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(configuration, runtime);

        viewModel.SelectedSensor!.Enabled = true;
        await ExecuteAsync(viewModel.SaveConfigurationCommand);

        Assert.Equal(0, runtime.ApplyConfigurationCallCount);
        Assert.Contains(viewModel.VisibleLogEntries, entry => entry.Contains("active polygon", StringComparison.OrdinalIgnoreCase) || entry.Contains("calibration", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(RadarSensorRuntimeState.Starting)]
    [InlineData(RadarSensorRuntimeState.Running)]
    [InlineData(RadarSensorRuntimeState.Reconnecting)]
    public async Task RemovingActiveAssociatedSensorAwaitsDisconnectBeforeMutation(RadarSensorRuntimeState runtimeState)
    {
        var configuration = new RadarAppConfiguration { Screens = [Screen("front", true, "f1")] };
        configuration.Screens[0].IsPrimary = true;
        var disconnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new TestRuntime { DisconnectGate = disconnectGate };
        using var viewModel = new MainViewModel(configuration, runtime);
        runtime.PublishSensorState(new RadarSensorRuntimeStateChanged("front", "f1", runtimeState));

        viewModel.DeleteSensorCommand.Execute(null);
        await WaitUntilAsync(() => runtime.DisconnectSensorCallCount == 1);

        Assert.Single(configuration.Screens[0].Sensors);
        Assert.Single(viewModel.SelectedScreen!.Sensors);
        disconnectGate.SetResult();
        await WaitUntilAsync(() => configuration.Screens[0].Sensors.Count == 0);
        Assert.Equal(["disconnect:front/f1"], runtime.Operations);
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
    public void GlobalIpcLogs_UseTwoPartTagAndAppearOnlyInGlobalFilter()
    {
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), new TestRuntime());
        viewModel.ReceiveLogForTest("[GLOBAL/IPC] Unity requested shutdown.");
        viewModel.ReceiveLogForTest("[front/f1] sensor frame");

        Assert.Contains("[GLOBAL/IPC] Unity requested shutdown.", viewModel.VisibleLogEntries);
        viewModel.SelectedLogScreenId = "front";
        Assert.DoesNotContain(viewModel.VisibleLogEntries, entry => entry.StartsWith("[GLOBAL/", StringComparison.Ordinal));
        viewModel.SelectedLogScreenId = "*";
        Assert.Contains("[GLOBAL/IPC] Unity requested shutdown.", viewModel.VisibleLogEntries);
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
    public void MoveLogThrottleState_RemovesOnUpExpiresByTtlAndNeverExceedsHardCap()
    {
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), new TestRuntime());
        var start = DateTimeOffset.UnixEpoch;

        viewModel.ReceiveLogForTest("[front/Pold] Move initial", start);
        viewModel.ReceiveLogForTest("[front/Pfresh] Move initial", start.AddMinutes(6));
        Assert.Equal(1, viewModel.MoveLogThrottleStateCount);

        for (var index = 0; index < MainViewModel.MoveLogThrottleStateCapacity + 100; index++)
        {
            viewModel.ReceiveLogForTest(
                $"[front/P{index}] Move churn",
                start.AddMinutes(6).AddMilliseconds(index));
        }

        Assert.Equal(MainViewModel.MoveLogThrottleStateCapacity, viewModel.MoveLogThrottleStateCount);
        viewModel.ReceiveLogForTest(
            $"[front/P{MainViewModel.MoveLogThrottleStateCapacity + 99}] Up released",
            start.AddMinutes(7));
        Assert.Equal(MainViewModel.MoveLogThrottleStateCapacity - 1, viewModel.MoveLogThrottleStateCount);
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

    [Theory]
    [InlineData(0, 0, "雷达：0/0 未配置")]
    [InlineData(0, 2, "雷达：0/2 未连接")]
    [InlineData(1, 2, "雷达：1/2 部分连接")]
    [InlineData(2, 2, "雷达：2/2 已连接")]
    public async Task RadarConnectionText_ReflectsEnabledRunningSensors(
        int running,
        int enabled,
        string expected)
    {
        var configuration = new RadarAppConfiguration
        {
            Screens =
            [
                Screen("front", true, Enumerable.Range(1, enabled).Select(index => $"s{index}").ToArray())
            ]
        };
        foreach (var sensor in configuration.Screens[0].Sensors) sensor.Enabled = true;
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(configuration, runtime);

        foreach (var sensor in configuration.Screens[0].Sensors.Take(running))
        {
            runtime.PublishSensorState(new RadarSensorRuntimeStateChanged(
                "front", sensor.SensorId, RadarSensorRuntimeState.Running));
        }

        await WaitUntilAsync(() => viewModel.ConnectedRadarCount == running);

        Assert.Equal(enabled, viewModel.EnabledRadarCount);
        Assert.Equal(expected, viewModel.RadarConnectionText);
    }

    [Fact]
    public async Task UnityConnectionText_UsesChineseConnectedState()
    {
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);
        var notifications = new HashSet<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null) notifications.Add(args.PropertyName);
        };

        runtime.PublishUnityStatus(new UnityClientStatus(true, 42, "2021.3.45f1", [], null, 0, null));
        await WaitUntilAsync(() => viewModel.UnityStatus.IsConnected);

        Assert.Equal("Unity：已连接", viewModel.UnityConnectionText);
        Assert.Contains(nameof(MainViewModel.UnityConnectionText), notifications);
        runtime.PublishUnityStatus(UnityClientStatus.Disconnected);
        await WaitUntilAsync(() => !viewModel.UnityStatus.IsConnected);
        Assert.Equal("Unity：未连接", viewModel.UnityConnectionText);
    }

    [Fact]
    public void Constructor_UsesAtomicUnityStatusSubscription()
    {
        var runtime = new TestRuntime();
        runtime.BeforeUnityStatusSubscription = () => runtime.PublishUnityStatus(
            new UnityClientStatus(true, 42, "2021.3.45f1", [], null, 0, null));

        using var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);

        Assert.True(viewModel.UnityStatus.IsConnected);
        Assert.Equal("Unity：已连接", viewModel.UnityConnectionText);
    }

    [Fact]
    public async Task SensorSnapshots_IgnoreStaleBindingAndPreviousSubscription()
    {
        var configuration = new RadarAppConfiguration { Screens = [Screen("front", true, "s1")] };
        configuration.Screens[0].Sensors[0].Enabled = true;
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(configuration, runtime);
        runtime.PublishSensorStateSnapshot(new RadarSensorRuntimeStateSnapshot(
            new RadarSensorRuntimeStateChanged("front", "s1", RadarSensorRuntimeState.Running), 2, 5));
        await WaitUntilAsync(() => viewModel.ConnectedRadarCount == 1);

        runtime.PublishSensorStateSnapshot(new RadarSensorRuntimeStateSnapshot(
            new RadarSensorRuntimeStateChanged("front", "s1", RadarSensorRuntimeState.Stopped), 2, 5));
        runtime.PublishSensorStateSnapshot(new RadarSensorRuntimeStateSnapshot(
            new RadarSensorRuntimeStateChanged("front", "s1", RadarSensorRuntimeState.Faulted), 2, 4));
        await Task.Delay(25);
        Assert.Equal(RadarSensorRuntimeState.Running, viewModel.SelectedScreen!.Sensors.Single().RuntimeState);

        var oldSubscription = runtime.CaptureSensorStateSubscribers();
        runtime.PublishConfigurationChanged();
        await Task.Delay(25);
        oldSubscription?.Invoke(new RadarSensorRuntimeStateSnapshot(
            new RadarSensorRuntimeStateChanged("front", "s1", RadarSensorRuntimeState.Stopped), 1, 99));
        runtime.PublishSensorStateSnapshot(new RadarSensorRuntimeStateSnapshot(
            new RadarSensorRuntimeStateChanged("front", "s1", RadarSensorRuntimeState.Stopped), 1, 100));
        await Task.Delay(25);

        Assert.Equal(RadarSensorRuntimeState.Running, viewModel.SelectedScreen!.Sensors.Single().RuntimeState);
        Assert.Equal("雷达：1/1 已连接", viewModel.RadarConnectionText);
    }

    [Fact]
    public async Task ConnectionStatus_RefreshesForSensorEnabledStateAndConfigurationChanges()
    {
        var configuration = new RadarAppConfiguration { Screens = [Screen("front", true, "s1")] };
        configuration.Screens[0].Sensors[0].Enabled = false;
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(configuration, runtime);
        var notifications = new HashSet<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null) notifications.Add(args.PropertyName);
        };

        viewModel.SelectedSensor!.Enabled = true;
        await WaitUntilAsync(() => viewModel.EnabledRadarCount == 1);
        runtime.PublishSensorState(new RadarSensorRuntimeStateChanged("front", "s1", RadarSensorRuntimeState.Running));
        await WaitUntilAsync(() => viewModel.ConnectedRadarCount == 1);

        configuration.Screens[0].Sensors[0].Enabled = false;
        runtime.PublishConfigurationChanged();
        await WaitUntilAsync(() => viewModel.EnabledRadarCount == 0);

        Assert.Contains(nameof(MainViewModel.EnabledRadarCount), notifications);
        Assert.Contains(nameof(MainViewModel.ConnectedRadarCount), notifications);
        Assert.Contains(nameof(MainViewModel.RadarConnectionText), notifications);
        Assert.Equal("雷达：0/0 未配置", viewModel.RadarConnectionText);
    }

    [Fact]
    public async Task ConnectionStatus_RefreshesWhenSensorsAreAddedDeletedAndScreensAreRebuilt()
    {
        var configuration = new RadarAppConfiguration { Screens = [Screen("front", true, "s1")] };
        configuration.Screens[0].Sensors[0].Enabled = true;
        var runtime = new TestRuntime();
        using var viewModel = new MainViewModel(configuration, runtime);
        var originalScreen = viewModel.SelectedScreen!;

        await ExecuteAsync(viewModel.AddSensorCommand);
        var added = viewModel.SelectedScreen!.Sensors.Single(sensor => sensor.SensorId == "sensor-1");
        added.Enabled = true;
        await WaitUntilAsync(() => viewModel.EnabledRadarCount == 2);
        Assert.Equal("雷达：0/2 未连接", viewModel.RadarConnectionText);

        viewModel.SelectedSensor = added;
        await ExecuteAsync(viewModel.DeleteSensorCommand);
        await WaitUntilAsync(() => viewModel.EnabledRadarCount == 1);

        runtime.PublishConfigurationChanged();
        await WaitUntilAsync(() => !ReferenceEquals(originalScreen, viewModel.SelectedScreen));
        var notificationsAfterRefresh = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.RadarConnectionText)) notificationsAfterRefresh++;
        };
        originalScreen.Sensors[0].Enabled = false;
        await Task.Delay(25);

        Assert.Equal(0, notificationsAfterRefresh);
        Assert.Equal(1, viewModel.EnabledRadarCount);
    }

    [Fact]
    public async Task Dispose_PreventsConnectionStatusUpdates()
    {
        var runtime = new TestRuntime();
        var viewModel = new MainViewModel(ThreeScreenFourSensorConfiguration(), runtime);
        var notifications = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.UnityConnectionText) or nameof(MainViewModel.RadarConnectionText)) notifications++;
        };

        viewModel.Dispose();
        runtime.PublishUnityStatus(new UnityClientStatus(true, 42, "2021.3.45f1", [], null, 0, null));
        runtime.PublishSensorState(new RadarSensorRuntimeStateChanged("front", "f1", RadarSensorRuntimeState.Running));
        await Task.Delay(25);

        Assert.Equal(0, notifications);
        Assert.Equal("Unity：未连接", viewModel.UnityConnectionText);
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

        selected.ApplySnapshot(Snapshot("front", selected.SensorId, 9, new Point2(4f, 5f), detectionId: 2));
        Assert.False(viewModel.CaptureCalibrationPointCommand.CanExecute(null));
        Assert.False(viewModel.AddMaskedRegionCommand.CanExecute(null));

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

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate()) await Task.Delay(10, cancellation.Token);
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
        public event Action<string>? LogReceived;
        public event Action<UnityClientStatus>? UnityStatusChanged;
        private event Action<RadarSensorRuntimeStateSnapshot>? SensorStateSnapshotChanged;
        private readonly Dictionary<string, RadarSensorRuntimeStateSnapshot> _sensorStates = new(StringComparer.OrdinalIgnoreCase);
        public (string ScreenId, string SensorId)? LastConnectedSensor { get; private set; }
        public (string ScreenId, string SensorId)? LastDisconnectedSensor { get; private set; }
        public int DisconnectSensorCallCount { get; private set; }
        public int ReplayCallCount { get; private set; }
        public (string Path, double Speed, bool Loop)? LastReplay { get; private set; }
        public int StartRecordingCallCount { get; private set; }
        public int ApplyConfigurationCallCount { get; private set; }
        public int StopAllSimulationCallCount { get; private set; }
        public Exception? ConnectSensorException { get; init; }
        public TaskCompletionSource? ConnectGate { get; init; }
        public TaskCompletionSource? DisconnectGate { get; init; }
        public Action? BeforeUnityStatusSubscription { get; set; }
        public List<string> Operations { get; } = [];
        public UnityClientStatus UnityStatus { get; private set; } = UnityClientStatus.Disconnected;
        public Task StartInfrastructureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectSensorAsync(string screenId, string sensorId, CancellationToken cancellationToken = default)
        {
            LastConnectedSensor = (screenId, sensorId);
            return ConnectAsyncCore();
        }
        private async Task ConnectAsyncCore() { if (ConnectGate is not null) await ConnectGate.Task; if (ConnectSensorException is not null) throw ConnectSensorException; }
        public async Task DisconnectSensorAsync(string screenId, string sensorId)
        {
            DisconnectSensorCallCount++;
            LastDisconnectedSensor = (screenId, sensorId);
            Operations.Add($"disconnect:{screenId}/{sensorId}");
            if (DisconnectGate is not null) await DisconnectGate.Task;
        }
        public Task ConnectScreenAsync(string screenId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectScreenAsync(string screenId) => Task.CompletedTask;
        public Task ConnectAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAllAsync() => Task.CompletedTask;
        public Task StartAllSimulationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAllSimulationAsync() { StopAllSimulationCallCount++; return Task.CompletedTask; }
        public Task StartRecordingAsync(string screenId, string sensorId, string path, CancellationToken cancellationToken = default) { StartRecordingCallCount++; return Task.CompletedTask; }
        public Task StopRecordingAsync(string screenId, string sensorId) => Task.CompletedTask;
        public Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken = default) { ReplayCallCount++; LastReplay = (path, speed, loop); return Task.CompletedTask; }
        public void PauseReplay(string screenId, string sensorId) { }
        public void ResumeReplay(string screenId, string sensorId) { }
        public void StepReplay(string screenId, string sensorId) { }
        public Task StopReplayAsync(string screenId, string sensorId) => Task.CompletedTask;
        public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) { ApplyConfigurationCallCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void PublishLog(string entry) => LogReceived?.Invoke(entry);
        public void PublishSensorSnapshot(RadarSensorRuntimeSnapshot snapshot) => SensorSnapshotUpdated?.Invoke(snapshot);
        public void PublishSensorState(RadarSensorRuntimeStateChanged state)
        {
            SensorStateChanged?.Invoke(state);
            var key = string.Concat(state.ScreenId, "\u001F", state.SensorId);
            var version = _sensorStates.TryGetValue(key, out var prior) ? checked(prior.Version + 1) : 1;
            var snapshot = new RadarSensorRuntimeStateSnapshot(state, 1, version);
            _sensorStates[key] = snapshot;
            SensorStateSnapshotChanged?.Invoke(snapshot);
        }
        public void PublishSensorStateSnapshot(RadarSensorRuntimeStateSnapshot snapshot)
        {
            var state = snapshot.State;
            var key = string.Concat(state.ScreenId, "\u001F", state.SensorId);
            if (!_sensorStates.TryGetValue(key, out var cached) ||
                snapshot.BindingGeneration > cached.BindingGeneration ||
                (snapshot.BindingGeneration == cached.BindingGeneration && snapshot.Version > cached.Version))
            {
                _sensorStates[key] = snapshot;
            }
            SensorStateSnapshotChanged?.Invoke(snapshot);
        }
        public Action<RadarSensorRuntimeStateSnapshot>? CaptureSensorStateSubscribers() => SensorStateSnapshotChanged;
        public void PublishUnityStatus(UnityClientStatus status)
        {
            UnityStatus = status;
            UnityStatusChanged?.Invoke(status);
        }
        public void PublishConfigurationChanged() => ConfigurationChanged?.Invoke();
        public void SubscribeUnityStatus(Action<UnityClientStatus> handler)
        {
            UnityStatusChanged += handler;
            BeforeUnityStatusSubscription?.Invoke();
            handler(UnityStatus);
        }
        public void UnsubscribeUnityStatus(Action<UnityClientStatus> handler) => UnityStatusChanged -= handler;
        public void SubscribeSensorStates(Action<RadarSensorRuntimeStateSnapshot> handler)
        {
            SensorStateSnapshotChanged += handler;
            foreach (var snapshot in _sensorStates.Values) handler(snapshot);
        }
        public void UnsubscribeSensorStates(Action<RadarSensorRuntimeStateSnapshot> handler) => SensorStateSnapshotChanged -= handler;
        public event Action<RadarSensorRuntimeStateChanged>? SensorStateChanged;
        public event Action? ConfigurationChanged;
    }

    private sealed class FakeDialogs : IFileDialogService
    {
        public string? ReplayPath { get; init; }
        public int ReplayDialogCount { get; private set; }
        public string? SelectRecordingPath() => null;
        public string? SelectReplayPath() { ReplayDialogCount++; return ReplayPath; }
    }
}
