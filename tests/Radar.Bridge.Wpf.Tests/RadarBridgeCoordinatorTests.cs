using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using Yuexin.Radar.Bridge.Wpf;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarBridgeCoordinatorTests
{
    [Fact]
    public async Task Coordinator_PublishesAllEnabledScreensAndMergesFrontOverlap()
    {
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2)));

        factory["front", "f1"].Publish(Detection("f1", 1000, 700));
        factory["front", "f2"].Publish(Detection("f2", 1040, 700));

        var batch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(["left", "front", "right"], batch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Empty(batch.Screens.Single(frame => frame.Screen.ScreenId == "left").Pointers);
        Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Equal(4096, batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Screen.WidthPixels);
    }

    [Fact]
    public async Task Coordinator_IsolatesFaultedSensorAndContinuesSiblingScreenFusion()
    {
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2)));
        factory["front", "f1"].Fault();
        factory["front", "f2"].Publish(Detection("f2", 1100, 700));
        factory["right", "r1"].Publish(Detection("r1", 500, 300));

        var batch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "right").Pointers);
        Assert.Equal(RadarSensorRuntimeState.Faulted, factory["front", "f1"].State);
    }

    [Fact]
    public async Task Coordinator_HotRemovedScreenPublishesUpThenEmptyFrameBeforeReset()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [ScreenConfiguration("front", "f1", 1920, 1080)]
        };
        configuration.Screens[0].Tracking.ConfirmFrames = 1;
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].Publish(Detection("f1", 400, 300));
        Assert.Single(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens.Single().Pointers);

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        var upBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2));
        var emptyBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3));
        var settledBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(4));

        var upFrame = upBatch.Screens.Single(frame => frame.Screen.ScreenId == "front");
        Assert.Equal(["front", "left"], upBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Single(upFrame.Pointers);
        Assert.Equal(RadarPointerPhase.Up, upFrame.Pointers[0].Phase);
        Assert.Equal(["front", "left"], emptyBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Empty(emptyBatch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Equal(["left"], settledBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.True(factory["front", "f1"].StopCallCount > 0);
    }

    [Fact]
    public async Task Coordinator_RetriesUndeliveredTransitionBeforeActivatingReplacement()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        configuration.Screens[0].Tracking.ConfirmFrames = 1;
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].Publish(Detection("f1", 400, 300));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        var failedUp = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2), delivered: false);
        var retriedUp = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3));
        var empty = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(4));
        var settled = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(5));

        Assert.Equal(RadarPointerPhase.Up, Assert.Single(failedUp.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers).Phase);
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(retriedUp.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers).Phase);
        Assert.Empty(empty.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Equal(["left"], settled.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Equal(1, factory["front", "f1"].StopCallCount);
    }

    [Fact]
    public async Task Coordinator_WaitsForLeasedPipelineOperationBeforeRetirementDisposesIt()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].BlockStart();
        var connect = coordinator.ConnectSensorAsync("front", "f1");
        await factory["front", "f1"].StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3), waitForRetirement: false);
        await Task.Delay(20);

        Assert.False(factory["front", "f1"].DisposedDuringOperation);
        factory["front", "f1"].ReleaseStart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await WaitUntilAsync(() => factory["front", "f1"].Disposed, new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
        Assert.False(factory["front", "f1"].DisposedDuringOperation);
    }

    [Fact]
    public async Task Coordinator_FactoryFailureLeavesPreviousTopologyAndConfigurationUnchanged()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory { ThrowOnCreate = true };
        await using var coordinator = CreateCoordinator(configuration, factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0))));

        Assert.Equal(["front"], configuration.Screens.Select(screen => screen.ScreenId));
        Assert.Empty(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens);
    }

    [Fact]
    public async Task Coordinator_SaveFailureLeavesTopologyAndConfigurationUnchanged()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        var saves = 0;
        await using var coordinator = CreateCoordinator(configuration, factory, (_, _) => { saves++; return Task.FromException(new IOException("save failed")); });

        await Assert.ThrowsAsync<IOException>(() => coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0))));

        Assert.Equal(1, saves);
        Assert.Equal(["front"], configuration.Screens.Select(screen => screen.ScreenId));
        Assert.True(factory.AllDisposed);
        Assert.Empty(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens);
    }

    [Fact]
    public async Task Coordinator_ApplyConfigurationSaveFailureLeavesActiveRuntimeUnchanged()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory, (_, _) => Task.FromException(new IOException("save failed")));

        await Assert.ThrowsAsync<IOException>(() => coordinator.ApplyConfigurationAsync());

        Assert.Empty(factory.Created);
        Assert.Empty(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens);
    }

    [Fact]
    public async Task Coordinator_SecondPipelineFactoryFailureDisposesFirstStagedPipeline()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [new RadarScreenConfiguration { ScreenId = "front", UnityDisplayName = "Front", Sensors = [Sensor("f1"), Sensor("f2")] }]
        };
        var factory = new FakePipelineFactory { ThrowOnCreateNumber = 2 };
        await using var coordinator = CreateCoordinator(configuration, factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0))));

        Assert.True(factory["front", "f1"].Disposed);
        Assert.Empty(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens);
    }

    [Fact]
    public async Task Coordinator_StagesExistingScreenNewSensorBeforePersist()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var order = new List<string>();
        var factory = new FakePipelineFactory { CreateObserved = sensor => order.Add("factory:" + sensor) };
        await using var coordinator = CreateCoordinator(configuration, factory, (_, _) => { order.Add("persist"); return Task.CompletedTask; });
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        order.Clear();
        configuration.Screens[0].Sensors.Add(Sensor("f2"));

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));

        Assert.Equal(["factory:f1", "factory:f2", "persist"], order);
    }

    [Fact]
    public async Task Coordinator_DisposeBoundsHungOperationAndQuarantinesPipelineUntilRelease()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].BlockStart(ignoreCancellation: true);
        var operation = coordinator.ConnectSensorAsync("front", "f1");
        await factory["front", "f1"].StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(factory["front", "f1"].Disposed);
        factory["front", "f1"].ReleaseStart();
        await operation;
        await WaitUntilAsync(() => factory["front", "f1"].Disposed, new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
    }

    [Fact]
    public async Task Coordinator_BlockedTransitionSendSerializesConcurrentTopologyAndPreservesUpThenEmpty()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        configuration.Screens[0].Tracking.ConfirmFrames = 1;
        var factory = new FakePipelineFactory();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new System.Collections.Concurrent.ConcurrentQueue<PointerBatchPayload>();
        var upAttempts = 0;
        await using var coordinator = CreateCoordinator(configuration, factory, send: async (batch, _) =>
        {
            sent.Enqueue(batch);
            if (batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Any(pointer => pointer.Phase == RadarPointerPhase.Up)))
            {
                if (Interlocked.Increment(ref upAttempts) == 1)
                {
                    sendStarted.TrySetResult();
                    await releaseSend.Task;
                    return false;
                }
            }
            return true;
        });
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].Publish(Detection("f1", 400, 300));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        await coordinator.StartInfrastructureAsync();
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var concurrent = coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        await concurrent.WaitAsync(TimeSpan.FromSeconds(1));
        releaseSend.TrySetResult();
        await WaitUntilAsync(() => Volatile.Read(ref upAttempts) >= 2 && sent.Any(batch => batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Count == 0)), new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);

        var batches = sent.ToArray();
        var firstUp = Array.FindIndex(batches, batch => batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Any(pointer => pointer.Phase == RadarPointerPhase.Up)));
        var retriedUp = Array.FindIndex(batches, firstUp + 1, batch => batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Any(pointer => pointer.Phase == RadarPointerPhase.Up)));
        var empty = Array.FindIndex(batches, retriedUp + 1, batch => batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Count == 0));
        Assert.Equal(2, upAttempts);
        Assert.True(firstUp >= 0 && retriedUp > firstUp && empty > retriedUp);
    }

    [Fact]
    public async Task Coordinator_LegacyStartRecordingLeaseDefersRetirementAndBecomesSafeAfterRetirement()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        var retiringPipeline = factory["front", "f1"];
        retiringPipeline.BlockRecording(ignoreCancellation: true);

