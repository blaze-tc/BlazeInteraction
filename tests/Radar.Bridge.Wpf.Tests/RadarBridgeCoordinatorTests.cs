using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
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
        Assert.Single(upFrame.Pointers);
        Assert.Equal(RadarPointerPhase.Up, upFrame.Pointers[0].Phase);
        Assert.Empty(emptyBatch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Equal(["left"], settledBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.True(factory["front", "f1"].StopCallCount > 0);
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

    private static RadarBridgeCoordinator CreateCoordinator(RadarAppConfiguration configuration, FakePipelineFactory factory)
    {
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        return new RadarBridgeCoordinator(configuration, NullLogger<RadarBridgeCoordinator>.Instance, factory);
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
        Assert.Equal(["left", "front", "right"], acknowledgement.DeserializePayload<HelloAckPayload>().Screens.Select(screen => screen.ScreenId));
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

        public FakePipeline this[string screenId, string sensorId] => _pipelines[(screenId, sensorId)];

        public IRadarSensorPipeline Create(RadarScreenConfiguration screen, RadarSensorConfiguration sensor)
        {
            var pipeline = new FakePipeline(screen.ScreenId, sensor.SensorId);
            _pipelines.Add((screen.ScreenId, sensor.SensorId), pipeline);
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
        public event Action<RadarSensorRuntimeSnapshot>? SnapshotUpdated;
        public event Action<SensorDetectionFrame>? DetectionFrameUpdated;
        public event Action<RadarSensorRuntimeState>? StateChanged;
        public event Action<string>? LogReceived;
        public Task StartAsync(CancellationToken cancellationToken = default) { State = RadarSensorRuntimeState.Running; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task StopAsync() { StopCallCount++; State = RadarSensorRuntimeState.Stopped; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public Task StartRecordingAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopRecordingAsync() => Task.CompletedTask;
        public Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PauseReplay() { }
        public void ResumeReplay() { }
        public void StepReplay() { }
        public Task StopReplayAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Publish(SensorDetectionFrame frame) => DetectionFrameUpdated?.Invoke(frame);
        public void Fault() { State = RadarSensorRuntimeState.Faulted; StateChanged?.Invoke(State); }
    }
}
