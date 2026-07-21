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

    private static RadarSensorPipeline CreatePipeline(string screenId, string sensorId, ConfigurationPixelRect outputRect)
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

        return RadarSensorPipelineFactory.Create(screen, sensor, NullLogger<RadarSensorPipeline>.Instance);
    }

    private static RadarScanFrame CreateFrame(long sequence) => new(
        sequence,
        DateTimeOffset.UtcNow,
        [
            new RadarPoint(180, 6000, 60f, 1.5f, 1.5f),
            new RadarPoint(182, 6001, 60.01f, 1.51f, 1.51f)
        ]);

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(10, cancellationToken);
        }
    }
}