#pragma warning disable CS0618
        var recording = coordinator.StartRecordingAsync("capture.rdr");
#pragma warning restore CS0618
        await retiringPipeline.RecordingEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.ApplyConfigurationAsync();
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2), waitForRetirement: false);

        Assert.False(retiringPipeline.Disposed);
#pragma warning disable CS0618
        await coordinator.StartRecordingAsync("obsolete-safe.rdr");
#pragma warning restore CS0618
        Assert.Equal(1, retiringPipeline.RecordingCallCount);

        retiringPipeline.ReleaseRecording();
        await recording;
        await WaitUntilAsync(() => retiringPipeline.Disposed, new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
        Assert.False(retiringPipeline.DisposedDuringOperation);
    }

    [Fact]
    public async Task Coordinator_StartStopAndDisposeAreConcurrentSafe()
    {
        var factory = new FakePipelineFactory();
        var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), factory);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => coordinator.StartInfrastructureAsync()));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => coordinator.DisconnectAllAsync()));
            await Task.WhenAll(coordinator.DisposeAsync().AsTask(), coordinator.DisposeAsync().AsTask());
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Coordinator_ScreenRemovalTransitionsWithACompleteThreeScreenBatch()
    {
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2)));
        factory["front", "f1"].Publish(Detection("f1", 1000, 700));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", true, 1920, 1440, 0),
            Screen("right", "Right", false, 1920, 1440, 1)));
        var upBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2));
        var emptyBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.Equal(["left", "front", "right"], upBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Equal(["left", "front", "right"], emptyBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(upBatch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers).Phase);
        Assert.Empty(emptyBatch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.True(emptyBatch.Screens.Select(frame => frame.Sequence).Distinct().Single() > upBatch.Screens.Select(frame => frame.Sequence).Distinct().Single());
    }

    [Fact]
    public async Task Coordinator_ReconnectsIpcAndPublishesCompleteBatchesAfterEachHello()
    {
        var factory = new FakePipelineFactory();
        var configuration = ThreeScreenFourSensorConfiguration();
        await using var coordinator = CreateCoordinator(configuration, factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await coordinator.StartInfrastructureAsync(cancellation.Token);

        await using (var first = await ConnectAsync(configuration.Ipc.PipeName, cancellation.Token))
        {
            var firstBatch = await ReadBatchAsync(first, cancellation.Token);
            Assert.Equal(["left", "front", "right"], firstBatch.Screens.Select(frame => frame.Screen.ScreenId));
        }

        await WaitUntilAsync(() => !coordinator.UnityStatus.IsConnected, cancellation.Token);
        await using var second = await ConnectAsync(configuration.Ipc.PipeName, cancellation.Token);
        var secondBatch = await ReadBatchAsync(second, cancellation.Token);

        Assert.Equal(["left", "front", "right"], secondBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.True(secondBatch.Screens[0].Sequence > 0);
        Assert.True(coordinator.UnityStatus.IsConnected);
    }

    private static RadarBridgeCoordinator CreateCoordinator(RadarAppConfiguration configuration, FakePipelineFactory factory, Func<RadarAppConfiguration, CancellationToken, Task>? persist = null, Func<PointerBatchPayload, CancellationToken, Task<bool>>? send = null)
    {
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        return new RadarBridgeCoordinator(configuration, NullLogger<RadarBridgeCoordinator>.Instance, factory, persistConfigurationAsync: persist, sendPointerBatchAsync: send);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellationToken);
        await IpcStream.WriteAsync(client, IpcEnvelope.Create(IpcMessageType.Hello, 1, Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2))), cancellationToken);
        var acknowledgement = await IpcStream.ReadAsync(client, cancellationToken);
        Assert.Equal(IpcMessageType.HelloAck, acknowledgement.MessageType);
        var ack = acknowledgement.DeserializePayload<HelloAckPayload>();
        Assert.Equal("1.2.0", BridgeVersion.Value);
        Assert.Equal(BridgeVersion.Value, ack.BridgeVersion);
        Assert.Equal(["left", "front", "right"], ack.Screens.Select(screen => screen.ScreenId));
        return client;
    }

    private static async Task<PointerBatchPayload> ReadBatchAsync(Stream client, CancellationToken cancellationToken)
    {
        while (true)
        {
            var envelope = await IpcStream.ReadAsync(client, cancellationToken);
            if (envelope.MessageType == IpcMessageType.PointerBatch) return envelope.DeserializePayload<PointerBatchPayload>();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate()) await Task.Delay(10, cancellationToken);
    }

    private static RadarAppConfiguration ThreeScreenFourSensorConfiguration() => new()
    {
        Screens =
        [
            ScreenConfiguration("left", "l1", 1920, 1440),
            new RadarScreenConfiguration
            {
                ScreenId = "front", UnityDisplayName = "Front", ResolutionMode = RadarResolutionMode.Override,
                WidthPixels = 4096, HeightPixels = 1536, Sensors = [Sensor("f1"), Sensor("f2")],
                Tracking = new RadarScreenTrackingConfiguration { ConfirmFrames = 1 }
            },
            ScreenConfiguration("right", "r1", 1920, 1440)
        ]
    };

    private static RadarScreenConfiguration ScreenConfiguration(string screenId, string sensorId, int width, int height) => new()
    {
        ScreenId = screenId,
        UnityDisplayName = screenId,
        ResolutionMode = RadarResolutionMode.Override,
        WidthPixels = width,
        HeightPixels = height,
        Sensors = [Sensor(sensorId)],
        Tracking = new RadarScreenTrackingConfiguration { ConfirmFrames = 1 }
    };

    private static RadarSensorConfiguration Sensor(string sensorId) => new()
    {
        SensorId = sensorId,
        Enabled = true,
        SourceMode = RadarSensorSourceMode.Simulation
    };

    private static RadarScreenDefinitionPayload Screen(string id, string name, bool primary, int width, int height, int order) =>
        new(id, name, width, height, primary, order);

    private static HelloPayload Hello(params RadarScreenDefinitionPayload[] screens) => new(42, "2021.3", screens);

    private static SensorDetectionFrame Detection(string sensorId, float x, float y) =>
        new(sensorId, DateTimeOffset.UnixEpoch.AddMilliseconds(950), [new SensorDetection(1, x, y, 1f)]);

    private sealed class FakePipelineFactory : IRadarSensorPipelineFactory
    {
        private readonly Dictionary<(string ScreenId, string SensorId), FakePipeline> _pipelines = new();
        public bool ThrowOnCreate { get; set; }
        public int ThrowOnCreateNumber { get; set; }
        public Action<string>? CreateObserved { get; set; }
        private int _createCalls;
        public bool AllDisposed => _pipelines.Values.All(value => value.Disposed);
        public IReadOnlyCollection<FakePipeline> Created => _pipelines.Values;

        public FakePipeline this[string screenId, string sensorId] => _pipelines[(screenId, sensorId)];

        public IRadarSensorPipeline Create(RadarScreenConfiguration screen, RadarSensorConfiguration sensor)
        {
            if (ThrowOnCreate || ++_createCalls == ThrowOnCreateNumber) throw new InvalidOperationException("factory failure");
            CreateObserved?.Invoke(sensor.SensorId);
            var pipeline = new FakePipeline(screen.ScreenId, sensor.SensorId);
            _pipelines[(screen.ScreenId, sensor.SensorId)] = pipeline;
            return pipeline;
        }
    }

    private sealed class FakePipeline(string screenId, string sensorId) : IRadarSensorPipeline
    {
        public string ScreenId { get; } = screenId;
        public string SensorId { get; } = sensorId;
        public RadarSensorRuntimeState State { get; private set; } = RadarSensorRuntimeState.Stopped;
        public long DroppedInputFrameCount => 0;
        public int StopCallCount { get; private set; }
        public bool Disposed { get; private set; }
        public bool DisposedDuringOperation { get; private set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecordingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RecordingCallCount { get; private set; }
        private TaskCompletionSource? _startRelease;
        private TaskCompletionSource? _recordingRelease;
        private bool _ignoreStartCancellation;
        private bool _ignoreRecordingCancellation;
        public event Action<RadarSensorRuntimeSnapshot>? SnapshotUpdated;
        public event Action<SensorDetectionFrame>? DetectionFrameUpdated;
        public event Action<RadarSensorRuntimeState>? StateChanged;
        public event Action<string>? LogReceived;
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartEntered.TrySetResult();
            if (_startRelease is not null)
            {
                if (_ignoreStartCancellation) await _startRelease.Task;
                else await _startRelease.Task.WaitAsync(cancellationToken);
            }
            DisposedDuringOperation |= Disposed;
            State = RadarSensorRuntimeState.Running;
            StateChanged?.Invoke(State);
        }
        public Task StopAsync() { StopCallCount++; State = RadarSensorRuntimeState.Stopped; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public async Task StartRecordingAsync(string path, CancellationToken cancellationToken = default)
        {
            RecordingCallCount++;
            RecordingEntered.TrySetResult();
            if (_recordingRelease is not null)
            {
                if (_ignoreRecordingCancellation) await _recordingRelease.Task;
                else await _recordingRelease.Task.WaitAsync(cancellationToken);
            }
            DisposedDuringOperation |= Disposed;
        }
        public Task StopRecordingAsync() => Task.CompletedTask;
        public Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PauseReplay() { }
        public void ResumeReplay() { }
        public void StepReplay() { }
        public Task StopReplayAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        public void BlockStart(bool ignoreCancellation = false) { _ignoreStartCancellation = ignoreCancellation; _startRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ReleaseStart() => _startRelease?.TrySetResult();
        public void BlockRecording(bool ignoreCancellation = false) { _ignoreRecordingCancellation = ignoreCancellation; _recordingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ReleaseRecording() => _recordingRelease?.TrySetResult();
        public void Publish(SensorDetectionFrame frame) => DetectionFrameUpdated?.Invoke(frame);
        public void Fault() { State = RadarSensorRuntimeState.Faulted; StateChanged?.Invoke(State); }
    }
}
