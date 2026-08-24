using Microsoft.Extensions.Logging.Abstractions;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;
using Yuexin.Radar.Processing;
using System.Text.Json;
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
    public async Task SimulationFootprints_FormClosedContoursAroundEveryDetection()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<SensorDetectionFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.DetectionFrameUpdated += frame =>
        {
            if (frame.Detections.Count == 2)
            {
                received.TrySetResult(frame);
            }
        };

        await pipeline.StartAsync(cancellation.Token);
        var frame = await received.Task.WaitAsync(cancellation.Token);

        Assert.Equal([12, 16], frame.Detections.Select(item => item.Footprint.Count).Order().ToArray());
        Assert.All(frame.Detections, detection =>
        {
            Assert.True(detection.Footprint.Min(point => point.PixelX) < detection.PixelX);
            Assert.True(detection.Footprint.Max(point => point.PixelX) > detection.PixelX);
            Assert.True(detection.Footprint.Min(point => point.PixelY) < detection.PixelY);
            Assert.True(detection.Footprint.Max(point => point.PixelY) > detection.PixelY);
        });
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
    public async Task Mapping_MapsEveryActualClusterPointInAcquisitionOrderAndPreservesDuplicates()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1000, 1000));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<RadarSensorRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.SnapshotUpdated += snapshot =>
        {
            if (snapshot.RawPoints.Count == 3) received.TrySetResult(snapshot);
        };

        await pipeline.StartAsync(cancellation.Token);
        pipeline.PublishScan(new RadarScanFrame(
            71,
            DateTimeOffset.UtcNow,
            [
                new RadarPoint(500, 6000, 60f, -4f, 3f),
                new RadarPoint(500, 6001, 60.01f, -3.9f, 2.9f),
                new RadarPoint(500, 6001, 60.01f, -3.9f, 2.9f)
            ]));
        var snapshot = await received.Task.WaitAsync(cancellation.Token);

        var detection = Assert.Single(snapshot.Detections);
        Assert.Equal(110f, detection.PixelX, 3);
        Assert.Equal(210f, detection.PixelY, 3);
        Assert.Equal(3, detection.Footprint.Count);
        Assert.Equal(100f, detection.Footprint[0].PixelX, 3);
        Assert.Equal(200f, detection.Footprint[0].PixelY, 3);
        Assert.Equal(110f, detection.Footprint[1].PixelX, 3);
        Assert.Equal(210f, detection.Footprint[1].PixelY, 3);
        Assert.Equal(detection.Footprint[1], detection.Footprint[2]);
    }

    [Fact]
    public async Task Mapping_DoesNotInsertASyntheticClusterCenterIntoTheFootprint()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1000, 1000));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<RadarSensorRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.SnapshotUpdated += snapshot =>
        {
            if (snapshot.RawPoints.Count == 2) received.TrySetResult(snapshot);
        };

        await pipeline.StartAsync(cancellation.Token);
        pipeline.PublishScan(new RadarScanFrame(
            72,
            DateTimeOffset.UtcNow,
            [
                new RadarPoint(500, 6000, 60f, -4f, 3f),
                new RadarPoint(500, 6001, 60.01f, -3.9f, 2.9f)
            ]));
        var snapshot = await received.Task.WaitAsync(cancellation.Token);

        var detection = Assert.Single(snapshot.Detections);
        Assert.Equal(105f, detection.PixelX, 3);
        Assert.Equal(205f, detection.PixelY, 3);
        Assert.Collection(
            detection.Footprint,
            point =>
            {
                Assert.Equal(100f, point.PixelX, 3);
                Assert.Equal(200f, point.PixelY, 3);
            },
            point =>
            {
                Assert.Equal(110f, point.PixelX, 3);
                Assert.Equal(210f, point.PixelY, 3);
            });
        Assert.DoesNotContain(new RadarScreenPoint(detection.PixelX, detection.PixelY), detection.Footprint);
    }

    [Fact]
    public async Task Mapping_RejectsTheEntireDetectionWithOneDiagnosticWhenAnyActualPointIsUnmappable()
    {
        var (screen, sensor) = CreateConfiguration("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1000, 1000));
        sensor.Range.ActivePolygon = [];
        sensor.Calibration = new RadarCalibrationConfiguration
        {
            IsValid = true,
            PhysicalCorners = [new(-5f, -5f), new(5f, -5f), new(5f, 5f), new(-5f, 5f)]
        };
        await using var pipeline = CreatePipeline(screen, sensor);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new List<string>();
        var received = new TaskCompletionSource<RadarSensorRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.LogReceived += logs.Add;
        pipeline.SnapshotUpdated += snapshot =>
        {
            if (snapshot.RawPoints.Count == 3) received.TrySetResult(snapshot);
        };

        await pipeline.StartAsync(cancellation.Token);
        pipeline.PublishScan(new RadarScanFrame(
            73,
            DateTimeOffset.UtcNow,
            [
                new RadarPoint(500, 6000, 60f, -4f, -3f),
                new RadarPoint(500, 6001, 60.01f, -3.9f, -2.9f),
                new RadarPoint(500, 6002, 60.02f, float.NaN, -3f)
            ]));
        var snapshot = await received.Task.WaitAsync(cancellation.Token);

        Assert.Empty(snapshot.Detections);
        Assert.Single(logs.Where(message => message.Contains("footprint", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Mapping_EmitsOneDiagnosticWhenInvalidActualPointsAlsoMakeTheCenterUnmappable()
    {
        var (screen, sensor) = CreateConfiguration("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1000, 1000));
        sensor.Range.ActivePolygon = [];
        sensor.Calibration = new RadarCalibrationConfiguration
        {
            IsValid = true,
            PhysicalCorners = [new(-5f, -5f), new(5f, -5f), new(5f, 5f), new(-5f, 5f)]
        };
        await using var pipeline = CreatePipeline(screen, sensor);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var logs = new List<string>();
        var received = new TaskCompletionSource<RadarSensorRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.LogReceived += logs.Add;
        pipeline.SnapshotUpdated += snapshot =>
        {
            if (snapshot.RawPoints.Count == 2) received.TrySetResult(snapshot);
        };

        await pipeline.StartAsync(cancellation.Token);
        pipeline.PublishScan(new RadarScanFrame(
            74,
            DateTimeOffset.UtcNow,
            [
                new RadarPoint(500, 6000, 60f, float.NaN, -3f),
                new RadarPoint(500, 6001, 60.01f, float.NaN, -2.9f)
            ]));
        var snapshot = await received.Task.WaitAsync(cancellation.Token);

        Assert.Empty(snapshot.Detections);
        Assert.Single(logs.Where(message => message.Contains("footprint", StringComparison.OrdinalIgnoreCase)));
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
    public async Task Recording_IsReleasedBeforeReplayTransitionsAwayFromARealSource()
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        var recordingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".radarrec");
        var replayPath = await CreateRecordingAsync();
        try
        {
            SetActiveRealSourceForRecording(pipeline);
            await pipeline.StartRecordingAsync(recordingPath);
            Assert.NotNull(GetRecordingWriter(pipeline));

            await pipeline.ReplayAsync(replayPath, 1d, loop: true);
            await pipeline.StopReplayAsync();

            Assert.Null(GetRecordingWriter(pipeline));
        }
        finally
        {
            File.Delete(recordingPath);
            File.Delete(replayPath);
        }
    }

    [Fact]
    public async Task Recording_IsReleasedBeforeStopAndDisposeComplete()
    {
        var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        var recordingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".radarrec");
        try
        {
            SetActiveRealSourceForRecording(pipeline);
            await pipeline.StartRecordingAsync(recordingPath);
            await pipeline.StopAsync();
            Assert.Null(GetRecordingWriter(pipeline));

            await pipeline.DisposeAsync();
            Assert.Null(GetRecordingWriter(pipeline));
        }
        finally
        {
            await pipeline.DisposeAsync();
            File.Delete(recordingPath);
        }
    }

    [Fact]
    public async Task RecordingHeader_EmbedsTheLegacyDefaultSerializerRuntimeSnapshotExactly()
    {
        var (screen, sensor) = CreateConfiguration("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        sensor.SourceMode = RadarSensorSourceMode.Real;
        sensor.Device.DeviceModel = RadarModel.F20;
        var expectedSnapshot = JsonSerializer.Serialize(sensor);
        var pipeline = CreatePipeline(screen, sensor);
        var recordingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".radarrec");
        try
        {
            SetActiveRealSourceForRecording(pipeline);
            await pipeline.StartRecordingAsync(recordingPath);
            await pipeline.StopRecordingAsync();

            await using var stream = new FileStream(recordingPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var reader = new RadarRecordingReader(stream, leaveOpen: true);
            var header = await reader.ReadHeaderAsync();

            Assert.Equal(expectedSnapshot, header.ConfigurationSnapshotJson);
        }
        finally
        {
            await pipeline.DisposeAsync();
            File.Delete(recordingPath);
        }
    }

    [Fact]
    public async Task SlowRecordingWriter_UsesOneBoundedQueueAndStopDrainsEveryByteInOrder()
    {
        var (screen, sensor) = CreateConfiguration("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        sensor.SourceMode = RadarSensorSourceMode.Real;
        var slowStream = new ControlledSlowStream();
        var pipeline = new RadarSensorPipeline(screen, sensor, NullLogger<RadarSensorPipeline>.Instance, _ => slowStream);
        var recordingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".radarrec");
        try
        {
            SetActiveRealSourceForRecording(pipeline);
            await pipeline.StartRecordingAsync(recordingPath);
            slowStream.BlockWrites();

            var produce = Task.Run(() =>
            {
                for (var index = 0; index < RadarSensorPipeline.RecordingQueueCapacity + 8; index++)
                {
                    pipeline.EnqueueRecordingBytesForTests(new[] { checked((byte)index) });
                }
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => pipeline.PendingRecordingEntryCount == RadarSensorPipeline.RecordingQueueCapacity,
                timeout.Token);
            Assert.False(produce.IsCompleted);
            Assert.Equal(1, pipeline.RecordingWriterTaskStartCount);
            Assert.InRange(pipeline.PendingRecordingEntryCount, 0, RadarSensorPipeline.RecordingQueueCapacity);

            slowStream.ReleaseWrites();
            await produce.WaitAsync(timeout.Token);
            await pipeline.StopRecordingAsync().WaitAsync(timeout.Token);

            Assert.Equal(0, pipeline.PendingRecordingEntryCount);
            Assert.Null(GetRecordingWriter(pipeline));
            var entries = await ReadRawEntriesAsync(slowStream.ToArray(), timeout.Token);
            Assert.Equal(
                Enumerable.Range(0, RadarSensorPipeline.RecordingQueueCapacity + 8).Select(index => (byte)index),
                entries.SelectMany(entry => entry.Payload));
        }
        finally
        {
            slowStream.ReleaseWrites();
            await pipeline.DisposeAsync();
            File.Delete(recordingPath);
        }
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(4.0)]
    [InlineData(4.25)]
    [InlineData(8.0)]
    public async Task Replay_AcceptsEveryFiniteSpeedInApprovedInclusiveRange(double speed)
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        var replayPath = await CreateRecordingAsync();
        try
        {
            await pipeline.ReplayAsync(replayPath, speed, loop: false);
            await pipeline.StopReplayAsync();
        }
        finally
        {
            File.Delete(replayPath);
        }
    }

    [Theory]
    [InlineData(0.099)]
    [InlineData(8.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Replay_RejectsOutOfRangeOrNonFiniteSpeed(double speed)
    {
        await using var pipeline = CreatePipeline("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        var replayPath = await CreateRecordingAsync();
        try
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                pipeline.ReplayAsync(replayPath, speed, loop: false));
        }
        finally
        {
            File.Delete(replayPath);
        }
    }

    [Theory]
    [InlineData(0.1, true)]
    [InlineData(4.0, true)]
    [InlineData(4.25, true)]
    [InlineData(8.0, true)]
    [InlineData(0.099, false)]
    [InlineData(8.001, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void ReplaySpeedEditor_PreservesValueAndReportsApprovedRange(double speed, bool valid)
    {
        var (_, sensor) = CreateConfiguration("main", "sensor-1", new ConfigurationPixelRect(0, 0, 1920, 1080));
        var viewModel = new SensorItemViewModel(sensor);

        viewModel.ReplaySpeed = speed;

        Assert.Equal(speed, viewModel.ReplaySpeed);
        Assert.Equal(valid, string.IsNullOrEmpty(viewModel[nameof(SensorItemViewModel.ReplaySpeed)]));
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

    private static async Task<IReadOnlyList<RadarRecordingEntry>> ReadRawEntriesAsync(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        await using var reader = new RadarRecordingReader(stream, leaveOpen: true);
        await reader.ReadHeaderAsync(cancellationToken);
        var entries = new List<RadarRecordingEntry>();
        await foreach (var entry in reader.ReadEntriesAsync(cancellationToken))
        {
            if (entry.EntryType == RadarRecordingEntryType.RawBytes)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static void SetActiveRealSourceForRecording(RadarSensorPipeline pipeline)
    {
        typeof(RadarSensorPipeline).GetField("_activeSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(pipeline, (int)RadarSensorSourceMode.Real);
        typeof(RadarSensorPipeline).GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(pipeline, (int)RadarSensorRuntimeState.Running);
    }

    private static RadarRecordingWriter? GetRecordingWriter(RadarSensorPipeline pipeline) =>
        (RadarRecordingWriter?)typeof(RadarSensorPipeline)
            .GetField("_recordingWriter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(pipeline);

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class ControlledSlowStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private TaskCompletionSource _writeRelease = Released();

        public void BlockWrites() => Volatile.Write(
            ref _writeRelease,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseWrites() => Volatile.Read(ref _writeRelease).TrySetResult();

        public byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Volatile.Read(ref _writeRelease).Task.WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // Keep the captured bytes readable after the pipeline closes its recording stream.
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TaskCompletionSource Released()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
