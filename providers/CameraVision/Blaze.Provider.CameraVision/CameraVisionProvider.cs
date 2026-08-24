using System.Diagnostics;
using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision;

public sealed class CameraVisionProvider : IInteractionProvider
{
    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ICameraCaptureBackendFactory _captureFactory;
    private readonly CameraCaptureOptions _captureOptions;
    private readonly IVisualDetector _detector;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IReadOnlyList<InteractionSurface> _surfaces = Array.Empty<InteractionSurface>();
    private CameraCaptureService? _captureService;
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private long _sequence;
    private int _status = (int)ProviderRuntimeStatus.Created;
    private int _disposed;
    private int _completedRunCount;

    internal CameraVisionProvider(string providerInstanceId, ProviderCreateContext createContext)
    {
        ProviderInstanceId = string.IsNullOrWhiteSpace(providerInstanceId)
            ? throw new ArgumentException("A provider instance identifier is required.", nameof(providerInstanceId))
            : providerInstanceId;
        ArgumentNullException.ThrowIfNull(createContext);
        _captureFactory = createContext.Services.GetService(typeof(ICameraCaptureBackendFactory))
            as ICameraCaptureBackendFactory
            ?? new OpenCvCameraCaptureBackendFactory();
        _captureOptions = createContext.Services.GetService(typeof(CameraCaptureOptions))
            as CameraCaptureOptions
            ?? LoadCaptureOptions(createContext.ProviderDirectory);
        _captureOptions.Validate();
        _detector = createContext.Services.GetService(typeof(IVisualDetector)) as IVisualDetector
            ?? new FakeVisualDetector();
    }

    public string ProviderInstanceId { get; }
    public ProviderRuntimeStatus Status => (ProviderRuntimeStatus)Volatile.Read(ref _status);
    public CameraCaptureStatus CameraStatus =>
        Volatile.Read(ref _captureService)?.Status ?? CameraCaptureStatus.Stopped;
    public CameraFrameStatistics? FrameStatistics =>
        Volatile.Read(ref _captureService)?.Statistics;
    internal int CompletedRunCount => Volatile.Read(ref _completedRunCount);

    public event EventHandler<InteractionFrameEventArgs>? FrameReceived;
    public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged;

    public async Task InitializeAsync(
        ProviderInitializationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Status != ProviderRuntimeStatus.Created)
            {
                throw new InvalidOperationException($"CameraVision cannot initialize from {Status}.");
            }

            TransitionTo(ProviderRuntimeStatus.Initializing);
            if (context.Surfaces.Count == 0)
            {
                var exception = new InvalidOperationException(
                    "CameraVision requires at least one Interaction Surface.");
                TransitionTo(ProviderRuntimeStatus.Faulted, exception);
                throw exception;
            }

