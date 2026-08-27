using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision;

public sealed class CameraVisionProvider : IInteractionProvider, ICameraVisionControl
{
    private readonly string _providerDirectory;
    private readonly IServiceProvider _services;
    private readonly ICameraCaptureBackendFactory _captureFactory;
    private readonly CameraVisionConfigurationStore _configurationStore;
    private readonly IInteractionHostStatusSubscription? _hostStatusSubscription;
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
    private InteractionHostStatus _unityStatus = InteractionHostStatus.Disconnected;
    private CameraVisionStatusSnapshot _currentControlStatus;
    private long _runStartedTimestamp;
    private long _runStartedOutputSequence;
    private string? _controlError;

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
        var hostStatus = createContext.Services.GetService(typeof(IInteractionHostStatus))
            as IInteractionHostStatus;
        if (hostStatus is not null)
        {
            _hostStatusSubscription = hostStatus.Subscribe(OnUnityStatusChanged);
            _unityStatus = _hostStatusSubscription.Current;
        }

        _currentControlStatus = CreateStatusSnapshot();
    }

    public string ProviderInstanceId { get; }
    public ProviderRuntimeStatus Status => (ProviderRuntimeStatus)Volatile.Read(ref _status);
    public CameraCaptureStatus CameraStatus =>
        Volatile.Read(ref _captureService)?.Status ?? CameraCaptureStatus.Stopped;
    public CameraFrameStatistics? FrameStatistics =>
        Volatile.Read(ref _captureService)?.Statistics;
    internal int CompletedRunCount => Volatile.Read(ref _completedRunCount);
    internal string? ActiveSurfaceId => Volatile.Read(ref _surface)?.SurfaceId;
    internal InteractionSurface? ActiveSurface => Volatile.Read(ref _surface);
    CameraVisionConfiguration? ICameraVisionControl.CurrentConfiguration =>
        Volatile.Read(ref _configuration);
    CameraVisionStatusSnapshot ICameraVisionControl.CurrentStatus =>
        Volatile.Read(ref _currentControlStatus);

    public event EventHandler<InteractionFrameEventArgs>? FrameReceived;
    public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged;
    event Action<CameraVisionStatusSnapshot>? ICameraVisionControl.StatusChanged
    {
        add => _controlStatusChanged += value;
        remove => _controlStatusChanged -= value;
    }

    private event Action<CameraVisionStatusSnapshot>? _controlStatusChanged;

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

            await StartRunAsync(cancellationToken).ConfigureAwait(false);
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

        _hostStatusSubscription?.Dispose();
        _lifecycle.Dispose();
    }

    Task<IReadOnlyList<CameraDeviceDescriptor>> ICameraVisionControl.EnumerateDevicesAsync(
        CancellationToken cancellationToken) =>
        EnumerateDevicesAsync(cancellationToken);

    Task<CameraDeviceCapabilities> ICameraVisionControl.GetCapabilitiesAsync(
        int deviceIndex,
        CancellationToken cancellationToken) =>
        GetCapabilitiesAsync(deviceIndex, cancellationToken);

    Task ICameraVisionControl.ApplyAsync(
        CameraVisionConfiguration configuration,
        CancellationToken cancellationToken) =>
        ApplyConfigurationAsync(configuration, cancellationToken);

    Task ICameraVisionControl.ReconnectAsync(CancellationToken cancellationToken) =>
        ReconnectAsync(cancellationToken);

    Task ICameraVisionControl.SetCalibrationPointAsync(
        string surfaceId,
        int pointIndex,
        Vector2Data previewPosition,
        Vector2Data previewSize,
        CancellationToken cancellationToken) =>
        SetCalibrationPointAsync(
            surfaceId,
            pointIndex,
            previewPosition,
            previewSize,
            cancellationToken);

    Task ICameraVisionControl.ResetCalibrationAsync(
        string surfaceId,
        CancellationToken cancellationToken) =>
        ResetCalibrationAsync(surfaceId, cancellationToken);

    private async Task<IReadOnlyList<CameraDeviceDescriptor>> EnumerateDevicesAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await new CameraDeviceEnumerator(_captureFactory)
                .EnumerateAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<CameraDeviceCapabilities> GetCapabilitiesAsync(
        int deviceIndex,
        CancellationToken cancellationToken)
    {
        if (deviceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deviceIndex));
        }

        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await new CameraCapabilityEnumerator(_captureFactory)
                .EnumerateAsync(
                    new CameraDeviceDescriptor(deviceIndex, $"Camera {deviceIndex}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ApplyConfigurationAsync(
        CameraVisionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var validated = CloneConfiguration(configuration);
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await ApplyConfigurationCoreAsync(validated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _ = _configuration
                ?? throw new InvalidOperationException("CameraVision is not initialized.");
            await StopRunAsync().ConfigureAwait(false);
            TransitionTo(ProviderRuntimeStatus.Stopped);
            await StartRunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task SetCalibrationPointAsync(
        string surfaceId,
        int pointIndex,
        Vector2Data previewPosition,
        Vector2Data previewSize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
        {
            throw new ArgumentException("A surface ID is required.", nameof(surfaceId));
        }
        if (pointIndex is < 0 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(pointIndex));
        }
        ArgumentNullException.ThrowIfNull(previewPosition);
        ArgumentNullException.ThrowIfNull(previewSize);
        if (!float.IsFinite(previewSize.X) || !float.IsFinite(previewSize.Y) ||
            previewSize.X <= 0 || previewSize.Y <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(previewSize));
        }

        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = _configuration
                ?? throw new InvalidOperationException("CameraVision is not initialized.");
            var preview = _processingService?.LatestFrame?.Preview;
            var cameraWidth = preview?.Width ?? RotatedWidth(configuration.Capture);
            var cameraHeight = preview?.Height ?? RotatedHeight(configuration.Capture);
            var cameraPoint = new Vector2Data(
                previewPosition.X / previewSize.X * cameraWidth,
                previewPosition.Y / previewSize.Y * cameraHeight);
            var calibration = configuration.Calibrations.TryGetValue(surfaceId, out var current)
                ? current
                : FullFrameCalibration(configuration.Capture);
            var points = calibration.Points.ToArray();
            points[pointIndex] = cameraPoint;
            var nextCalibration = new CameraCalibration(points[0], points[1], points[2], points[3]);
            var calibrations = configuration.Calibrations.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            calibrations[surfaceId] = nextCalibration;
            await ApplyConfigurationCoreAsync(
                    CloneConfiguration(configuration, calibrations),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ResetCalibrationAsync(
        string surfaceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
        {
            throw new ArgumentException("A surface ID is required.", nameof(surfaceId));
        }

        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = _configuration
                ?? throw new InvalidOperationException("CameraVision is not initialized.");
            var calibrations = configuration.Calibrations.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal);
            calibrations.Remove(surfaceId);
            await ApplyConfigurationCoreAsync(
                    CloneConfiguration(configuration, calibrations),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ApplyConfigurationCoreAsync(
        CameraVisionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var previous = _configuration
            ?? throw new InvalidOperationException("CameraVision is not initialized.");
        var captureChanged = !CaptureConfigurationEquals(
            previous.Capture,
            configuration.Capture);
        var processingChanged = !ProcessingConfigurationEquals(previous, configuration);
        await _configurationStore.SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _configuration, configuration);

        if (Status != ProviderRuntimeStatus.Running || (!captureChanged && !processingChanged))
        {
            PublishControlStatus();
            return;
        }

        if (captureChanged)
        {
            await StopRunAsync().ConfigureAwait(false);
            TransitionTo(ProviderRuntimeStatus.Stopped);
            await StartRunAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await RestartProcessingAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    private async Task StartRunAsync(CancellationToken cancellationToken)
    {
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
        capture.StatusChanged += OnCameraStatusChanged;
        var processing = CreateProcessingService(capture, configuration, surface);
        processing.FrameProcessed += PublishHandFrame;
        try
        {
            _captureService = capture;
            _processingService = processing;
            await capture.StartAsync(cancellationToken).ConfigureAwait(false);
            await processing.StartAsync(cancellationToken).ConfigureAwait(false);
            _processingMonitor = MonitorProcessingAsync(processing);
            Interlocked.Exchange(ref _runStartedTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _runStartedOutputSequence, Interlocked.Read(ref _sequence));
            TransitionTo(ProviderRuntimeStatus.Running);
        }
        catch (Exception exception)
        {
            processing.FrameProcessed -= PublishHandFrame;
            capture.StatusChanged -= OnCameraStatusChanged;
            _processingService = null;
            _captureService = null;
            await DisposeRunResourcesAsync(processing, capture).ConfigureAwait(false);
            TransitionTo(ProviderRuntimeStatus.Faulted, exception);
            throw;
        }
    }

    private async Task RestartProcessingAsync(
        CameraVisionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var capture = _captureService
            ?? throw new InvalidOperationException("CameraVision capture is not running.");
        var surface = _surface
            ?? throw new InvalidOperationException("CameraVision has no selected surface.");
        TransitionTo(ProviderRuntimeStatus.Starting);
        var previous = Interlocked.Exchange(ref _processingService, null);
        var monitor = Interlocked.Exchange(ref _processingMonitor, null);
        if (previous is not null)
        {
            previous.FrameProcessed -= PublishHandFrame;
            await previous.DisposeAsync().ConfigureAwait(false);
        }
        if (monitor is not null)
        {
            await monitor.ConfigureAwait(false);
        }

        var next = CreateProcessingService(capture, configuration, surface);
        next.FrameProcessed += PublishHandFrame;
        try
        {
            _processingService = next;
            await next.StartAsync(cancellationToken).ConfigureAwait(false);
            _processingMonitor = MonitorProcessingAsync(next);
            TransitionTo(ProviderRuntimeStatus.Running);
        }
        catch (Exception exception)
        {
            next.FrameProcessed -= PublishHandFrame;
            _processingService = null;
            await next.DisposeAsync().ConfigureAwait(false);
            TransitionTo(ProviderRuntimeStatus.Faulted, exception);
            throw;
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

        var pointSnapshot = Array.AsReadOnly(points.ToArray());
        PublishControlStatus(handFrame, outputPoints: pointSnapshot);
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
            Points = pointSnapshot
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
        var orderedLandmarks = hand.Landmarks
            .OrderBy(landmark => landmark.Index)
            .ToArray();
        var mappedLandmarks = orderedLandmarks
            .Select(landmark => new Vector2Data(
                landmark.NormalizedPosition.X * surface.LogicalWidth,
                landmark.NormalizedPosition.Y * surface.LogicalHeight))
            .ToArray();
        var extension = JsonSerializer.SerializeToElement(
            new
            {
                schemaVersion = 1,
                trackingPoint = configuration.TrackingPoint.ToString(),
                landmarks = orderedLandmarks.Select((landmark, index) => new
                {
                    index = landmark.Index,
                    normalizedPosition = landmark.NormalizedPosition,
                    pixelPosition = mappedLandmarks[index],
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
            Fp = mappedLandmarks,
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
        if (capture is not null)
        {
            capture.StatusChanged -= OnCameraStatusChanged;
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

        if (error is not null)
        {
            Volatile.Write(ref _controlError, error.Message);
        }
        else if (status != ProviderRuntimeStatus.Faulted)
        {
            Volatile.Write(ref _controlError, null);
        }
        PublishControlStatus();
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

    private void OnCameraStatusChanged(object? sender, CameraCaptureStatusChangedEventArgs eventArgs) =>
        PublishControlStatus();

    private void OnUnityStatusChanged(InteractionHostStatus status)
    {
        Volatile.Write(ref _unityStatus, status);
        PublishControlStatus();
    }

    private void PublishControlStatus(
        CameraHandFrame? handFrame = null,
        Exception? error = null,
        IReadOnlyList<InteractionPoint>? outputPoints = null)
    {
        if (error is not null)
        {
            Volatile.Write(ref _controlError, error.Message);
        }
        var snapshot = CreateStatusSnapshot(handFrame, error?.Message, outputPoints);
        Volatile.Write(ref _currentControlStatus, snapshot);
        var handlers = _controlStatusChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<CameraVisionStatusSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch
            {
            }
        }
    }

    private CameraVisionStatusSnapshot CreateStatusSnapshot(
        CameraHandFrame? handFrame = null,
        string? error = null,
        IReadOnlyList<InteractionPoint>? outputPoints = null)
    {
        var capture = Volatile.Read(ref _captureService);
        var statistics = capture?.Statistics;
        var elapsedSeconds = ElapsedRunSeconds();
        var processedFrames = handFrame?.ProcessedFrameCount
            ?? Volatile.Read(ref _processingService)?.LatestFrame?.ProcessedFrameCount
            ?? 0;
        var outputFrames = Math.Max(
            0,
            Interlocked.Read(ref _sequence) - Interlocked.Read(ref _runStartedOutputSequence));
        var latest = handFrame ?? Volatile.Read(ref _processingService)?.LatestFrame;
        if (outputPoints is null)
        {
            lock (_pointGate)
            {
                outputPoints = Array.AsReadOnly(_lastPoints.Values.ToArray());
            }
        }
        var configuration = Volatile.Read(ref _configuration);
        var surface = Volatile.Read(ref _surface);
        var calibration = configuration is not null && surface is not null &&
                          configuration.Calibrations.TryGetValue(surface.SurfaceId, out var configured)
            ? configured
            : configuration is not null
                ? FullFrameCalibration(configuration.Capture)
                : null;
        return new CameraVisionStatusSnapshot(
            Status,
            capture?.Status ?? CameraCaptureStatus.Stopped,
            Rate(statistics?.CapturedFrames ?? 0, elapsedSeconds),
            Rate(processedFrames, elapsedSeconds),
            Rate(outputFrames, elapsedSeconds),
            latest?.InferenceMilliseconds ?? 0,
            latest?.DetectedHandCount ?? 0,
            latest?.ActiveHands.Select(static hand => hand.NormalizedPosition)
                ?? Enumerable.Empty<Vector2Data>(),
            latest?.DroppedFrameCount ?? statistics?.DroppedFrames ?? 0,
            Volatile.Read(ref _unityStatus).IsConnected,
            latest?.Preview,
            latest?.ActiveHands,
            error ?? Volatile.Read(ref _controlError),
            latest?.TimestampUnixMs ?? 0,
            statistics?.ActualWidth ?? latest?.Preview.Width ?? 0,
            statistics?.ActualHeight ?? latest?.Preview.Height ?? 0,
            outputPoints,
            calibration?.Points);
    }

    private double ElapsedRunSeconds()
    {
        var started = Interlocked.Read(ref _runStartedTimestamp);
        if (started <= 0)
        {
            return 0;
        }

        return Math.Max(
            0,
            (System.Diagnostics.Stopwatch.GetTimestamp() - started) /
            (double)System.Diagnostics.Stopwatch.Frequency);
    }

    private static double Rate(long count, double elapsedSeconds) =>
        elapsedSeconds > 0 ? Math.Max(0, count / elapsedSeconds) : 0;

    private static int RotatedWidth(CameraCaptureOptions capture) =>
        capture.Rotation is CameraRotation.Rotate90 or CameraRotation.Rotate270
            ? capture.Height
            : capture.Width;

    private static int RotatedHeight(CameraCaptureOptions capture) =>
        capture.Rotation is CameraRotation.Rotate90 or CameraRotation.Rotate270
            ? capture.Width
            : capture.Height;

    private static CameraVisionConfiguration CloneConfiguration(
        CameraVisionConfiguration configuration,
        IReadOnlyDictionary<string, CameraCalibration>? calibrations = null) =>
        new(
            configuration.SchemaVersion,
            configuration.Capture,
            configuration.MaxHands,
            configuration.MinDetectionConfidence,
            configuration.MinTrackingConfidence,
            configuration.TrackingPoint,
            configuration.SmoothingFactor,
            configuration.MaximumMatchDistance,
            configuration.LostFrameTolerance,
            calibrations ?? configuration.Calibrations,
            configuration.DeviceProfiles);

    private static bool ProcessingConfigurationEquals(
        CameraVisionConfiguration left,
        CameraVisionConfiguration right) =>
        left.MaxHands == right.MaxHands &&
        left.MinDetectionConfidence.Equals(right.MinDetectionConfidence) &&
        left.MinTrackingConfidence.Equals(right.MinTrackingConfidence) &&
        left.TrackingPoint == right.TrackingPoint &&
        left.SmoothingFactor.Equals(right.SmoothingFactor) &&
        left.MaximumMatchDistance.Equals(right.MaximumMatchDistance) &&
        left.LostFrameTolerance == right.LostFrameTolerance &&
        CalibrationEquals(left.Calibrations, right.Calibrations);

    private static bool CaptureConfigurationEquals(
        CameraCaptureOptions left,
        CameraCaptureOptions right) =>
        left.DeviceIndex == right.DeviceIndex &&
        left.Width == right.Width &&
        left.Height == right.Height &&
        left.FramesPerSecond.Equals(right.FramesPerSecond) &&
        left.MirrorX == right.MirrorX &&
        left.Rotation == right.Rotation &&
        left.ReconnectDelay == right.ReconnectDelay;

    private static bool CalibrationEquals(
        IReadOnlyDictionary<string, CameraCalibration> left,
        IReadOnlyDictionary<string, CameraCalibration> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (surfaceId, calibration) in left)
        {
            if (!right.TryGetValue(surfaceId, out var other) ||
                !calibration.Points.SequenceEqual(other.Points))
            {
                return false;
            }
        }

        return true;
    }
}
