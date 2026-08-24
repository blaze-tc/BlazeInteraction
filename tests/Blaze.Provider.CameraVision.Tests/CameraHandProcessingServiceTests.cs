using System.Diagnostics;
using System.Runtime.InteropServices;
using Blaze.Interaction.Contracts;
using OpenCvSharp;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraHandProcessingServiceTests
{
    [Fact]
    public async Task Processing_ConvertsBgrToRgbAndKeepsSourceAliveUntilDetectionReturns()
    {
        using var slot = new LatestFrameSlot<CameraFrame>();
        var detectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDetect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeHandDetectionBackend(async (frame, _, cancellationToken) =>
        {
            var rgb = new byte[3];
            Marshal.Copy(frame.Data, rgb, 0, rgb.Length);
            Assert.Equal(new byte[] { 30, 20, 10 }, rgb);
            detectEntered.TrySetResult();
            await releaseDetect.Task.WaitAsync(cancellationToken);
            return HandDetectionResult.Empty;
        });
        await using var service = Service(slot, backend, width: 1, height: 1);
        await service.StartAsync(CancellationToken.None);
        var source = Frame(1, width: 1, height: 1, new Scalar(10, 20, 30));

        slot.Publish(source);
        await detectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, source.Width);
        releaseDetect.TrySetResult();
        await NextFrameAsync(service);
        Assert.Throws<ObjectDisposedException>(() => _ = source.Image);
    }

    [Fact]
    public async Task Processing_TakesOnlyLatestFrameWithoutBacklog()
    {
        using var slot = new LatestFrameSlot<CameraFrame>();
        var first = Frame(1);
        var second = Frame(2);
        var third = Frame(3);
        slot.Publish(first);
        slot.Publish(second);
        slot.Publish(third);
        var backend = new FakeHandDetectionBackend(Result());
        await using var service = Service(slot, backend);

        await service.StartAsync(CancellationToken.None);
        var output = await NextFrameAsync(service);

        Assert.Equal(3, output.SourceSequence);
        Assert.Equal(1, backend.DetectCount);
        Assert.Equal(2, output.DroppedFrameCount);
        Assert.Throws<ObjectDisposedException>(() => _ = first.Image);
        Assert.Throws<ObjectDisposedException>(() => _ = second.Image);
    }

    [Fact]
    public async Task Processing_PreservesAllEightHands()
    {
        using var slot = new LatestFrameSlot<CameraFrame>();
        var hands = Enumerable.Range(0, 8)
            .Select(index => Hand(0.1f + (index * 0.1f), 0.5f))
            .ToArray();
        await using var service = Service(
            slot,
            new FakeHandDetectionBackend(new HandDetectionResult(hands)));
        await service.StartAsync(CancellationToken.None);

        slot.Publish(Frame(1));
        var output = await NextFrameAsync(service);

        Assert.Equal(8, output.ActiveHands.Count);
        Assert.Equal(8, output.ActiveHands.Select(hand => hand.TrackId).Distinct().Count());
    }

    [Fact]
    public async Task Processing_FiltersLowConfidenceAndOutsideTrackingPoints()
    {
        using var slot = new LatestFrameSlot<CameraFrame>();
        var result = new HandDetectionResult(new[]
        {
            Hand(0.25f, 0.5f, confidence: 0.49f),
            Hand(1.25f, 0.5f),
            Hand(0.75f, 0.5f)
        });
        await using var service = Service(slot, new FakeHandDetectionBackend(result));
        await service.StartAsync(CancellationToken.None);

        slot.Publish(Frame(1));
        var output = await NextFrameAsync(service);

        var active = Assert.Single(output.ActiveHands);
        AssertClose(0.75f, active.NormalizedPosition.X);
        Assert.Equal(2, output.RejectedHandCount);
    }

    [Fact]
    public async Task Processing_MapsAllTwentyOneLandmarksInMediaPipeOrder()
    {
        using var slot = new LatestFrameSlot<CameraFrame>();
        var landmarks = Enumerable.Range(0, DetectedHand.LandmarkCount)
            .Select(index => new HandLandmark(index / 20f, index / 40f, -index / 100f))
            .ToArray();
        landmarks[8] = new HandLandmark(0.5f, 0.5f, -0.08f);
        var result = new HandDetectionResult([new DetectedHand(0.9f, landmarks)]);
        await using var service = Service(slot, new FakeHandDetectionBackend(result));
        await service.StartAsync(CancellationToken.None);

        slot.Publish(Frame(1));
        var output = await NextFrameAsync(service);

        var mapped = Assert.Single(output.ActiveHands).Landmarks;
        Assert.Equal(21, mapped.Count);
        Assert.Equal(Enumerable.Range(0, 21), mapped.Select(point => point.Index));
        AssertClose(1f, mapped[20].NormalizedPosition.X);
        AssertClose(0.5f, mapped[20].NormalizedPosition.Y);
        AssertClose(-0.2f, mapped[20].Z);
    }

    [Fact]
    public async Task SlowBackend_DoesNotBlockFramePublisher()
    {
        using var slot = new LatestFrameSlot<CameraFrame>();
        var detectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeHandDetectionBackend(async (_, _, cancellationToken) =>
        {
            detectEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Result();
        });
        await using var service = Service(slot, backend);
        await service.StartAsync(CancellationToken.None);
        var stopwatch = Stopwatch.StartNew();

        slot.Publish(Frame(1));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
        await detectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(service.Completion.IsCompleted);
        release.TrySetResult();
        await NextFrameAsync(service);
    }

    [Fact]
    public async Task Cancellation_DisposesInFlightFrameAndBackendExactlyOnce()
    {
        using var slot = new LatestFrameSlot<CameraFrame>();
        var detectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeHandDetectionBackend(async (_, _, cancellationToken) =>
        {
            detectEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Result();
        });
        var service = Service(slot, backend);
        await service.StartAsync(CancellationToken.None);
        var frame = Frame(1);
        slot.Publish(frame);
        await detectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(1, backend.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = frame.Image);
    }

    [Fact]
    public async Task StartStop_FiftyTimes_ReleasesEveryBackendAndFrame()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            using var slot = new LatestFrameSlot<CameraFrame>();
            var backend = new FakeHandDetectionBackend(Result());
            var service = Service(slot, backend);
            await service.StartAsync(CancellationToken.None);
            var frame = Frame(iteration);
            slot.Publish(frame);
            await NextFrameAsync(service);

            await service.DisposeAsync();

            Assert.Equal(1, backend.DisposeCount);
            Assert.Throws<ObjectDisposedException>(() => _ = frame.Image);
        }
    }

    private static CameraHandProcessingService Service(
        LatestFrameSlot<CameraFrame> slot,
        FakeHandDetectionBackend backend,
        int width = 100,
        int height = 100) =>
        new(
            slot,
            backend,
            new HandDetectionOptions("model.task", 32, 0.5f, 0.5f),
            HandTrackingPoint.IndexTip,
            new HomographySurfaceMapper(new CameraCalibration(
                new Vector2Data(0f, 0f),
                new Vector2Data(width, 0f),
                new Vector2Data(width, height),
                new Vector2Data(0f, height))),
            new HandTrackAssigner(0.2f, 1),
            new EmaPositionFilter(0.35f),
            minimumHandConfidence: 0.5f);

    private static CameraFrame Frame(
        long sequence,
        int width = 100,
        int height = 100,
        Scalar? color = null)
    {
        var image = new Mat(height, width, MatType.CV_8UC3, color ?? new Scalar(1, 2, 3));
        return new CameraFrame(
            sequence,
            DateTimeOffset.FromUnixTimeMilliseconds(1000 + sequence),
            image);
    }

    private static HandDetectionResult Result(params DetectedHand[] hands) => new(hands);

    private static DetectedHand Hand(float trackingX, float trackingY, float confidence = 0.9f)
    {
        var landmarks = Enumerable.Range(0, DetectedHand.LandmarkCount)
            .Select(_ => new HandLandmark(trackingX, trackingY, 0f))
            .ToArray();
        landmarks[8] = new HandLandmark(trackingX, trackingY, -0.1f);
        return new DetectedHand(confidence, landmarks);
    }

    private static async Task<CameraHandFrame> NextFrameAsync(CameraHandProcessingService service)
    {
        if (service.LatestFrame is { } current)
        {
            return current;
        }

        var source = new TaskCompletionSource<CameraHandFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(CameraHandFrame frame) => source.TrySetResult(frame);
        service.FrameProcessed += Handler;
        try
        {
            if (service.LatestFrame is { } raced)
            {
                return raced;
            }

            return await source.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            service.FrameProcessed -= Handler;
        }
    }

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.00001f, expected + 0.00001f);

    private sealed class FakeHandDetectionBackend : IHandDetectionBackend
    {
        private readonly Func<RgbFrameView, long, CancellationToken, ValueTask<HandDetectionResult>> _detect;
        private int _disposeCount;
        private int _detectCount;

        public FakeHandDetectionBackend(HandDetectionResult result)
            : this((_, _, _) => ValueTask.FromResult(result))
        {
        }

        public FakeHandDetectionBackend(
            Func<RgbFrameView, long, CancellationToken, ValueTask<HandDetectionResult>> detect)
        {
            _detect = detect;
        }

        public HandBackendStatus Status { get; private set; } = HandBackendStatus.Uninitialized;
        public int DetectCount => Volatile.Read(ref _detectCount);
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task InitializeAsync(HandDetectionOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Status = HandBackendStatus.Ready;
            return Task.CompletedTask;
        }

        public ValueTask<HandDetectionResult> DetectAsync(
            RgbFrameView frame,
            long timestampUnixMs,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _detectCount);
            return _detect(frame, timestampUnixMs, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                Status = HandBackendStatus.Disposed;
            }

            return ValueTask.CompletedTask;
        }
    }
}