            _surfaces = Array.AsReadOnly(context.Surfaces
                .OrderBy(static surface => surface.Order)
                .ToArray());
            TransitionTo(ProviderRuntimeStatus.Ready);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Status == ProviderRuntimeStatus.Running)
            {
                return;
            }

            if (Status is not ProviderRuntimeStatus.Ready and not ProviderRuntimeStatus.Stopped)
            {
                throw new InvalidOperationException($"CameraVision cannot start from {Status}.");
            }

            TransitionTo(ProviderRuntimeStatus.Starting);
            var capture = new CameraCaptureService(_captureFactory, _captureOptions);
            var runCancellation = new CancellationTokenSource();
            try
            {
                await capture.StartAsync(cancellationToken).ConfigureAwait(false);
                _captureService = capture;
                _runCancellation = runCancellation;
                _runTask = PublishLoopAsync(capture, runCancellation.Token);
                TransitionTo(ProviderRuntimeStatus.Running);
            }
            catch (Exception exception)
            {
                runCancellation.Dispose();
                await capture.DisposeAsync().ConfigureAwait(false);
                TransitionTo(ProviderRuntimeStatus.Faulted, exception);
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
            await StopRunAsync().ConfigureAwait(false);
            TransitionTo(ProviderRuntimeStatus.Stopped);
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

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopRunAsync().ConfigureAwait(false);
            TransitionTo(ProviderRuntimeStatus.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopRunAsync()
    {
        if (Status is not ProviderRuntimeStatus.Stopped and not ProviderRuntimeStatus.Created)
        {
            TransitionTo(ProviderRuntimeStatus.Stopping);
        }

        var runCancellation = Interlocked.Exchange(ref _runCancellation, null);
        var runTask = Interlocked.Exchange(ref _runTask, null);
        var capture = Interlocked.Exchange(ref _captureService, null);
        if (runCancellation is null && capture is null)
        {
            return;
        }

        if (runCancellation is not null)
        {
            await runCancellation.CancelAsync().ConfigureAwait(false);
            if (runTask is not null)
            {
                try
                {
                    await runTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            runCancellation.Dispose();
        }

        if (capture is not null)
        {
            await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await capture.DisposeAsync().ConfigureAwait(false);
        }

        Interlocked.Increment(ref _completedRunCount);
    }

    private async Task PublishLoopAsync(
        CameraCaptureService capture,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            CameraFrame? cameraFrame = null;
            try
            {
                _ = capture.LatestFrames.TryTake(out cameraFrame);
                var detections = _detector.Detect(cameraFrame, clock.Elapsed);
                PublishDetections(detections, DateTimeOffset.UtcNow);
            }
            finally
            {
                cameraFrame?.Dispose();
            }
        }
    }

    private void PublishDetections(
        IReadOnlyList<VisualDetection> detections,
        DateTimeOffset timestamp)
    {
        if (Status != ProviderRuntimeStatus.Running || detections.Count == 0)
        {
            return;
        }

        var surface = _surfaces.FirstOrDefault(static candidate => candidate.IsPrimary)
            ?? _surfaces[0];
        var timestampUnixMs = timestamp.ToUnixTimeMilliseconds();
        var points = detections.Select((detection, index) => new InteractionPoint
        {
            Id = index + 1,
            SurfaceId = surface.SurfaceId,
            ProviderId = CameraVisionPlugin.ProviderId,
            ProviderInstanceId = ProviderInstanceId,
            SourceId = detection.DetectorId,
            Phase = InteractionPhase.Hover,
            NormalizedPosition = new Vector2Data(detection.CameraX, detection.CameraY),
            PixelPosition = new Vector2Data(
                detection.CameraX * surface.LogicalWidth,
                detection.CameraY * surface.LogicalHeight),
            Confidence = detection.Confidence,
            TimestampUnixMs = timestampUnixMs
        }).ToArray();
        var frame = new InteractionFrame
        {
            ProviderId = CameraVisionPlugin.ProviderId,
            ProviderInstanceId = ProviderInstanceId,
            SurfaceId = surface.SurfaceId,
            Sequence = Interlocked.Increment(ref _sequence),
            TimestampUnixMs = timestampUnixMs,
            Points = Array.AsReadOnly(points)
        };
        InvokeFrameReceived(frame);
    }

    private void InvokeFrameReceived(InteractionFrame frame)
    {
        var handlers = FrameReceived;
        if (handlers is null)
        {
            return;
        }

        var arguments = new InteractionFrameEventArgs(frame);
        foreach (EventHandler<InteractionFrameEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, arguments);
            }
            catch
            {
            }
        }
    }

    private void TransitionTo(ProviderRuntimeStatus status, Exception? error = null)
    {
        var previous = (ProviderRuntimeStatus)Interlocked.Exchange(ref _status, (int)status);
        if (previous == status && error is null)
        {
            return;
        }

        var handlers = StatusChanged;
        if (handlers is null)
        {
            return;
        }

        var arguments = new ProviderStatusChangedEventArgs(previous, status, error);
        foreach (EventHandler<ProviderStatusChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, arguments);
            }
            catch
            {
            }
        }
    }

    private static CameraCaptureOptions LoadCaptureOptions(string providerDirectory)
    {
        var profilePath = Path.Combine(
            providerDirectory,
            "profiles",
            "camera-vision-default.json");
        if (!File.Exists(profilePath))
        {
            return new CameraCaptureOptions();
        }

        try
        {
            return JsonSerializer.Deserialize<CameraCaptureOptions>(
                       File.ReadAllText(profilePath),
                       ProfileJsonOptions)
                   ?? throw new InvalidDataException("CameraVision capture profile deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("CameraVision capture profile is invalid.", exception);
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
