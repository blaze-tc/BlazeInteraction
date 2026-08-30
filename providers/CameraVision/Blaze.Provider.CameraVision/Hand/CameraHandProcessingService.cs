using System.Diagnostics;
using System.Runtime.InteropServices;
using Blaze.Interaction.Contracts;
using OpenCvSharp;

namespace Blaze.Provider.CameraVision;

internal sealed class CameraHandProcessingService : IAsyncDisposable
{
    private const int MaximumConsecutiveFrameFailures = 3;
    private const int PreviewMaximumWidth = 960;
    private const int PreviewMaximumHeight = 540;
    private const long PreviewIntervalMilliseconds = 100;

    private readonly LatestFrameSlot<CameraFrame> _latestFrames;
    private readonly IHandDetectionBackend _backend;
    private readonly HandDetectionOptions _backendOptions;
    private readonly HandTrackingPoint _trackingPoint;
    private HomographySurfaceMapper _mapper;
    private readonly bool _useActualFrameDimensions;
    private readonly HandTrackAssigner _assigner;
    private readonly EmaPositionFilter _positionFilter;
    private readonly float _minimumHandConfidence;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private CameraHandFrame? _latestFrame;
    private long _processedFrameCount;
    private long _lastBackendTimestampUnixMs = long.MinValue;
    private long _lastPreviewTimestampUnixMs = long.MinValue;
    private CameraPreviewSnapshot? _latestPreview;
    private int _mapperWidth;
    private int _mapperHeight;
    private int _started;
    private int _disposed;

