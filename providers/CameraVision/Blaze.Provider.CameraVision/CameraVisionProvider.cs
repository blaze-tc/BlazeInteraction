using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision;

public sealed class CameraVisionProvider : IInteractionProvider
{
    private readonly string _providerDirectory;
    private readonly IServiceProvider _services;
    private readonly ICameraCaptureBackendFactory _captureFactory;
    private readonly CameraVisionConfigurationStore _configurationStore;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _pointGate = new();
    private readonly Dictionary<long, InteractionPoint> _lastPoints = new();
    private IReadOnlyList<InteractionSurface> _surfaces = Array.Empty<InteractionSurface>();
    private InteractionSurface? _surface;
    private CameraVisionConfiguration? _configuration;
    private CameraCaptureService? _captureService;
    private CameraHandProcessingService? _processingService;
    private Task? _processingMonitor;
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
        _providerDirectory = Path.GetFullPath(createContext.ProviderDirectory);
        _services = createContext.Services;
        _captureFactory = createContext.Services.GetService(typeof(ICameraCaptureBackendFactory))
            as ICameraCaptureBackendFactory
            ?? new OpenCvCameraCaptureBackendFactory();
        var storage = createContext.Services.GetService(typeof(IProviderStorageContext))
            as IProviderStorageContext
            ?? throw new InvalidOperationException(
                "CameraVision requires project-scoped provider storage.");
        _configurationStore = new CameraVisionConfigurationStore(storage);
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
            try
            {
                if (context.Surfaces.Count == 0)
                {
                    throw new InvalidOperationException(
                        "CameraVision requires at least one Interaction Surface.");
                }

                _surfaces = Array.AsReadOnly(context.Surfaces
                    .OrderBy(static surface => surface.Order)
                    .ToArray());
                _surface = _surfaces.FirstOrDefault(static surface => surface.IsPrimary)
                    ?? _surfaces[0];
                var loaded = await _configurationStore.LoadOrCreateAsync(cancellationToken)
                    .ConfigureAwait(false);
                _configuration = loaded.Configuration
                    ?? throw new InvalidDataException(loaded.Error
                        ?? "CameraVision configuration could not be loaded.");
                TransitionTo(ProviderRuntimeStatus.Ready);
            }
            catch (Exception exception)
            {
                TransitionTo(ProviderRuntimeStatus.Faulted, exception);
                throw;
            }
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

            var configuration = _configuration
                ?? throw new InvalidOperationException("CameraVision is not initialized.");
            var surface = _surface
                ?? throw new InvalidOperationException("CameraVision has no selected surface.");
            TransitionTo(ProviderRuntimeStatus.Starting);
            lock (_pointGate)
            {
                _lastPoints.Clear();
            }

