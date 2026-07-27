using System.Collections.Concurrent;
using System.IO.Pipes;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RuntimeLoggingTests
{
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
        Screens =
        [
            new RadarScreenConfiguration
            {
                ScreenId = "front",
                UnityDisplayName = "Front",
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
                        SensorId = "f1",
                        Enabled = true,
                        SourceMode = RadarSensorSourceMode.Simulation,
                        OutputRectPixels = new Yuexin.Radar.Configuration.RadarPixelRect(0, 0, 1920, 1080)
                    }
                ]
            }
        ]
    };

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
        new("f1", timestamp, [new SensorDetection(1, x, y, 1)]);
}
