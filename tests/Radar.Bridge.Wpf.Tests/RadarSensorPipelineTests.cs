using Microsoft.Extensions.Logging.Abstractions;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;
using ConfigurationPixelRect = Yuexin.Radar.Configuration.RadarPixelRect;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarSensorPipelineTests
{
    [Fact]
    public async Task Factory_CreatesPipelineThroughInjectableInterface()
    {
        var (screen, sensor) = CreateConfiguration("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        IRadarSensorPipelineFactory factory = new RadarSensorPipelineFactory(NullLoggerFactory.Instance);

        await using var pipeline = factory.Create(screen, sensor);

        Assert.IsAssignableFrom<IRadarSensorPipeline>(pipeline);
    }

    [Fact]
    public async Task TwoSimulationPipelines_RunAndStopIndependently()
    {
        await using var first = CreatePipeline("front", "f1", new ConfigurationPixelRect(0, 0, 2048, 1536));
        await using var second = CreatePipeline("front", "f2", new ConfigurationPixelRect(2048, 0, 2048, 1536));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var firstFrame = new TaskCompletionSource<SensorDetectionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFrame = new TaskCompletionSource<SensorDetectionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFrameCount = 0;
        first.DetectionFrameUpdated += value => firstFrame.TrySetResult(value);
        second.DetectionFrameUpdated += value =>
        {
            Interlocked.Increment(ref secondFrameCount);
            secondFrame.TrySetResult(value);
        };

        await first.StartAsync(cancellation.Token);
        await second.StartAsync(cancellation.Token);
        await firstFrame.Task.WaitAsync(cancellation.Token);
        await secondFrame.Task.WaitAsync(cancellation.Token);
        var countBeforeFirstStops = Volatile.Read(ref secondFrameCount);

        await first.StopAsync();
        await WaitUntilAsync(
            () => Volatile.Read(ref secondFrameCount) > countBeforeFirstStops,
            cancellation.Token);

        Assert.Equal(RadarSensorRuntimeState.Stopped, first.State);
        Assert.Equal(RadarSensorRuntimeState.Running, second.State);
    }

    [Fact]
    public async Task ProcessingChannel_DropsOldFramesInsteadOfAccumulatingLatency()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var latestSnapshot = new TaskCompletionSource<RadarSensorRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.SnapshotUpdated += snapshot =>
        {
            if (snapshot.Sequence == 3)
            {
                latestSnapshot.TrySetResult(snapshot);
            }
        };

        pipeline.PublishScan(CreateFrame(1));
        pipeline.PublishScan(CreateFrame(2));
        pipeline.PublishScan(CreateFrame(3));
        await pipeline.StartAsync(cancellation.Token);
        var snapshot = await latestSnapshot.Task.WaitAsync(cancellation.Token);

        Assert.Equal(3, snapshot.Sequence);
        Assert.True(pipeline.DroppedInputFrameCount >= 2);
    }

    [Fact]
    public async Task StopReplayAsync_DoesNotStopASimulationPipeline()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<SensorDetectionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.DetectionFrameUpdated += frame => received.TrySetResult(frame);

        await pipeline.StartAsync(cancellation.Token);
        await received.Task.WaitAsync(cancellation.Token);
        await pipeline.StopReplayAsync();

        Assert.Equal(RadarSensorRuntimeState.Running, pipeline.State);
    }

    [Fact]
    public async Task ReplayActiveSource_RejectsRecordingEvenWhenConfiguredAsReal()
    {
        var (screen, sensor) = CreateConfiguration("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        sensor.SourceMode = RadarSensorSourceMode.Real;
        var replayPath = await CreateRecordingAsync();
        var recordingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".radarrec");
        await using var pipeline = CreatePipeline(screen, sensor);

        await pipeline.ReplayAsync(replayPath, 1d, loop: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.StartRecordingAsync(recordingPath));
        Assert.False(File.Exists(recordingPath));
    }

    [Fact]
    public async Task Snapshots_UseStrictlyIncreasingPipelineSequenceForDuplicateSourceFrames()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(100, 50, 300, 200));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var snapshots = new List<RadarSensorRuntimeSnapshot>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.SnapshotUpdated += snapshot =>
        {
            if (snapshot.RawPoints.Count == 2)
            {
                snapshots.Add(snapshot);
                if (snapshots.Count == 2) received.TrySetResult();
            }
        };

        await pipeline.StartAsync(cancellation.Token);
        var timestamp = DateTimeOffset.UtcNow;
        pipeline.PublishScan(CreateFrame(7, timestamp));
        await WaitUntilAsync(() => snapshots.Count == 1, cancellation.Token);
        pipeline.PublishScan(CreateFrame(7, timestamp));
        await received.Task.WaitAsync(cancellation.Token);

        Assert.True(snapshots[1].Sequence > snapshots[0].Sequence);
        Assert.True(snapshots[1].Timestamp >= snapshots[0].Timestamp);
        var detection = Assert.Single(snapshots[0].Detections);
        Assert.InRange(detection.PixelX, 100f, 400f);
        Assert.InRange(detection.PixelY, 50f, 250f);
    }

    [Fact]
    public async Task RealSource_RemainsNonRunningUntilConnected()
    {
        var (screen, sensor) = CreateConfiguration("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        sensor.SourceMode = RadarSensorSourceMode.Real;
        sensor.Device.RadarIp = "127.0.0.1";
        sensor.Device.Port = 1;
        await using var pipeline = CreatePipeline(screen, sensor);

        await pipeline.StartAsync();

        Assert.NotEqual(RadarSensorRuntimeState.Running, pipeline.State);
    }

    [Fact]
    public async Task ProcessingFault_OnlyFaultsItsOwnPipelineAndEndsWithEmptySnapshot()
    {
        await using var broken = CreatePipeline("main", "broken", new ConfigurationPixelRect(0, 0, 1920, 1080));
        await using var healthy = CreatePipeline("main", "healthy", new ConfigurationPixelRect(0, 0, 1920, 1080));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var final = new TaskCompletionSource<RadarSensorRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        broken.SnapshotUpdated += snapshot =>
        {
            if (snapshot.RawPoints.Count == 0) final.TrySetResult(snapshot);
        };

        await broken.StartAsync(cancellation.Token);
        await healthy.StartAsync(cancellation.Token);
        broken.PublishScan(new RadarScanFrame(99, DateTimeOffset.UtcNow, null!));
        var terminal = await final.Task.WaitAsync(cancellation.Token);

        Assert.Equal(RadarSensorRuntimeState.Faulted, broken.State);
        Assert.Empty(terminal.Detections);
        Assert.Equal(RadarSensorRuntimeState.Running, healthy.State);
    }

    [Fact]
    public async Task ReplayControls_AreNoOpsForSimulationAndDoNotLogReplayActions()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        var logs = new List<string>();
        pipeline.LogReceived += logs.Add;
        await pipeline.StartAsync();
        logs.Clear();

        pipeline.PauseReplay();
        pipeline.ResumeReplay();
        pipeline.StepReplay();

        Assert.Empty(logs);
        Assert.Equal(RadarSensorRuntimeState.Running, pipeline.State);
    }

    [Fact]
    public async Task ReplayEof_PublishesEmptySnapshotAfterAnyQueuedFrame()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var snapshots = new List<RadarSensorRuntimeSnapshot>();
        pipeline.SnapshotUpdated += snapshots.Add;
        var replayPath = await CreateRecordingAsync();

        await pipeline.ReplayAsync(replayPath, 1d, loop: true, cancellation.Token);
        pipeline.PublishScan(CreateFrame(11));
        await WaitUntilAsync(() => snapshots.Any(snapshot => snapshot.RawPoints.Count == 2), cancellation.Token);
        await pipeline.StopReplayAsync();

        Assert.Empty(snapshots[^1].RawPoints);
        Assert.Empty(snapshots[^1].Detections);
    }

    [Fact]
    public async Task Lifecycle_IsIdempotentAndSnapshotsAreImmutable()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var snapshot = new TaskCompletionSource<RadarSensorRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.SnapshotUpdated += value => snapshot.TrySetResult(value);

        await Task.WhenAll(pipeline.StartAsync(cancellation.Token), pipeline.StartAsync(cancellation.Token));
        var value = await snapshot.Task.WaitAsync(cancellation.Token);
        await pipeline.StopAsync();
        await pipeline.StopAsync();

        Assert.Throws<NotSupportedException>(() => ((IList<RadarPoint>)value.RawPoints).Add(default));
    }

    private static RadarSensorPipeline CreatePipeline(string screenId, string sensorId, ConfigurationPixelRect outputRect)
    {
        var (screen, sensor) = CreateConfiguration(screenId, sensorId, outputRect);
        return CreatePipeline(screen, sensor);
    }

    private static RadarSensorPipeline CreatePipeline(RadarScreenConfiguration screen, RadarSensorConfiguration sensor) =>
        (RadarSensorPipeline)new RadarSensorPipelineFactory(NullLoggerFactory.Instance).Create(screen, sensor);

    private static (RadarScreenConfiguration Screen, RadarSensorConfiguration Sensor) CreateConfiguration(
        string screenId,
        string sensorId,
        ConfigurationPixelRect outputRect)
    {
        var screen = new RadarScreenConfiguration
        {
            ScreenId = screenId,
            ResolutionMode = RadarResolutionMode.Override,
            WidthPixels = 4096,
            HeightPixels = 1536,
            Sensors = []
        };
        var sensor = new RadarSensorConfiguration
        {
            SensorId = sensorId,
            SourceMode = RadarSensorSourceMode.Simulation,
            OutputRectPixels = outputRect,
            Range = new RadarRangeConfiguration
            {
                ActivePolygon =
                [
                    new(-5f, -5f),
                    new(5f, -5f),
                    new(5f, 5f),
                    new(-5f, 5f)
                ]
            }
        };

        return (screen, sensor);
    }

    private static RadarScanFrame CreateFrame(long sequence, DateTimeOffset? timestamp = null) => new(
        sequence,
        timestamp ?? DateTimeOffset.UtcNow,
        [
            new RadarPoint(180, 6000, 60f, 1.5f, 1.5f),
            new RadarPoint(182, 6001, 60.01f, 1.51f, 1.51f)
        ]);

    private static async Task<string> CreateRecordingAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".radarrec");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new Yuexin.Radar.Device.RadarRecordingWriter(stream, leaveOpen: true);
        await writer.InitializeAsync(new Yuexin.Radar.Device.RadarRecordingHeader(
            RadarModel.F10, "{}", null, DateTimeOffset.UtcNow));
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
