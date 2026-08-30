using System.Collections.Concurrent;
using System.IO.Pipes;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RuntimeLoggingTests
{
    [Fact]
    public void RecoverableRenderBoundary_LogsMetricsAndAllowsNextRender()
    {
        var failures = new List<(Exception Exception, RadarDisplayMetrics Metrics)>();
        var shutdownCount = 0;
        var rendered = 0;
        var boundary = new RadarDisplayExceptionBoundary((exception, metrics) => failures.Add((exception, metrics)));
        var metrics = new RadarDisplayMetrics("sensor-1", 100_000, 12_000, 6, 25, 975);

        var failed = boundary.TryRender(() => throw new InvalidOperationException("display failed"), metrics);
        var recovered = boundary.TryRender(() => rendered++, metrics with { RenderedCount = 26 });

        Assert.False(failed);
        Assert.True(recovered);
        Assert.Equal(1, rendered);
        Assert.Equal(0, shutdownCount);
        var failure = Assert.Single(failures);
        Assert.Equal("display failed", failure.Exception.Message);
        Assert.Equal("sensor-1", failure.Metrics.SensorId);
        Assert.Equal(100_000, failure.Metrics.InputPointCount);
        Assert.Equal(12_000, failure.Metrics.DisplayedPointCount);
        Assert.Equal(6, failure.Metrics.TrailLayerCount);
        Assert.Equal(25, failure.Metrics.RenderedCount);
        Assert.Equal(975, failure.Metrics.CoalescedCount);
    }

    [Fact]
    public async Task RuntimeMetrics_AreSampledOncePerSecondInsteadOfWrittenForEveryFrame()
    {
        var configuration = Configuration("unused");
        var factory = new LoggingPipelineFactory();
        await using var coordinator = new RadarBridgeCoordinator(
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarBridgeCoordinator>.Instance,
            factory);
        var logs = new ConcurrentQueue<string>();
        coordinator.LogReceived += logs.Enqueue;
        await coordinator.ApplyUnityTopologyAsync(new HelloPayload(
            Environment.ProcessId,
            "2021.3.45f1",
            [new RadarScreenDefinitionPayload("front", "Front", 1920, 1080, true, 0)]));

        var start = DateTimeOffset.UnixEpoch.AddMinutes(1);
        for (var index = 0; index < 30; index++)
        {
            var timestamp = start.AddMilliseconds(index * 33L);
            factory.Pipeline.PublishSnapshot(Snapshot(timestamp, index + 1));
            factory.Pipeline.PublishDetection(Detection(timestamp, 400 + index, 300));
            coordinator.TickForTest(timestamp);
        }

        Assert.Single(logs.Where(value => value.Contains("[front/f1]", StringComparison.Ordinal) && value.Contains("raw=", StringComparison.Ordinal)));
        Assert.Single(logs.Where(value => value.Contains("[front/FUSION]", StringComparison.Ordinal)));

        var afterInterval = start.AddSeconds(1);
        factory.Pipeline.PublishSnapshot(Snapshot(afterInterval, 31));
        factory.Pipeline.PublishDetection(Detection(afterInterval, 450, 300));
        coordinator.TickForTest(afterInterval);

        Assert.Equal(2, logs.Count(value => value.Contains("[front/f1]", StringComparison.Ordinal) && value.Contains("raw=", StringComparison.Ordinal)));
        Assert.Equal(2, logs.Count(value => value.Contains("[front/FUSION]", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RequiredRuntimeDiagnostics_IncludeSensorFusionAndIpcMetrics()
    {
        var pipeName = "RadarControl.Logging." + Guid.NewGuid().ToString("N");
        var configuration = Configuration(pipeName);
        var factory = new LoggingPipelineFactory();
        await using var coordinator = new RadarBridgeCoordinator(
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarBridgeCoordinator>.Instance,
            factory);
        var logs = new ConcurrentQueue<string>();
        coordinator.LogReceived += logs.Enqueue;
        await coordinator.StartInfrastructureAsync();
        await using var client = await ConnectAsync(pipeName);
        await coordinator.ConnectAllAsync();

        var timestamp = DateTimeOffset.UtcNow;
        factory.Pipeline.PublishSnapshot(new RadarSensorRuntimeSnapshot(
            "front",
            "f1",
            1,
            timestamp,
            [new RadarPoint(100, 100, 1, 1, 1), new RadarPoint(101, 101, 1, 1, 1)],
            [new RadarPoint(100, 100, 1, 1, 1)],
            [new RadarCluster(1, [new RadarPoint(100, 100, 1, 1, 1)], 1, 1, 0.1f, 1)],
            [new SensorDetection(1, 400, 300, 1)],
            30,
            1024,
            7,
            3,
            0));
        factory.Pipeline.PublishDetection(new SensorDetectionFrame(
            "f1",
            timestamp,
            [new SensorDetection(1, 400, 300, 1)]));

        await ReadBatchWithPointersAsync(client, TimeSpan.FromSeconds(3));
        await WaitUntilAsync(
            () => logs.Any(value => value.Contains("batch=", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(1));

        Assert.Contains(logs, value => value.Contains("[front/f1]", StringComparison.Ordinal) && value.Contains("raw=", StringComparison.Ordinal) && value.Contains("valid=", StringComparison.Ordinal) && value.Contains("crc=", StringComparison.Ordinal));
        Assert.Contains(logs, value => value.Contains("[front/FUSION]", StringComparison.Ordinal) && value.Contains("groups=", StringComparison.Ordinal) && value.Contains("pointers=", StringComparison.Ordinal));
        Assert.Contains(logs, value => value.Contains("[IPC]", StringComparison.Ordinal) && value.Contains("batch=", StringComparison.Ordinal) && value.Contains("latencyMs=", StringComparison.Ordinal));
        Assert.Contains("[front/f1] connection running", logs);
        Assert.DoesNotContain(logs, value => value.Contains("[front/f1] [front/f1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CoordinatorPointerLogs_LimitMoveToTenHertzPerKeyWithoutThrottlingLifecycleOrOperationalEvents()
    {
        var configuration = Configuration("unused");
        var factory = new LoggingPipelineFactory();
        await using var coordinator = new RadarBridgeCoordinator(
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarBridgeCoordinator>.Instance,
            factory);
        var logs = new ConcurrentQueue<string>();
        coordinator.LogReceived += logs.Enqueue;
        await coordinator.ApplyUnityTopologyAsync(new HelloPayload(
            Environment.ProcessId,
            "2021.3.45f1",
            [new RadarScreenDefinitionPayload("front", "Front", 1920, 1080, true, 0)]));
        await coordinator.ConnectAllAsync();
        factory.Pipeline.EmitLog("[front/f1] connection pulse");
        factory.Pipeline.EmitLog("[front/f1] connection pulse");
        factory.Pipeline.EmitLog("[front/f1] error: simulated disconnect");
        factory.Pipeline.EmitLog("[front/f1] error: simulated disconnect");

        var start = DateTimeOffset.UnixEpoch.AddSeconds(10);
        factory.Pipeline.PublishDetection(Detection(start, 400, 300));
        Assert.Equal(RadarPointerPhase.Down, Assert.Single(coordinator.TickForTest(start).Screens.Single().Pointers).Phase);
        for (var index = 1; index <= 10; index++)
        {
            var timestamp = start.AddMilliseconds(index * 34);
            factory.Pipeline.PublishDetection(Detection(timestamp, 400 + index, 300));
            Assert.Equal(RadarPointerPhase.Move, Assert.Single(coordinator.TickForTest(timestamp).Screens.Single().Pointers).Phase);
        }

        var firstUpAt = start.AddMilliseconds(400);
        factory.Pipeline.PublishDetection(new SensorDetectionFrame("f1", firstUpAt, []));
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(coordinator.TickForTest(firstUpAt).Screens.Single().Pointers).Phase);

        var secondDownAt = start.AddMilliseconds(500);
        factory.Pipeline.PublishDetection(Detection(secondDownAt, 700, 400));
        Assert.Equal(RadarPointerPhase.Down, Assert.Single(coordinator.TickForTest(secondDownAt).Screens.Single().Pointers).Phase);
        var secondUpAt = start.AddMilliseconds(550);
        factory.Pipeline.PublishDetection(new SensorDetectionFrame("f1", secondUpAt, []));
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(coordinator.TickForTest(secondUpAt).Screens.Single().Pointers).Phase);

        Assert.Equal(2, logs.Count(value => value.StartsWith("[front/P", StringComparison.Ordinal) && value.Contains(" Down ", StringComparison.Ordinal)));
        Assert.Equal(4, logs.Count(value => value.StartsWith("[front/P1] Move ", StringComparison.Ordinal)));
        Assert.Equal(2, logs.Count(value => value.StartsWith("[front/P", StringComparison.Ordinal) && value.Contains(" Up ", StringComparison.Ordinal)));
        Assert.Equal(2, logs.Count(value => value == "[front/f1] connection pulse"));
        Assert.Equal(2, logs.Count(value => value == "[front/f1] error: simulated disconnect"));
    }

    [Fact]
    public async Task HotConfiguration_EmitsTransitionUpLogOnceWhileWireStillPublishesUpThenZero()
    {
        var configuration = Configuration("unused");
        var factory = new LoggingPipelineFactory();
        await using var coordinator = new RadarBridgeCoordinator(
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarBridgeCoordinator>.Instance,
            factory);
        var logs = new ConcurrentQueue<string>();
        coordinator.LogReceived += logs.Enqueue;
        await coordinator.ApplyUnityTopologyAsync(new HelloPayload(
            Environment.ProcessId,
            "2021.3.45f1",
            [new RadarScreenDefinitionPayload("front", "Front", 1920, 1080, true, 0)]));

        var start = DateTimeOffset.UnixEpoch.AddSeconds(10);
        factory.Pipeline.PublishDetection(Detection(start, 400, 300));
        Assert.Equal(RadarPointerPhase.Down, Assert.Single(coordinator.TickForTest(start).Screens.Single().Pointers).Phase);
        factory.Pipeline.PublishDetection(Detection(start.AddMilliseconds(34), 410, 300));
        Assert.Equal(RadarPointerPhase.Move, Assert.Single(coordinator.TickForTest(start.AddMilliseconds(34)).Screens.Single().Pointers).Phase);
        Assert.Equal(1, PointerMoveLogStateCount(coordinator));

        configuration.Screens.Single().WidthPixels = 1600;
        await coordinator.ApplyConfigurationAsync();
        var upBatch = coordinator.TickForTest(start.AddSeconds(1));
        var zeroBatch = coordinator.TickForTest(start.AddSeconds(2));

        Assert.Equal(RadarPointerPhase.Up, Assert.Single(upBatch.Screens.Single().Pointers).Phase);
        Assert.Empty(zeroBatch.Screens.Single().Pointers);
        Assert.Equal(1, logs.Count(value => value.StartsWith("[front/P1] Up ", StringComparison.Ordinal)));
        Assert.Equal(0, PointerMoveLogStateCount(coordinator));
    }

    [Fact]
    public async Task RepeatedUniqueScreenRetirements_ClearMoveLogStateAndLogEveryUp()
    {
        const int retirementCount = 12;
        var configuration = new RadarAppConfiguration
        {
            Ipc = new RadarIpcConfiguration { PipeName = "unused" },
            Screens = Enumerable.Range(0, retirementCount + 1)
                .Select(index => ScreenConfiguration($"screen-{index}", "sensor"))
                .ToList()
        };
        var factory = new LoggingPipelineFactory();
        await using var coordinator = new RadarBridgeCoordinator(
            configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarBridgeCoordinator>.Instance,
            factory);
        var logs = new ConcurrentQueue<string>();
        coordinator.LogReceived += logs.Enqueue;

        await coordinator.ApplyUnityTopologyAsync(new HelloPayload(
            Environment.ProcessId,
            "2021.3.45f1",
            [new RadarScreenDefinitionPayload("screen-0", "Screen 0", 1920, 1080, true, 0)]));

        for (var index = 0; index < retirementCount; index++)
        {
            var screenId = $"screen-{index}";
            var start = DateTimeOffset.UnixEpoch.AddMinutes(index + 1);
            factory.Pipeline.PublishDetection(Detection("sensor", start, 400, 300));
            Assert.Equal(RadarPointerPhase.Down, Assert.Single(coordinator.TickForTest(start).Screens.Single().Pointers).Phase);
            factory.Pipeline.PublishDetection(Detection("sensor", start.AddMilliseconds(34), 410, 300));
            Assert.Equal(RadarPointerPhase.Move, Assert.Single(coordinator.TickForTest(start.AddMilliseconds(34)).Screens.Single().Pointers).Phase);
            Assert.Equal(1, PointerMoveLogStateCount(coordinator));

            var nextScreenId = $"screen-{index + 1}";
            await coordinator.ApplyUnityTopologyAsync(new HelloPayload(
                Environment.ProcessId,
                "2021.3.45f1",
                [new RadarScreenDefinitionPayload(nextScreenId, $"Screen {index + 1}", 1920, 1080, true, 0)]));

            Assert.Equal(0, PointerMoveLogStateCount(coordinator));
            Assert.Equal(1, logs.Count(value => value.StartsWith($"[{screenId}/P1] Up ", StringComparison.Ordinal)));
            Assert.Equal(RadarPointerPhase.Up, Assert.Single(coordinator.TickForTest(start.AddSeconds(1)).Screens.Single(frame => frame.Screen.ScreenId == screenId).Pointers).Phase);
            Assert.Empty(coordinator.TickForTest(start.AddSeconds(2)).Screens.Single(frame => frame.Screen.ScreenId == screenId).Pointers);
            coordinator.TickForTest(start.AddSeconds(3));
        }

        Assert.Equal(retirementCount, logs.Count(value => value.Contains("] Up ", StringComparison.Ordinal)));
        Assert.Equal(0, PointerMoveLogStateCount(coordinator));
    }

    [Fact]
    public async Task CoordinatorMoveLogState_ExpiresAndHasHardCapWhenUpIsMissing()
    {
        await using var coordinator = new RadarBridgeCoordinator(
            Configuration("unused"),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RadarBridgeCoordinator>.Instance,
            new LoggingPipelineFactory());
        var start = DateTimeOffset.UnixEpoch;

        Assert.True(coordinator.ShouldPublishPointerLogForTest(
            "front", Pointer(1, RadarPointerPhase.Move), start));
        Assert.True(coordinator.ShouldPublishPointerLogForTest(
            "front", Pointer(2, RadarPointerPhase.Move), start.AddMinutes(6)));
        Assert.Equal(1, PointerMoveLogStateCount(coordinator));

        for (var index = 0; index < RadarBridgeCoordinator.PointerMoveLogStateCapacity + 100; index++)
        {
            Assert.True(coordinator.ShouldPublishPointerLogForTest(
                "front",
                Pointer(index + 10, RadarPointerPhase.Move),
                start.AddMinutes(6).AddMilliseconds(index * 101L)));
        }

        Assert.Equal(RadarBridgeCoordinator.PointerMoveLogStateCapacity, PointerMoveLogStateCount(coordinator));
        Assert.True(coordinator.ShouldPublishPointerLogForTest(
            "front",
            Pointer(RadarBridgeCoordinator.PointerMoveLogStateCapacity + 109, RadarPointerPhase.Up),
            start.AddMinutes(9)));
        Assert.Equal(RadarBridgeCoordinator.PointerMoveLogStateCapacity - 1, PointerMoveLogStateCount(coordinator));
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(cancellation.Token);
            await IpcStream.WriteAsync(client, IpcEnvelope.Create(
                IpcMessageType.Hello,
                1,
                new HelloPayload(Environment.ProcessId, "2021.3.45f1", [new RadarScreenDefinitionPayload("front", "Front", 1920, 1080, true, 0)])), cancellation.Token);
            Assert.Equal(IpcMessageType.HelloAck, (await IpcStream.ReadAsync(client, cancellation.Token)).MessageType);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task ReadBatchWithPointersAsync(Stream stream, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            var envelope = await IpcStream.ReadAsync(stream, cancellation.Token);
            if (envelope.MessageType == IpcMessageType.PointerBatch &&
                envelope.DeserializePayload<PointerBatchPayload>().Screens.SelectMany(frame => frame.Pointers).Any()) return;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!predicate()) await Task.Delay(10, cancellation.Token);
    }

    private static RadarAppConfiguration Configuration(string pipeName) => new()
    {
        Ipc = new RadarIpcConfiguration { PipeName = pipeName },
        Screens = [ScreenConfiguration("front", "f1")]
    };

    private static RadarScreenConfiguration ScreenConfiguration(string screenId, string sensorId) => new()
    {
        ScreenId = screenId,
        UnityDisplayName = screenId,
        IsPrimary = true,
        ResolutionMode = RadarResolutionMode.Override,
        WidthPixels = 1920,
        HeightPixels = 1080,
        Fusion = new RadarFusionConfiguration { OutputRateHz = 30, SensorDataMaxAgeMilliseconds = 250, FusionDistancePixels = 80 },
        Tracking = new RadarScreenTrackingConfiguration { ConfirmFrames = 1, LostFrames = 1 },
        Interaction = new RadarInteractionConfiguration { MinimumPressMilliseconds = 0 },
        Sensors =
        [
            new RadarSensorConfiguration
            {
                SensorId = sensorId,
                Enabled = true,
                SourceMode = RadarSensorSourceMode.Simulation,
                OutputRectPixels = new Yuexin.Radar.Configuration.RadarPixelRect(0, 0, 1920, 1080)
            }
        ]
    };

    private static int PointerMoveLogStateCount(RadarBridgeCoordinator coordinator)
    {
        var field = typeof(RadarBridgeCoordinator).GetField("_lastPointerMoveLogAt", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(coordinator)).Count;
    }

    private sealed class LoggingPipelineFactory : IRadarSensorPipelineFactory
    {
        public LoggingPipeline Pipeline { get; private set; } = null!;

        public IRadarSensorPipeline Create(RadarScreenConfiguration screen, RadarSensorConfiguration sensor) =>
            Pipeline = new LoggingPipeline(screen.ScreenId, sensor.SensorId);
    }

    private sealed class LoggingPipeline(string screenId, string sensorId) : IRadarSensorPipeline
    {
        public string ScreenId { get; } = screenId;
        public string SensorId { get; } = sensorId;
        public RadarSensorRuntimeState State { get; private set; }
        public long DroppedInputFrameCount => 0;
        public event Action<RadarSensorRuntimeSnapshot>? SnapshotUpdated;
        public event Action<SensorDetectionFrame>? DetectionFrameUpdated;
        public event Action<RadarSensorRuntimeState>? StateChanged;
        public event Action<string>? LogReceived;
        public Task StartAsync(CancellationToken cancellationToken = default) { State = RadarSensorRuntimeState.Running; StateChanged?.Invoke(State); LogReceived?.Invoke("[front/f1] connection running"); return Task.CompletedTask; }
        public Task StopAsync() { State = RadarSensorRuntimeState.Stopped; StateChanged?.Invoke(State); return Task.CompletedTask; }
        public void PublishSnapshot(RadarSensorRuntimeSnapshot snapshot) => SnapshotUpdated?.Invoke(snapshot);
        public void PublishDetection(SensorDetectionFrame frame) => DetectionFrameUpdated?.Invoke(frame);
        public void EmitLog(string message) => LogReceived?.Invoke(message);
        public Task StartRecordingAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopRecordingAsync() => Task.CompletedTask;
        public Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PauseReplay() { }
        public void ResumeReplay() { }
        public void StepReplay() { }
        public Task StopReplayAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SensorDetectionFrame Detection(DateTimeOffset timestamp, float x, float y) =>
        Detection("f1", timestamp, x, y);

    private static SensorDetectionFrame Detection(string sensorId, DateTimeOffset timestamp, float x, float y) =>
        new(sensorId, timestamp, [new SensorDetection(1, x, y, 1)]);

    private static RadarSensorRuntimeSnapshot Snapshot(DateTimeOffset timestamp, long sequence) => new(
        "front",
        "f1",
        sequence,
        timestamp,
        [new RadarPoint(100, 100, 1, 1, 1)],
        [new RadarPoint(100, 100, 1, 1, 1)],
        [new RadarCluster(1, [new RadarPoint(100, 100, 1, 1, 1)], 1, 1, 0.1f, 1)],
        [new SensorDetection(1, 400, 300, 1)],
        30,
        1024,
        0,
        0,
        0);

    private static RadarScreenPointer Pointer(int pointerId, RadarPointerPhase phase) => new(
        pointerId,
        phase,
        0.5f,
        0.5f,
        960f,
        540f,
        1f,
        0L);
}