    public CameraHandProcessingService(
        LatestFrameSlot<CameraFrame> latestFrames,
        IHandDetectionBackend backend,
        HandDetectionOptions backendOptions,
        HandTrackingPoint trackingPoint,
        HomographySurfaceMapper mapper,
        HandTrackAssigner assigner,
        EmaPositionFilter positionFilter,
        float minimumHandConfidence,
        bool useActualFrameDimensions = false)
    {
        _latestFrames = latestFrames ?? throw new ArgumentNullException(nameof(latestFrames));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _backendOptions = backendOptions ?? throw new ArgumentNullException(nameof(backendOptions));
        if (!Enum.IsDefined(trackingPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(trackingPoint));
        }

        if (!float.IsFinite(minimumHandConfidence)
            || minimumHandConfidence is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumHandConfidence));
        }

        _trackingPoint = trackingPoint;
        _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _assigner = assigner ?? throw new ArgumentNullException(nameof(assigner));
        _positionFilter = positionFilter ?? throw new ArgumentNullException(nameof(positionFilter));
        _minimumHandConfidence = minimumHandConfidence;
        _useActualFrameDimensions = useActualFrameDimensions;
    }

    public event Action<CameraHandFrame>? FrameProcessed;

    public CameraHandFrame? LatestFrame => Volatile.Read(ref _latestFrame);
    public Task Completion => Volatile.Read(ref _runTask) ?? Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                throw new InvalidOperationException("The camera hand service can only be started once.");
            }

            try
            {
                await _backend.InitializeAsync(_backendOptions, cancellationToken).ConfigureAwait(false);
                var runCancellation = new CancellationTokenSource();
                _runCancellation = runCancellation;
                _runTask = Task.Run(
                    () => ProcessingLoopAsync(runCancellation.Token),
                    CancellationToken.None);
            }
            catch
            {
                Volatile.Write(ref _started, 0);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cancellation = Interlocked.Exchange(ref _runCancellation, null);
            var runTask = Interlocked.Exchange(ref _runTask, null);
            if (cancellation is null)
            {
                return;
            }

            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                if (runTask is not null)
                {
                    await runTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Completion is the authoritative runtime-fault channel. Do not
                // report that same frame-processing failure again as a cleanup
                // failure when the provider is stopped or switched.
            }
        }
        finally
        {
            await _backend.DisposeAsync().ConfigureAwait(false);
            _mapper.Dispose();
            _positionFilter.Reset();
            _lifecycle.Dispose();
        }
    }

    private async Task ProcessingLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_latestFrames.TryTake(out var frame) || frame is null)
                {
                    await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                CameraHandFrame output;
                try
                {
                    using (frame)
                    {
                        output = await ProcessFrameAsync(frame, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= MaximumConsecutiveFrameFailures)
                    {
                        throw new InvalidOperationException(
                            $"Camera hand processing failed for {consecutiveFailures} consecutive frames.",
                            exception);
                    }

                    continue;
                }

                consecutiveFailures = 0;
                Volatile.Write(ref _latestFrame, output);
                InvokeFrameProcessed(output);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<CameraHandFrame> ProcessFrameAsync(
        CameraFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.Image.Type() != MatType.CV_8UC3)
        {
            throw new InvalidDataException("Camera hand processing requires a BGR 8-bit three-channel frame.");
        }

        using var rgb = new Mat();
        Cv2.CvtColor(frame.Image, rgb, ColorConversionCodes.BGR2RGB);
        var timestampUnixMs = NextBackendTimestamp(frame.Timestamp.ToUnixTimeMilliseconds());
        var stopwatch = Stopwatch.StartNew();
        var result = await _backend.DetectAsync(
                new RgbFrameView(
                    rgb.Data,
                    rgb.Cols,
                    rgb.Rows,
                    checked((int)rgb.Step())),
                timestampUnixMs,
                cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        var mapper = MapperFor(frame.Width, frame.Height);
        var candidates = new List<HandCandidate>(result.Hands.Count);
        var rejectedHandCount = 0;
        foreach (var hand in result.Hands)
        {
            if (hand.Confidence < _minimumHandConfidence)
            {
                rejectedHandCount++;
                continue;
            }

            var trackingPoint = TrackingPointCalculator.Calculate(hand, _trackingPoint);
            var trackingPixel = ToCameraPixel(trackingPoint, frame.Width, frame.Height);
            if (!mapper.TryMapTrackingPoint(trackingPixel, out _))
            {
                rejectedHandCount++;
                continue;
            }

            candidates.Add(new HandCandidate(trackingPoint, hand));
        }

        var assignment = _assigner.Update(candidates);
        foreach (var removedTrackId in assignment.RemovedTrackIds)
        {
            _positionFilter.Remove(removedTrackId);
        }

        var activeHands = assignment.Active.Select(track =>
        {
            var trackingPixel = ToCameraPixel(
                track.Candidate.TrackingPoint,
                frame.Width,
                frame.Height);
            if (!mapper.TryMapTrackingPoint(trackingPixel, out var mappedTrackingPoint))
            {
                throw new InvalidDataException("A gated hand tracking point could not be remapped.");
            }

            var smoothed = _positionFilter.Update(track.TrackId, mappedTrackingPoint);
            var landmarks = track.Candidate.Hand.Landmarks.Select((landmark, index) =>
            {
                var cameraPixel = new Vector2Data(
                    landmark.X * frame.Width,
                    landmark.Y * frame.Height);
                return new CameraMappedLandmark(
                    index,
                    cameraPixel,
                    mapper.MapLandmark(cameraPixel),
                    landmark.Z);
            });
            return new CameraHandSnapshot(
                track.TrackId,
                track.Candidate.Hand.Confidence,
                trackingPixel,
                smoothed,
                landmarks);
        }).ToArray();

        var preview = PreviewFor(frame);
        var processedFrameCount = Interlocked.Increment(ref _processedFrameCount);
        var output = new CameraHandFrame(
            frame.Sequence,
            frame.Timestamp.ToUnixTimeMilliseconds(),
            activeHands,
            assignment.RemovedTrackIds,
            preview,
            stopwatch.Elapsed.TotalMilliseconds,
            processedFrameCount,
            _latestFrames.DroppedCount,
            result.Hands.Count,
            rejectedHandCount);
        return output;
    }

    private long NextBackendTimestamp(long sourceTimestampUnixMs)
    {
        var timestamp = sourceTimestampUnixMs <= _lastBackendTimestampUnixMs
            ? checked(_lastBackendTimestampUnixMs + 1)
            : sourceTimestampUnixMs;
        _lastBackendTimestampUnixMs = timestamp;
        return timestamp;
    }

    private static Vector2Data ToCameraPixel(CameraPoint point, int width, int height) =>
        new(point.X * width, point.Y * height);

    private HomographySurfaceMapper MapperFor(int width, int height)
    {
        if (!_useActualFrameDimensions || (_mapperWidth == width && _mapperHeight == height))
        {
            return _mapper;
        }

        var next = new HomographySurfaceMapper(new CameraCalibration(
            new Vector2Data(0f, 0f),
            new Vector2Data(width, 0f),
            new Vector2Data(width, height),
            new Vector2Data(0f, height)));
        var previous = _mapper;
        _mapper = next;
        _mapperWidth = width;
        _mapperHeight = height;
        previous.Dispose();
        return next;
    }

    private CameraPreviewSnapshot PreviewFor(CameraFrame frame)
    {
        var timestampUnixMs = frame.Timestamp.ToUnixTimeMilliseconds();
        var latest = _latestPreview;
        if (latest is not null && timestampUnixMs >= _lastPreviewTimestampUnixMs &&
            timestampUnixMs - _lastPreviewTimestampUnixMs < PreviewIntervalMilliseconds)
        {
            return latest;
        }

        var preview = CreatePreview(frame.Image);
        _latestPreview = preview;
        _lastPreviewTimestampUnixMs = timestampUnixMs;
        return preview;
    }

    private static CameraPreviewSnapshot CreatePreview(Mat bgr)
    {
        var scale = Math.Min(
            1d,
            Math.Min(
                PreviewMaximumWidth / (double)bgr.Cols,
                PreviewMaximumHeight / (double)bgr.Rows));
        using var resized = scale < 1d
            ? bgr.Resize(
                new Size(
                    Math.Max(1, (int)Math.Round(bgr.Cols * scale)),
                    Math.Max(1, (int)Math.Round(bgr.Rows * scale))),
                0,
                0,
                InterpolationFlags.Area)
            : null;
        var previewImage = resized ?? bgr;
        var stride = checked(previewImage.Cols * 3);
        var pixels = new byte[checked(stride * previewImage.Rows)];
        var sourceStride = checked((int)previewImage.Step());
        for (var row = 0; row < previewImage.Rows; row++)
        {
            Marshal.Copy(
                IntPtr.Add(previewImage.Data, checked(row * sourceStride)),
                pixels,
                checked(row * stride),
                stride);
        }

        return new CameraPreviewSnapshot(
            previewImage.Cols,
            previewImage.Rows,
            stride,
            pixels);
    }

    private void InvokeFrameProcessed(CameraHandFrame frame)
    {
        var handlers = FrameProcessed;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<CameraHandFrame> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(frame);
            }
            catch
            {
            }
        }
    }
}
