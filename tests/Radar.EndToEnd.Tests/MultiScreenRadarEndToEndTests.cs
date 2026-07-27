using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;
using Yuexin.Radar.Processing;
using Xunit.Abstractions;

namespace Yuexin.Radar.EndToEnd.Tests;

public sealed class MultiScreenRadarEndToEndTests
{
    private static readonly TimeSpan AcceptanceTimeout = TimeSpan.FromSeconds(5);
    private readonly ITestOutputHelper _output;

    public MultiScreenRadarEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task ThreeScreensFourSensors_PublishMergedFrontPointerAndIsolateSensorFailure()
    {
        var pipeName = "RadarControl.E2E." + Guid.NewGuid().ToString("N");
        var configuration = ThreeScreenFourSensorConfiguration(pipeName);
        var factory = new ControlledSensorPipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.StartInfrastructureAsync();
        await using var client = await ConnectV2ClientAsync(pipeName, ThreeScreenHello());
        await coordinator.ConnectAllAsync();

        factory.Publish("front", "f1", Detection("f1", 1900, 700));
        factory.Publish("front", "f2", Detection("f2", 1940, 710));
        factory.Fail("left", "l1", new IOException("simulated disconnect"));

        var batch = await ReadNextBatchAsync(
            client,
            value => value.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers.Count > 0,
            AcceptanceTimeout);

        Assert.Equal(3, batch.Screens.Count);
        Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Empty(batch.Screens.Single(frame => frame.Screen.ScreenId == "left").Pointers);
        Assert.Equal(RadarSensorRuntimeState.Running, factory.Get("right", "r1").State);
        Assert.Equal(RadarSensorRuntimeState.Faulted, factory.Get("left", "l1").State);
    }

    [Fact]
    public async Task HotResolutionAndOutputRectangleChange_PublishesOneUpThenZeroAndWaitsForFreshDetections()
    {
        var pipeName = "RadarControl.E2E." + Guid.NewGuid().ToString("N");
        var configuration = ThreeScreenFourSensorConfiguration(pipeName);
        var factory = new ControlledSensorPipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.StartInfrastructureAsync();
        await using var client = await ConnectV2ClientAsync(pipeName, ThreeScreenHello());
        await coordinator.ConnectAllAsync();

        factory.Publish("front", "f1", Detection("f1", 1900, 700));
        factory.Publish("front", "f2", Detection("f2", 1940, 710));
        var downBatch = await ReadNextBatchAsync(client, HasFrontPhase(RadarPointerPhase.Down), AcceptanceTimeout);
        var down = Assert.Single(Front(downBatch).Pointers);
        var moveBatch = await ReadNextBatchAsync(client, HasFrontPhase(RadarPointerPhase.Move), AcceptanceTimeout);
        Assert.Equal(down.PointerId, Assert.Single(Front(moveBatch).Pointers).PointerId);

        var front = configuration.Screens.Single(screen => screen.ScreenId == "front");
        front.WidthPixels = 3840;
        front.HeightPixels = 1440;
        front.Sensors.Single(sensor => sensor.SensorId == "f1").OutputRectPixels = new Yuexin.Radar.Configuration.RadarPixelRect(0, 0, 2100, 1440);
        front.Sensors.Single(sensor => sensor.SensorId == "f2").OutputRectPixels = new Yuexin.Radar.Configuration.RadarPixelRect(1740, 0, 2100, 1440);
        await coordinator.ApplyConfigurationAsync();

        var transitionBatches = new List<PointerBatchPayload>();
        while (true)
        {
            var batch = await ReadNextBatchAsync(client, _ => true, AcceptanceTimeout);
            transitionBatches.Add(batch);
            var frame = Front(batch);
            var sawUp = transitionBatches.SelectMany(value => Front(value).Pointers).Any(pointer => pointer.Phase == RadarPointerPhase.Up);
            if (sawUp && frame.Pointers.Count == 0 && frame.Screen.WidthPixels == 4096) break;
        }

        var frontPointers = transitionBatches.SelectMany(value => Front(value).Pointers).ToArray();
        var up = Assert.Single(frontPointers.Where(pointer => pointer.Phase == RadarPointerPhase.Up));
        Assert.Equal(down.PointerId, up.PointerId);
        var upBatchIndex = transitionBatches.FindIndex(value => Front(value).Pointers.Any(pointer => pointer.Phase == RadarPointerPhase.Up));
        Assert.True(upBatchIndex >= 0 && upBatchIndex + 1 < transitionBatches.Count);
        Assert.Empty(Front(transitionBatches[upBatchIndex + 1]).Pointers);

        var settled = await ReadNextBatchAsync(
            client,
            value => Front(value).Screen.WidthPixels == 3840,
            AcceptanceTimeout);
        Assert.Empty(Front(settled).Pointers);
        Assert.Equal(new Yuexin.Radar.Configuration.RadarPixelRect(0, 0, 2100, 1440), factory.Get("front", "f1").OutputRectPixels);
        Assert.Equal(new Yuexin.Radar.Configuration.RadarPixelRect(1740, 0, 2100, 1440), factory.Get("front", "f2").OutputRectPixels);

        factory.Publish("front", "f1", Detection("f1", 1800, 650));
        factory.Publish("front", "f2", Detection("f2", 1840, 660));
        var fresh = await ReadNextBatchAsync(client, HasFrontPhase(RadarPointerPhase.Down), AcceptanceTimeout);
        var freshPointer = Assert.Single(Front(fresh).Pointers);
        Assert.Equal(RadarPointerPhase.Down, freshPointer.Phase);
        Assert.DoesNotContain(
            transitionBatches.Skip(upBatchIndex + 1).SelectMany(value => Front(value).Pointers),
            pointer => pointer.Phase is RadarPointerPhase.Down or RadarPointerPhase.Move);
    }

