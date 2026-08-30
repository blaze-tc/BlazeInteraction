using Microsoft.Win32.SafeHandles;

namespace Blaze.Provider.CameraVision;

internal sealed class HandBackendException : Exception
{
    public HandBackendException(int statusCode, string operation, string nativeError)
        : base($"Native hand operation '{operation}' failed with status {statusCode}: {nativeError}")
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

internal sealed class MediaPipeHandBackend : IHandDetectionBackend
{
    private const int MaxNativeErrorUtf8Bytes = 4096;
    private readonly INativeHandLibrary _native;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NativeHandSafeHandle? _handle;
    private long _lastTimestampUnixMs = long.MinValue;
    private int _status = (int)HandBackendStatus.Uninitialized;
    private int _disposeStarted;

    public MediaPipeHandBackend() : this(new NativeHandLibrary())
    {
    }

    internal MediaPipeHandBackend(INativeHandLibrary native)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
    }

    public HandBackendStatus Status => (HandBackendStatus)Volatile.Read(ref _status);

    public async Task InitializeAsync(
        HandDetectionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Status != HandBackendStatus.Uninitialized)
            {
                throw new InvalidOperationException("The hand backend can only be initialized once.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            SetStatus(HandBackendStatus.Initializing);
            var abiVersion = _native.GetAbiVersion();
            if (abiVersion != NativeHandLibrary.AbiVersion)
            {
                throw new InvalidOperationException(
                    $"Native hand ABI mismatch. Expected {NativeHandLibrary.AbiVersion}, actual {abiVersion}.");
            }

            var createStatus = _native.Create(options, out var rawHandle);
            if (createStatus != 0)
            {
                HandBackendException failure;
                try
                {
                    failure = NativeFailure(createStatus, rawHandle, "create");
                }
                finally
                {
                    if (rawHandle != 0)
                    {
                        _native.Destroy(rawHandle);
                    }
                }

                throw failure;
            }

            if (rawHandle == 0)
            {
                throw new InvalidOperationException("Native hand creation returned a null handle.");
            }

            _handle = new NativeHandSafeHandle(_native, rawHandle);
            SetStatus(HandBackendStatus.Ready);
        }
        catch (OperationCanceledException)
        {
            if (Status == HandBackendStatus.Initializing)
            {
                SetStatus(HandBackendStatus.Uninitialized);
            }

            throw;
        }
        catch
        {
            SetStatus(HandBackendStatus.Faulted);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<HandDetectionResult> DetectAsync(
        RgbFrameView frame,
        long timestampUnixMs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Status != HandBackendStatus.Ready || _handle is null || _handle.IsInvalid)
            {
                throw new InvalidOperationException("The hand backend is not ready.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (timestampUnixMs <= _lastTimestampUnixMs)
            {
                throw new ArgumentOutOfRangeException(nameof(timestampUnixMs),
                    "Hand detection timestamps must be strictly increasing.");
            }

            var rawHandle = _handle.DangerousGetHandle();
            var processStatus = _native.ProcessFrame(rawHandle, frame, timestampUnixMs);
            if (processStatus != 0)
            {
                SetStatus(HandBackendStatus.Faulted);
                throw NativeFailure(processStatus, rawHandle, "process frame");
            }

            _lastTimestampUnixMs = timestampUnixMs;
            var countStatus = _native.GetHandCount(rawHandle, out var handCount);
            if (countStatus != 0)
            {
                SetStatus(HandBackendStatus.Faulted);
                throw NativeFailure(countStatus, rawHandle, "get hand count");
            }

            if (handCount < 0)
            {
                SetStatus(HandBackendStatus.Faulted);
                throw new InvalidDataException("Native hand count cannot be negative.");
            }

            var hands = new DetectedHand[handCount];
            for (var index = 0; index < handCount; index++)
            {
                var copyStatus = _native.CopyHand(rawHandle, index, out var nativeHand);
                if (copyStatus != 0)
                {
                    SetStatus(HandBackendStatus.Faulted);
                    throw NativeFailure(copyStatus, rawHandle, $"copy hand {index}");
                }

                if (nativeHand is null)
                {
                    SetStatus(HandBackendStatus.Faulted);
                    throw new InvalidDataException("Native hand copy returned no result.");
                }

                hands[index] = new DetectedHand(
                    nativeHand.Confidence,
                    nativeHand.Landmarks.Select(static landmark =>
                        new HandLandmark(landmark.X, landmark.Y, landmark.Z)));
            }

            return new HandDetectionResult(hands);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _handle?.Dispose();
            _handle = null;
            _native.Dispose();
            SetStatus(HandBackendStatus.Disposed);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private HandBackendException NativeFailure(int status, nint handle, string operation)
    {
        var error = _native.GetLastError(handle, MaxNativeErrorUtf8Bytes);
        if (string.IsNullOrWhiteSpace(error))
        {
            error = "No native error text was provided.";
        }

        return new HandBackendException(status, operation, error);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
    }

    private void SetStatus(HandBackendStatus status)
    {
        Volatile.Write(ref _status, (int)status);
    }

    private sealed class NativeHandSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly INativeHandLibrary _native;

        public NativeHandSafeHandle(INativeHandLibrary native, nint handle) : base(true)
        {
            _native = native;
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => _native.Destroy(handle) == 0;
    }
}
