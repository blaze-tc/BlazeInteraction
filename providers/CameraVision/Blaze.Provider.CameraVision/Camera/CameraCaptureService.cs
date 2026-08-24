namespace Blaze.Provider.CameraVision;

public sealed class CameraCaptureService : IAsyncDisposable
{
    private readonly ICameraCaptureBackendFactory _factory;
    private readonly CameraCaptureOptions _options;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly LatestFrameSlot<CameraFrame> _latestFrames = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private long _capturedFrames;
    private long _reconnectAttempts;
    private long _lastFrameTimestampUnixMs = -1;
    private int _actualWidth;
    private int _actualHeight;
    private int _status = (int)CameraCaptureStatus.Stopped;
    private int _disposed;

    public CameraCaptureService(
        ICameraCaptureBackendFactory factory,
        CameraCaptureOptions options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public CameraCaptureStatus Status => (CameraCaptureStatus)Volatile.Read(ref _status);
    public LatestFrameSlot<CameraFrame> LatestFrames => _latestFrames;
    public CameraFrameStatistics Statistics => new(
        Interlocked.Read(ref _capturedFrames),
        _latestFrames.DroppedCount,
        Interlocked.Read(ref _reconnectAttempts),
        Volatile.Read(ref _actualWidth),
        Volatile.Read(ref _actualHeight),
        _options.FramesPerSecond,
        Interlocked.Read(ref _lastFrameTimestampUnixMs) is var timestamp && timestamp >= 0
            ? timestamp
            : null);

    public event EventHandler<CameraCaptureStatusChangedEventArgs>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runTask is not null)
            {
                return;
            }

            var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runCancellation = runCancellation;
            _runTask = Task.Run(
                () => CaptureLoopAsync(runCancellation.Token),
                CancellationToken.None);
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
            var runCancellation = Interlocked.Exchange(ref _runCancellation, null);
            var runTask = Interlocked.Exchange(ref _runTask, null);
            if (runCancellation is not null)
            {
                await runCancellation.CancelAsync().ConfigureAwait(false);
                if (runTask is not null)
                {
                    await runTask.ConfigureAwait(false);
                }

                runCancellation.Dispose();
            }

            TransitionTo(CameraCaptureStatus.Stopped);
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

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _latestFrames.Dispose();
        _lifecycle.Dispose();
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var firstAttempt = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TransitionTo(firstAttempt
                    ? CameraCaptureStatus.Connecting
                    : CameraCaptureStatus.Reconnecting);
                if (!firstAttempt)
                {
                    Interlocked.Increment(ref _reconnectAttempts);
                }

                firstAttempt = false;
                await using var backend = _factory.Create();
                bool opened;
                try
                {
                    opened = backend.TryOpen(_options);
                }
                catch
                {
                    opened = false;
                }

                if (!opened)
                {
                    TransitionTo(CameraCaptureStatus.Disconnected);
                    await DelayReconnectAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                TransitionTo(CameraCaptureStatus.Connected);
                while (!cancellationToken.IsCancellationRequested)
                {
                    CameraFrame? source = null;
                    try
                    {
                        if (!backend.TryRead(out source) || source is null)
                        {
                            break;
                        }

                        using (source)
                        {
                            var transformed = CameraFrameTransformer.Transform(
                                source,
                                _options.MirrorX,
                                _options.Rotation);
                            Volatile.Write(ref _actualWidth, transformed.Width);
                            Volatile.Write(ref _actualHeight, transformed.Height);
                            Interlocked.Exchange(
                                ref _lastFrameTimestampUnixMs,
                                transformed.Timestamp.ToUnixTimeMilliseconds());
                            Interlocked.Increment(ref _capturedFrames);
                            _latestFrames.Publish(transformed);
                        }
                    }
                    catch when (!cancellationToken.IsCancellationRequested)
                    {
                        source?.Dispose();
                        break;
                    }
                }

                backend.Close();
                if (!cancellationToken.IsCancellationRequested)
                {
                    TransitionTo(CameraCaptureStatus.Disconnected);
                    await DelayReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            TransitionTo(CameraCaptureStatus.Stopped);
        }
    }

    private async Task DelayReconnectAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
    }

    private void TransitionTo(CameraCaptureStatus status)
    {
        var previous = (CameraCaptureStatus)Interlocked.Exchange(ref _status, (int)status);
        if (previous == status)
        {
            return;
        }

        StatusChanged?.Invoke(this, new CameraCaptureStatusChangedEventArgs(previous, status));
    }
}
