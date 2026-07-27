using System.Collections.Concurrent;
using System.IO.Pipes;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
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
    public async Task MoveLogs_AreLimitedToTenHertzPerScreenPointer_WithoutThrottlingOperationalEvents()
    {
        using var viewModel = new MainViewModel(Configuration("unused"), new SilentRuntime());

        viewModel.ReceiveLogForTest("[front/f1] connection running");
        viewModel.ReceiveLogForTest("[front/f1] error: simulated disconnect");
        viewModel.ReceiveLogForTest("[front/P7] Down");
        viewModel.ReceiveLogForTest("[front/P7] Move x=1");
        viewModel.ReceiveLogForTest("[front/P7] Move x=2");
        viewModel.ReceiveLogForTest("[front/P8] Move x=3");
        viewModel.ReceiveLogForTest("[left/P7] Move x=4");
        viewModel.ReceiveLogForTest("[front/P7] Up");
        viewModel.ReceiveLogForTest("[front/FUSION] Configuration applied.");

        Assert.Equal(1, viewModel.VisibleLogEntries.Count(entry => entry.StartsWith("[front/P7] Move", StringComparison.Ordinal)));
        Assert.Contains("[front/f1] connection running", viewModel.VisibleLogEntries);
        Assert.Contains("[front/f1] error: simulated disconnect", viewModel.VisibleLogEntries);
        Assert.Contains("[front/P7] Down", viewModel.VisibleLogEntries);
        Assert.Contains("[front/P7] Up", viewModel.VisibleLogEntries);
        Assert.Contains("[front/FUSION] Configuration applied.", viewModel.VisibleLogEntries);
        Assert.Contains("[front/P8] Move x=3", viewModel.VisibleLogEntries);
        Assert.Contains("[left/P7] Move x=4", viewModel.VisibleLogEntries);

        await Task.Delay(110);
        viewModel.ReceiveLogForTest("[front/P7] Move x=5");
        Assert.Equal(2, viewModel.VisibleLogEntries.Count(entry => entry.StartsWith("[front/P7] Move", StringComparison.Ordinal)));
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
        public Task StartRecordingAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopRecordingAsync() => Task.CompletedTask;
        public Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PauseReplay() { }
        public void ResumeReplay() { }
        public void StepReplay() { }
        public Task StopReplayAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SilentRuntime : IRadarBridgeRuntime
    {
        public RadarAppConfiguration Configuration { get; } = Configuration("unused");
        public UnityClientStatus UnityStatus => UnityClientStatus.Disconnected;
        public event Action<RadarSensorRuntimeSnapshot>? SensorSnapshotUpdated { add { } remove { } }
        public event Action<RadarScreenRuntimeSnapshot>? ScreenSnapshotUpdated { add { } remove { } }
        public event Action<RadarSensorRuntimeStateChanged>? SensorStateChanged { add { } remove { } }
        public event Action<string>? LogReceived { add { } remove { } }
        public event Action<UnityClientStatus>? UnityStatusChanged { add { } remove { } }
        public Task StartInfrastructureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyUnityTopologyAsync(HelloPayload hello, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectSensorAsync(string screenId, string sensorId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectSensorAsync(string screenId, string sensorId) => Task.CompletedTask;
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
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