            var capture = new CameraCaptureService(_captureFactory, configuration.Capture);
            var processing = CreateProcessingService(capture, configuration, surface);
            processing.FrameProcessed += PublishHandFrame;
            try
            {
                _captureService = capture;
                _processingService = processing;
                await capture.StartAsync(cancellationToken).ConfigureAwait(false);
                await processing.StartAsync(cancellationToken).ConfigureAwait(false);
                _processingMonitor = MonitorProcessingAsync(processing);
                TransitionTo(ProviderRuntimeStatus.Running);
            }
            catch (Exception exception)
            {
                processing.FrameProcessed -= PublishHandFrame;
                _processingService = null;
                _captureService = null;
                await DisposeRunResourcesAsync(processing, capture).ConfigureAwait(false);
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

    internal void PublishHandFrame(CameraHandFrame handFrame)
    {
        ArgumentNullException.ThrowIfNull(handFrame);
        if (Status is not ProviderRuntimeStatus.Starting and not ProviderRuntimeStatus.Running)
        {
            return;
        }

        var surface = _surface;
        var configuration = _configuration;
        if (surface is null || configuration is null)
        {
            return;
        }

        var points = new List<InteractionPoint>(
            handFrame.ActiveHands.Count + handFrame.RemovedTrackIds.Count);
        lock (_pointGate)
        {
            foreach (var hand in handFrame.ActiveHands)
            {
                var point = CreateActivePoint(hand, handFrame.TimestampUnixMs, surface, configuration);
                _lastPoints[hand.TrackId] = point;
                points.Add(point);
            }

            foreach (var removedTrackId in handFrame.RemovedTrackIds)
            {
                if (!_lastPoints.Remove(removedTrackId, out var previous))
                {
                    continue;
                }

                points.Add(previous with
                {
                    Phase = InteractionPhase.Cancel,
                    TimestampUnixMs = handFrame.TimestampUnixMs
                });
            }
        }

        if (points.Count == 0)
        {
            return;
        }

        var frame = new InteractionFrame
        {
            ProviderId = CameraVisionPlugin.ProviderId,
            ProviderInstanceId = ProviderInstanceId,
            SurfaceId = surface.SurfaceId,
            Sequence = Interlocked.Increment(ref _sequence),
            TimestampUnixMs = handFrame.TimestampUnixMs,
            Points = Array.AsReadOnly(points.ToArray())
        };
        InvokeFrameReceived(frame);
    }

    private CameraHandProcessingService CreateProcessingService(
        CameraCaptureService capture,
        CameraVisionConfiguration configuration,
        InteractionSurface surface)
    {
        var backend = _services.GetService(typeof(IHandDetectionBackend))
            as IHandDetectionBackend
            ?? new MediaPipeHandBackend(new NativeHandLibrary(Path.Combine(
                _providerDirectory,
                "runtimes",
                "win-x64",
                "native",
                NativeHandLibrary.FileName)));
        var modelPath = Path.Combine(
            _providerDirectory,
            "models",
            "hand_landmarker.task");
        var calibration = configuration.Calibrations.TryGetValue(surface.SurfaceId, out var configured)
            ? configured
            : FullFrameCalibration(configuration.Capture);
        try
        {
            return new CameraHandProcessingService(
                capture.LatestFrames,
                backend,
                new HandDetectionOptions(
                    modelPath,
                    configuration.MaxHands,
                    configuration.MinDetectionConfidence,
                    configuration.MinTrackingConfidence),
                configuration.TrackingPoint,
                new HomographySurfaceMapper(calibration),
                new HandTrackAssigner(
                    configuration.MaximumMatchDistance,
                    configuration.LostFrameTolerance),
                new EmaPositionFilter(configuration.SmoothingFactor),
                configuration.MinDetectionConfidence);
        }
        catch
        {
            backend.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static CameraCalibration FullFrameCalibration(CameraCaptureOptions capture) => new(
        new Vector2Data(0f, 0f),
        new Vector2Data(capture.Width, 0f),
        new Vector2Data(capture.Width, capture.Height),
        new Vector2Data(0f, capture.Height));

    private InteractionPoint CreateActivePoint(
        CameraHandSnapshot hand,
        long timestampUnixMs,
        InteractionSurface surface,
        CameraVisionConfiguration configuration)
    {
        var extension = JsonSerializer.SerializeToElement(
            new
            {
                schemaVersion = 1,
                trackingPoint = configuration.TrackingPoint.ToString(),
                landmarks = hand.Landmarks.Select(landmark => new
                {
                    index = landmark.Index,
                    normalizedPosition = landmark.NormalizedPosition,
                    pixelPosition = new Vector2Data(
                        landmark.NormalizedPosition.X * surface.LogicalWidth,
                        landmark.NormalizedPosition.Y * surface.LogicalHeight),
                    z = landmark.Z
                }).ToArray()
            },
            InteractionJson.Options);
        return new InteractionPoint
        {
            Id = hand.TrackId,
            SurfaceId = surface.SurfaceId,
            ProviderId = CameraVisionPlugin.ProviderId,
            ProviderInstanceId = ProviderInstanceId,
            SourceId = $"hand-track-{hand.TrackId}",
            Phase = InteractionPhase.Hover,
            NormalizedPosition = hand.NormalizedPosition,
            PixelPosition = new Vector2Data(
                hand.NormalizedPosition.X * surface.LogicalWidth,
                hand.NormalizedPosition.Y * surface.LogicalHeight),
            Confidence = hand.Confidence,
            TimestampUnixMs = timestampUnixMs,
            Fp = Array.Empty<Vector2Data>(),
            Extensions = new InteractionExtensions(
                new Dictionary<string, JsonElement> { ["hand"] = extension })
        };
    }

    private async Task MonitorProcessingAsync(CameraHandProcessingService processing)
    {
        try
        {
            await processing.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(Volatile.Read(ref _processingService), processing)
                && Status is ProviderRuntimeStatus.Starting or ProviderRuntimeStatus.Running)
            {
                TransitionTo(ProviderRuntimeStatus.Faulted, exception);
            }
        }
    }

    private async Task StopRunAsync()
    {
        if (Status is not ProviderRuntimeStatus.Stopped and not ProviderRuntimeStatus.Created)
        {
            TransitionTo(ProviderRuntimeStatus.Stopping);
        }

        var processing = Interlocked.Exchange(ref _processingService, null);
        var capture = Interlocked.Exchange(ref _captureService, null);
        var monitor = Interlocked.Exchange(ref _processingMonitor, null);
        if (processing is null && capture is null)
        {
            return;
        }

        if (processing is not null)
        {
            processing.FrameProcessed -= PublishHandFrame;
        }

        await DisposeRunResourcesAsync(processing, capture).ConfigureAwait(false);
        if (monitor is not null)
        {
            await monitor.ConfigureAwait(false);
        }

        lock (_pointGate)
        {
            _lastPoints.Clear();
        }

        Interlocked.Increment(ref _completedRunCount);
    }

    private static async Task DisposeRunResourcesAsync(
        CameraHandProcessingService? processing,
        CameraCaptureService? capture)
    {
        Exception? firstError = null;
        if (processing is not null)
        {
            try
            {
                await processing.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstError = exception;
            }
        }

        if (capture is not null)
        {
            try
            {
                await capture.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await capture.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (firstError is not null)
            {
                // Preserve the first lifecycle error while still attempting capture cleanup.
                _ = exception;
            }
        }

        if (firstError is not null)
        {
            throw firstError;
        }
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

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