    [Theory]
    [InlineData(30)]
    public async Task ThreeScreensFourSensors_ThirtyHertzSoakRemainsBoundedDuringFrontSensorReconnects(int durationSeconds)
    {
        const long memoryLimitBytes = 64L * 1024L * 1024L;
        var pipeName = "RadarControl.Soak." + Guid.NewGuid().ToString("N");
        var configuration = ThreeScreenFourSensorConfiguration(pipeName);
        var factory = new ControlledSensorPipelineFactory();
        var coordinator = CreateCoordinator(configuration, factory);
        NamedPipeClientStream? client = null;
        var unobserved = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> unobservedHandler = (_, args) =>
        {
            unobserved.Enqueue(args.Exception.GetBaseException());
            args.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += unobservedHandler;

        var observations = new SoakObservations();
        var reconnectCount = 0;
        var publishedFrames = 0;
        var memoryAfterWarmup = 0L;
        var memoryAtCompletion = 0L;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await coordinator.StartInfrastructureAsync();
            client = await ConnectV2ClientAsync(pipeName, ThreeScreenHello());
            await coordinator.ConnectAllAsync();
            using var duration = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));
            var reader = ReadSoakBatchesAsync(client, observations, duration.Token);
            var heartbeat = SendHeartbeatsAsync(client, duration.Token);
            using var frameTimer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 30d));
            var nextReconnect = TimeSpan.FromSeconds(5);

            try
            {
                while (await frameTimer.WaitForNextTickAsync(duration.Token))
                {
                    var elapsed = stopwatch.Elapsed;
                    if (memoryAfterWarmup == 0 && elapsed >= TimeSpan.FromSeconds(5))
                    {
                        memoryAfterWarmup = GC.GetTotalMemory(forceFullCollection: true);
                    }

                    if (elapsed >= nextReconnect && nextReconnect < TimeSpan.FromSeconds(durationSeconds))
                    {
                        await coordinator.DisconnectSensorAsync("front", "f1");
                        await coordinator.ConnectSensorAsync("front", "f1", duration.Token);
                        reconnectCount++;
                        nextReconnect += TimeSpan.FromSeconds(5);
                    }

                    var timestamp = DateTimeOffset.UtcNow;
                    factory.Publish("left", "l1", Detection("l1", 500, 600, timestamp));
                    factory.Publish("front", "f1", Detection("f1", 1900, 700, timestamp));
                    factory.Publish("front", "f2", Detection("f2", 1940, 710, timestamp));
                    factory.Publish("right", "r1", Detection("r1", 600, 500, timestamp));
                    publishedFrames++;
                }
            }
            catch (OperationCanceledException) when (duration.IsCancellationRequested)
            {
            }

            await IgnoreExpectedCancellationAsync(reader, duration.Token);
            await IgnoreExpectedCancellationAsync(heartbeat, duration.Token);
            memoryAtCompletion = GC.GetTotalMemory(forceFullCollection: true);
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            await coordinator.DisposeAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TaskScheduler.UnobservedTaskException -= unobservedHandler;
        }

        stopwatch.Stop();
        var memoryGrowth = memoryAtCompletion - memoryAfterWarmup;
        _output.WriteLine(
            "duration={0:F3}s publishedFrames={1} batches={2} reconnects={3} firstSequence={4} lastSequence={5} memoryWarmupBytes={6} memoryEndBytes={7} memoryGrowthBytes={8} unobservedExceptions={9}",
            stopwatch.Elapsed.TotalSeconds,
            publishedFrames,
            observations.BatchCount,
            reconnectCount,
            observations.FirstSequence,
            observations.LastSequence,
            memoryAfterWarmup,
            memoryAtCompletion,
            memoryGrowth,
            unobserved.Count);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(durationSeconds));
        Assert.InRange(publishedFrames, durationSeconds * 25, durationSeconds * 35);
        Assert.True(observations.BatchCount > durationSeconds * 20);
        Assert.Equal((durationSeconds - 1) / 5, reconnectCount);
        Assert.True(memoryAfterWarmup > 0);
        Assert.True(memoryGrowth < memoryLimitBytes, $"Managed-memory growth was {memoryGrowth} bytes; limit is {memoryLimitBytes} bytes.");
        Assert.Empty(unobserved);
    }

    private static RadarBridgeCoordinator CreateCoordinator(
        RadarAppConfiguration configuration,
        ControlledSensorPipelineFactory factory) =>
        new(configuration, NullLogger<RadarBridgeCoordinator>.Instance, factory);

    private static async Task<NamedPipeClientStream> ConnectV2ClientAsync(string pipeName, HelloPayload hello)
    {
        using var cancellation = new CancellationTokenSource(AcceptanceTimeout);
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(cancellation.Token);
            await IpcStream.WriteAsync(
                client,
                IpcEnvelope.Create(IpcMessageType.Hello, 1, hello),
                cancellation.Token);
            var acknowledgement = await IpcStream.ReadAsync(client, cancellation.Token);
            Assert.Equal(IpcMessageType.HelloAck, acknowledgement.MessageType);
            var payload = acknowledgement.DeserializePayload<HelloAckPayload>();
            Assert.Equal(2, payload.ProtocolVersion);
            Assert.Equal(["left", "front", "right"], payload.Screens.Select(screen => screen.ScreenId));
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    private static async Task<PointerBatchPayload> ReadNextBatchAsync(
        Stream stream,
        Func<PointerBatchPayload, bool> predicate,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            var envelope = await IpcStream.ReadAsync(stream, cancellation.Token);
            if (envelope.MessageType != IpcMessageType.PointerBatch) continue;
            var batch = envelope.DeserializePayload<PointerBatchPayload>();
            if (predicate(batch)) return batch;
        }
    }

    private static Func<PointerBatchPayload, bool> HasFrontPhase(RadarPointerPhase phase) =>
        batch => Front(batch).Pointers.Any(pointer => pointer.Phase == phase);

    private static RadarScreenPointerFrame Front(PointerBatchPayload batch) =>
        batch.Screens.Single(frame => frame.Screen.ScreenId == "front");

    private static HelloPayload ThreeScreenHello() => new(
        Environment.ProcessId,
        "2021.3.45f1",
        [
            new RadarScreenDefinitionPayload("left", "Left", 1920, 1440, false, 0),
            new RadarScreenDefinitionPayload("front", "Front", 4096, 1536, true, 1),
            new RadarScreenDefinitionPayload("right", "Right", 1920, 1440, false, 2)
        ]);

    private static RadarAppConfiguration ThreeScreenFourSensorConfiguration(string pipeName) => new()
    {
        Ipc = new RadarIpcConfiguration { PipeName = pipeName },
        Screens =
        [
            Screen("left", 1920, 1440, Sensor("l1", new Yuexin.Radar.Configuration.RadarPixelRect(0, 0, 1920, 1440))),
            Screen(
                "front",
                4096,
                1536,
                Sensor("f1", new Yuexin.Radar.Configuration.RadarPixelRect(0, 0, 2300, 1536)),
                Sensor("f2", new Yuexin.Radar.Configuration.RadarPixelRect(1800, 0, 2296, 1536))),
            Screen("right", 1920, 1440, Sensor("r1", new Yuexin.Radar.Configuration.RadarPixelRect(0, 0, 1920, 1440)))
        ]
    };

    private static RadarScreenConfiguration Screen(
        string screenId,
        int width,
        int height,
        params RadarSensorConfiguration[] sensors) => new()
    {
        ScreenId = screenId,
        UnityDisplayName = screenId,
        IsPrimary = screenId == "front",
        UnityOrder = screenId == "left" ? 0 : screenId == "front" ? 1 : 2,
        ResolutionMode = RadarResolutionMode.Override,
        WidthPixels = width,
        HeightPixels = height,
        Fusion = new RadarFusionConfiguration
        {
            OutputRateHz = 30,
            SensorDataMaxAgeMilliseconds = 250,
            FusionDistancePixels = 80
        },
        Tracking = new RadarScreenTrackingConfiguration
        {
            ConfirmFrames = 1,
            LostFrames = 1,
            MaximumAssociationDistancePixels = 160,
            SmoothingAlpha = 0.5f
        },
        Sensors = sensors.ToList()
    };

    private static RadarSensorConfiguration Sensor(
        string sensorId,
        Yuexin.Radar.Configuration.RadarPixelRect outputRect) => new()
    {
        SensorId = sensorId,
        DisplayName = sensorId,
        Enabled = true,
        SourceMode = RadarSensorSourceMode.Simulation,
        OutputRectPixels = outputRect
    };

    private static SensorDetectionFrame Detection(string sensorId, float x, float y) =>
        Detection(sensorId, x, y, DateTimeOffset.UtcNow);

    private static SensorDetectionFrame Detection(string sensorId, float x, float y, DateTimeOffset timestamp) =>
        new(sensorId, timestamp, [new SensorDetection(1, x, y, 1f)]);

    private static async Task SendHeartbeatsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var sequence = 1L;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await IpcStream.WriteAsync(
                    stream,
                    IpcEnvelope.Create(
                        IpcMessageType.Ping,
                        Interlocked.Increment(ref sequence),
                        new PingPayload(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task ReadSoakBatchesAsync(
        Stream stream,
        SoakObservations observations,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await IpcStream.ReadAsync(stream, cancellationToken);
                if (envelope.MessageType != IpcMessageType.PointerBatch) continue;
                var batch = envelope.DeserializePayload<PointerBatchPayload>();
                Assert.Equal(3, batch.Screens.Count);
                var sequence = Assert.Single(batch.Screens.Select(frame => frame.Sequence).Distinct());
                Assert.True(sequence > observations.LastSequence, $"IPC sequence {sequence} did not follow {observations.LastSequence}.");
                if (observations.BatchCount == 0) observations.FirstSequence = sequence;
                observations.LastSequence = sequence;
                observations.BatchCount++;

                var keys = new HashSet<(string ScreenId, int PointerId)>();
                foreach (var frame in batch.Screens)
                {
                    foreach (var pointer in frame.Pointers)
                    {
                        Assert.True(keys.Add((frame.Screen.ScreenId, pointer.PointerId)),
                            $"Duplicate pointer key ({frame.Screen.ScreenId}, {pointer.PointerId}) in IPC sequence {sequence}.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task IgnoreExpectedCancellationAsync(Task task, CancellationToken cancellationToken)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed class SoakObservations
    {
        public int BatchCount { get; set; }
        public long FirstSequence { get; set; }
        public long LastSequence { get; set; }
    }

    private sealed class ControlledSensorPipelineFactory : IRadarSensorPipelineFactory
    {
        private readonly ConcurrentDictionary<(string ScreenId, string SensorId), ControlledSensorPipeline> _current = new();

        public IRadarSensorPipeline Create(RadarScreenConfiguration screen, RadarSensorConfiguration sensor)
        {
            var pipeline = new ControlledSensorPipeline(screen, sensor);
            _current[(screen.ScreenId, sensor.SensorId)] = pipeline;
            return pipeline;
        }

        public ControlledSensorPipeline Get(string screenId, string sensorId) => _current[(screenId, sensorId)];

        public void Publish(string screenId, string sensorId, SensorDetectionFrame frame) =>
            Get(screenId, sensorId).Publish(frame);

        public void Fail(string screenId, string sensorId, Exception error) =>
            Get(screenId, sensorId).Fail(error);
    }

    private sealed class ControlledSensorPipeline : IRadarSensorPipeline
    {
        private int _state = (int)RadarSensorRuntimeState.Stopped;
        private int _disposed;

        public ControlledSensorPipeline(RadarScreenConfiguration screen, RadarSensorConfiguration sensor)
        {
            ScreenId = screen.ScreenId;
            SensorId = sensor.SensorId;
            OutputRectPixels = sensor.OutputRectPixels;
        }

        public string ScreenId { get; }
        public string SensorId { get; }
        public Yuexin.Radar.Configuration.RadarPixelRect OutputRectPixels { get; }
        public RadarSensorRuntimeState State => (RadarSensorRuntimeState)Volatile.Read(ref _state);
        public long DroppedInputFrameCount => 0;
        public event Action<RadarSensorRuntimeSnapshot>? SnapshotUpdated { add { } remove { } }
        public event Action<SensorDetectionFrame>? DetectionFrameUpdated;
        public event Action<RadarSensorRuntimeState>? StateChanged;
        public event Action<string>? LogReceived;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            SetState(RadarSensorRuntimeState.Running);
            LogReceived?.Invoke("connection running");
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            if (Volatile.Read(ref _disposed) == 0) SetState(RadarSensorRuntimeState.Stopped);
            return Task.CompletedTask;
        }

        public void Publish(SensorDetectionFrame frame)
        {
            ThrowIfDisposed();
            DetectionFrameUpdated?.Invoke(frame);
        }

        public void Fail(Exception error)
        {
            ArgumentNullException.ThrowIfNull(error);
            ThrowIfDisposed();
            SetState(RadarSensorRuntimeState.Faulted);
            LogReceived?.Invoke($"error: {error.Message}");
        }

        public Task StartRecordingAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopRecordingAsync() => Task.CompletedTask;
        public Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PauseReplay() { }
        public void ResumeReplay() { }
        public void StepReplay() { }
        public Task StopReplayAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Volatile.Write(ref _state, (int)RadarSensorRuntimeState.Stopped);
            return ValueTask.CompletedTask;
        }

        private void SetState(RadarSensorRuntimeState state)
        {
            Volatile.Write(ref _state, (int)state);
            StateChanged?.Invoke(state);
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
