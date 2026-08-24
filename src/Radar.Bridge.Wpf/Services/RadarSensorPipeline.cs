using System.Threading.Channels;
using System.IO;
using Microsoft.Extensions.Logging;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;
using Yuexin.Radar.Processing;
using Yuexin.Radar.Protocol;
using ConfigurationPixelRect = Yuexin.Radar.Configuration.RadarPixelRect;
using ContractPixelRect = Yuexin.Radar.Contracts.RadarPixelRect;

namespace Yuexin.Radar.Bridge.Wpf.Services;

public sealed class RadarSensorPipeline : IRadarSensorPipeline
{
    internal const int RecordingQueueCapacity = 32;

    private readonly ILogger<RadarSensorPipeline> _logger;
    private readonly SensorRuntimeOptions _options;
    private readonly Func<string, Stream> _recordingStreamFactory;
    private readonly Channel<RadarScanFrame> _latestFrames = Channel.CreateBounded<RadarScanFrame>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _recordingLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly RadarReplayGate _replayGate = new();
    private readonly object _snapshotGate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _sourceTask;
    private Task? _processingTask;
    private RadarConnectionService? _connectionService;
    private RadarRecordingWriter? _recordingWriter;
    private Stream? _recordingStream;
    private RecordingSession? _activeRecording;
    private Action<ReadOnlyMemory<byte>>? _connectionBytesReceivedHandler;
    private Action<RadarConnectionState>? _connectionStateChangedHandler;
    private int _state = (int)RadarSensorRuntimeState.Stopped;
    private int _pendingFrame;
    private int _disposed;
    private int _activeSource = -1;
    private long _runGeneration;
    private long _recordingSession;
    private int _recordingWriterTaskStartCount;
    private long _droppedInputFrameCount;
    private long _lastSequence;
    private long _snapshotSequence;
    private DateTimeOffset _lastSnapshotTimestamp = DateTimeOffset.MinValue;

    internal RadarSensorPipeline(
        RadarScreenConfiguration screen,
        RadarSensorConfiguration sensor,
        ILogger<RadarSensorPipeline> logger,
        Func<string, Stream>? recordingStreamFactory = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = SensorRuntimeOptions.Create(screen, sensor);
        _recordingStreamFactory = recordingStreamFactory ?? OpenRecordingStream;
    }

    public string ScreenId => _options.ScreenId;
    public string SensorId => _options.SensorId;
    public RadarSensorRuntimeState State => (RadarSensorRuntimeState)Volatile.Read(ref _state);
    public long DroppedInputFrameCount => Interlocked.Read(ref _droppedInputFrameCount);
    internal int PendingRecordingEntryCount => Volatile.Read(ref _activeRecording)?.PendingCount ?? 0;
    internal int RecordingWriterTaskStartCount => Volatile.Read(ref _recordingWriterTaskStartCount);

    public event Action<RadarSensorRuntimeSnapshot>? SnapshotUpdated;
    public event Action<SensorDetectionFrame>? DetectionFrameUpdated;
    public event Action<RadarSensorRuntimeState>? StateChanged;
    public event Action<string>? LogReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is RadarSensorRuntimeState.Starting or RadarSensorRuntimeState.Running or RadarSensorRuntimeState.Reconnecting)
            {
                return;
            }

            // A faulted processor cancels its run before its source-completion callback can
            // acquire this lock. Reap that complete generation before replacing any fields.
            if (_runCancellation is not null || _sourceTask is not null || _processingTask is not null || _connectionService is not null)
            {
                await StopCoreAsync().ConfigureAwait(false);
            }

            ValidateConfiguredSource();
            SetState(RadarSensorRuntimeState.Starting);
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            _processingTask = ProcessFramesAsync(_runCancellation);
            Volatile.Write(ref _activeSource, (int)_options.SourceMode);
            var generation = Interlocked.Increment(ref _runGeneration);
            _sourceTask = StartConfiguredSourceAsync(_runCancellation.Token, generation);
            ObserveSourceCompletion(_sourceTask);
            if (_options.SourceMode != RadarSensorSourceMode.Real)
            {
                SetState(RadarSensorRuntimeState.Running);
            }
            PublishLog($"Started {_options.SourceMode} source.");
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StartRecordingAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _recordingLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (Volatile.Read(ref _activeSource) != (int)RadarSensorSourceMode.Real || State != RadarSensorRuntimeState.Running)
                {
                    throw new InvalidOperationException("Only a Real radar source can record raw TCP bytes.");
                }

                await StopRecordingCoreAsync().ConfigureAwait(false);
                var stream = _recordingStreamFactory(fullPath);
                var writer = new RadarRecordingWriter(stream, leaveOpen: true);
                try
                {
                    await writer.InitializeAsync(new RadarRecordingHeader(
                        _options.DeviceModel,
                        _options.ConfigurationSnapshotJson,
                        null,
                        DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                    var session = new RecordingSession(
                        writer,
                        stream,
                        Volatile.Read(ref _runGeneration),
                        Interlocked.Increment(ref _recordingSession));
                    _recordingStream = stream;
                    _recordingWriter = writer;
                    Volatile.Write(ref _activeRecording, session);
                    Interlocked.Increment(ref _recordingWriterTaskStartCount);
                    session.WriterTask = RunRecordingWriterAsync(session);
                }
                catch
                {
                    await writer.DisposeAsync().ConfigureAwait(false);
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            finally
            {
                _recordingLock.Release();
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        PublishLog($"Recording raw TCP bytes to {fullPath}.");
    }

    public async Task StopRecordingAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopRecordingUnderLifecycleAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateReplay(path, speed);
        var fullPath = Path.GetFullPath(path);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetState(RadarSensorRuntimeState.Starting);
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
            _processingTask = ProcessFramesAsync(_runCancellation);
            Volatile.Write(ref _activeSource, (int)RadarSensorSourceMode.Replay);
            Interlocked.Increment(ref _runGeneration);
            _sourceTask = RunSourceSafelyAsync(
                token => ReplayCoreAsync(fullPath, speed, loop, token),
                _runCancellation.Token);
            ObserveSourceCompletion(_sourceTask);
            SetState(RadarSensorRuntimeState.Running);
            PublishLog($"Replaying {fullPath} at {speed:0.0}x{(loop ? " (loop)" : string.Empty)}.");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void PauseReplay()
    {
        if (Volatile.Read(ref _activeSource) != (int)RadarSensorSourceMode.Replay) return;
        _replayGate.Pause();
        PublishLog("Replay paused.");
    }

    public void ResumeReplay()
    {
        if (Volatile.Read(ref _activeSource) != (int)RadarSensorSourceMode.Replay) return;
        _replayGate.Resume();
        PublishLog("Replay resumed.");
    }

    public void StepReplay()
    {
        if (Volatile.Read(ref _activeSource) != (int)RadarSensorSourceMode.Replay) return;
        _replayGate.Step();
        PublishLog("Replay advanced by one frame.");
    }

    public async Task StopReplayAsync()
    {
        if (Volatile.Read(ref _activeSource) == (int)RadarSensorSourceMode.Replay)
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    internal void PublishScan(RadarScanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Interlocked.Exchange(ref _pendingFrame, 1) != 0)
        {
            Interlocked.Increment(ref _droppedInputFrameCount);
        }

        if (!_latestFrames.Writer.TryWrite(frame))
        {
            Interlocked.Exchange(ref _pendingFrame, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        await StopAsync().ConfigureAwait(false);
        await StopRecordingAsync().ConfigureAwait(false);
        _latestFrames.Writer.TryComplete();
        _lifecycleLock.Dispose();
        _recordingLock.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task StopCoreAsync()
    {
        var cancellation = _runCancellation;
        var source = _sourceTask;
        var processor = _processingTask;
        var service = _connectionService;
        _runCancellation = null;
        _sourceTask = null;
        _processingTask = null;
        _connectionService = null;
        Volatile.Write(ref _activeSource, -1);
        Interlocked.Increment(ref _runGeneration);
        DetachRecordingSourceHandlers(service);
        cancellation?.Cancel();
        _replayGate.Resume();
        await StopRecordingUnderLifecycleAsync().ConfigureAwait(false);

        if (source is not null)
        {
            await AwaitCooperativeTaskAsync(source).ConfigureAwait(false);
        }

        if (service is not null)
        {
            await service.DisposeAsync().ConfigureAwait(false);
        }

        if (processor is not null)
        {
            await AwaitCooperativeTaskAsync(processor).ConfigureAwait(false);
        }

        cancellation?.Dispose();
        while (_latestFrames.Reader.TryRead(out _)) { }
        Interlocked.Exchange(ref _pendingFrame, 0);
        if (State != RadarSensorRuntimeState.Stopped)
        {
            PublishEmptyFrame();
            SetState(RadarSensorRuntimeState.Stopped);
            PublishLog("Stopped.");
        }
    }

    private Task StartConfiguredSourceAsync(CancellationToken cancellationToken, long generation)
    {
        return _options.SourceMode switch
        {
            RadarSensorSourceMode.Real => StartRealSource(cancellationToken, generation),
            RadarSensorSourceMode.Simulation => RunSourceSafelyAsync(GenerateSimulationAsync, cancellationToken),
            RadarSensorSourceMode.Replay => RunSourceSafelyAsync(
                token => ReplayCoreAsync(_options.ReplayFilePath, _options.ReplaySpeed, _options.ReplayLoop, token),
                cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported source mode {_options.SourceMode}.")
        };
    }

    private Task StartRealSource(CancellationToken cancellationToken, long generation)
    {
        var service = new RadarConnectionService(new RadarConnectionOptions
        {
            RadarIp = _options.RadarIp,
            Port = _options.Port,
            LocalIp = _options.LocalIp,
            AutoReconnect = _options.AutoReconnect
        });
        _connectionStateChangedHandler = state => OnConnectionStateChanged(state, generation);
        _connectionBytesReceivedHandler = bytes => QueueRecordingBytes(bytes, generation);
        service.StateChanged += _connectionStateChangedHandler;
        service.ConnectionError += exception => PublishLog($"Radar connection error: {exception.Message}");
        service.DataWarning += elapsed => PublishLog($"No radar data for {elapsed.TotalMilliseconds:0} ms.");
        service.BytesReceived += _connectionBytesReceivedHandler;
        service.ScanFrameReceived += PublishScan;
        _connectionService = service;
        return RunSourceSafelyAsync(service.RunAsync, cancellationToken);
    }

    private void DetachRecordingSourceHandlers(RadarConnectionService? service)
    {
        if (service is null)
        {
            _connectionBytesReceivedHandler = null;
            _connectionStateChangedHandler = null;
            return;
        }

        if (_connectionBytesReceivedHandler is not null)
        {
            service.BytesReceived -= _connectionBytesReceivedHandler;
            _connectionBytesReceivedHandler = null;
        }

        if (_connectionStateChangedHandler is not null)
        {
            service.StateChanged -= _connectionStateChangedHandler;
            _connectionStateChangedHandler = null;
        }
    }

    private async Task RunSourceSafelyAsync(Func<CancellationToken, Task> source, CancellationToken cancellationToken)
    {
        try
        {
            await source(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal cooperative shutdown.
        }
        catch (Exception exception)
        {
            PublishLog($"Source failed: {exception.Message}");
            SetState(RadarSensorRuntimeState.Faulted);
        }
    }

    private async Task ProcessFramesAsync(CancellationTokenSource runCancellation)
    {
        var cancellationToken = runCancellation.Token;
        DateTimeOffset previousTimestamp = DateTimeOffset.MinValue;
        var previousBytes = 0L;
        try
        {
            await foreach (var frame in _latestFrames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref _pendingFrame, 0);
                Interlocked.Exchange(ref _lastSequence, frame.Sequence);
                var transformed = frame.Points.Select(point => RadarCoordinateConverter.ApplyTransform(point, _options.Transform)).ToArray();
                var valid = RadarPointFilter.Apply(transformed, _options.Filter).ToArray();
                var clusters = new SequentialPointClusterer(_options.Clustering).Cluster(valid).Select(CloneCluster).ToArray();
                var detections = MapClusters(clusters).ToArray();
                var elapsed = previousTimestamp == DateTimeOffset.MinValue ? 1d / _options.DefaultScanFrequencyHz : Math.Max(0.001d, (frame.Timestamp - previousTimestamp).TotalSeconds);
                var bytes = _connectionService?.Metrics.ReceivedByteCount ?? frame.Points.Count * 4L;
                var frequency = 1d / elapsed;
                var byteRate = Math.Max(0d, (bytes - previousBytes) / elapsed);
                previousTimestamp = frame.Timestamp;
                previousBytes = bytes;
                var (sequence, timestamp) = NextSnapshotMetadata(frame.Timestamp);
                var snapshot = new RadarSensorRuntimeSnapshot(ScreenId, SensorId, sequence, timestamp, Freeze(frame.Points), Freeze(valid), Freeze(clusters), Freeze(detections), frequency, byteRate, _connectionService?.Metrics.CrcErrorCount ?? 0, _connectionService?.Metrics.DiscardedByteCount ?? 0, DroppedInputFrameCount);
                InvokeSafely(SnapshotUpdated, snapshot);
                InvokeSafely(DetectionFrameUpdated, new SensorDetectionFrame(SensorId, timestamp, detections));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishLog($"Processing failed: {exception.Message}");
            SetState(RadarSensorRuntimeState.Faulted);
            runCancellation.Cancel();
        }
    }

    private async Task GenerateSimulationAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / _options.DefaultScanFrequencyHz));
        var sequence = Interlocked.Read(ref _lastSequence);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            sequence++;
            var phase = sequence * 0.045f;
            var points = new List<RadarPoint>();
            AddSyntheticTarget(points, 1.8f, 60f + MathF.Sin(phase) * 30f, 9);
            AddSyntheticTarget(points, 2.8f, 170f + MathF.Cos(phase * 0.8f) * 18f, 7);
            points.Sort((left, right) => left.AngleRaw.CompareTo(right.AngleRaw));
            PublishScan(new RadarScanFrame(sequence, DateTimeOffset.UtcNow, points));
        }
    }

    private async Task ReplayCoreAsync(string path, double speed, bool loop, CancellationToken cancellationToken)
    {
        do
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var reader = new RadarRecordingReader(stream, leaveOpen: true);
            await reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            var decoder = new RadarByteStreamDecoder();
            var builder = new RadarScanFrameBuilder(minimumValidPointCount: 2);
            DateTimeOffset? previousTimestamp = null;
            var publishedFrame = false;
            await foreach (var entry in reader.ReadEntriesAsync(cancellationToken).ConfigureAwait(false))
            {
                if (entry.EntryType != RadarRecordingEntryType.RawBytes)
                {
                    continue;
                }

                if (previousTimestamp.HasValue)
                {
                    var delay = TimeSpan.FromTicks((long)((entry.Timestamp - previousTimestamp.Value).Ticks / speed));
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                previousTimestamp = entry.Timestamp;
                foreach (var point in decoder.Append(entry.Payload))
                {
                    var frame = builder.AddPoint(point, entry.Timestamp);
                    if (frame is not null)
                    {
                        await _replayGate.WaitForFrameAsync(cancellationToken).ConfigureAwait(false);
                        PublishScan(frame);
                        publishedFrame = true;
                    }
                }
            }

            if (loop && !publishedFrame)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
        while (loop && !cancellationToken.IsCancellationRequested);
    }

    private IEnumerable<SensorDetection> MapClusters(IReadOnlyList<RadarCluster> clusters)
    {
        foreach (var cluster in clusters)
        {
            if (!TryMapPoint(cluster.CenterX, cluster.CenterY, out var center))
            {
                continue;
            }

            var footprint = new RadarScreenPoint[cluster.Points.Count];
            var footprintIsValid = true;
            for (var index = 0; index < cluster.Points.Count; index++)
            {
                var actualPoint = cluster.Points[index];
                if (TryMapPoint(actualPoint.X, actualPoint.Y, out footprint[index]))
                {
                    continue;
                }

                footprintIsValid = false;
                PublishLog($"Rejected cluster {cluster.ClusterIndex} because its footprint contains an unmappable actual point.");
                break;
            }

            if (!footprintIsValid)
            {
                continue;
            }

            yield return new SensorDetection(
                cluster.ClusterIndex,
                center.PixelX,
                center.PixelY,
                Math.Clamp(cluster.Points.Count / 10f, 0f, 1f),
                footprint);
        }
    }

    private bool TryMapPoint(float physicalX, float physicalY, out RadarScreenPoint point)
    {
        point = default;
        if (!_options.Calibration.TryMap(physicalX, physicalY, out var localX, out var localY) ||
            !float.IsFinite(localX) || !float.IsFinite(localY) ||
            localX is < 0f or > 1f || localY is < 0f or > 1f)
        {
            return false;
        }

        var mapped = _options.OutputMapper.Map(localX, localY);
        if (!float.IsFinite(mapped.PixelX) || !float.IsFinite(mapped.PixelY) ||
            mapped.PixelX < 0f || mapped.PixelX > _options.ScreenWidth ||
            mapped.PixelY < 0f || mapped.PixelY > _options.ScreenHeight)
        {
            return false;
        }

        point = new RadarScreenPoint(mapped.PixelX, mapped.PixelY);
        return true;
    }

    private void OnConnectionStateChanged(RadarConnectionState state, long generation)
    {
        if (generation != Volatile.Read(ref _runGeneration)) return;
        switch (state)
        {
            case RadarConnectionState.Connected:
                SetState(RadarSensorRuntimeState.Running);
                break;
            case RadarConnectionState.Reconnecting:
            case RadarConnectionState.Connecting:
                SetState(RadarSensorRuntimeState.Reconnecting);
                break;
            case RadarConnectionState.Faulted:
                SetState(RadarSensorRuntimeState.Faulted);
                break;
        }

        QueueRecordingItem(RecordingWorkItem.ForState(state, DateTimeOffset.UtcNow), generation);
    }

    internal void EnqueueRecordingBytesForTests(ReadOnlyMemory<byte> bytes) =>
        QueueRecordingBytes(bytes, Volatile.Read(ref _runGeneration));

    private void QueueRecordingBytes(ReadOnlyMemory<byte> bytes, long generation)
    {
        QueueRecordingItem(RecordingWorkItem.ForBytes(bytes, DateTimeOffset.UtcNow), generation);
    }

    private void QueueRecordingItem(RecordingWorkItem item, long generation)
    {
        var session = Volatile.Read(ref _activeRecording);
        if (session is null || session.Generation != generation || !session.TryEnterProducer())
        {
            return;
        }

        try
        {
            lock (session.EnqueueGate)
            {
                if (!ReferenceEquals(session, Volatile.Read(ref _activeRecording)) ||
                    session.Generation != Volatile.Read(ref _runGeneration) ||
                    !session.IsAccepting)
                {
                    return;
                }

                // The radar receive callback is the producer. Waiting here applies deterministic
                // backpressure to TCP reads instead of allocating an unbounded task per block.
                session.Queue.Writer.WriteAsync(item).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (exception is ChannelClosedException or OperationCanceledException)
        {
            if (session.IsAccepting)
            {
                PublishLog($"Recording queue closed: {exception.Message}");
            }
        }
        finally
        {
            session.ExitProducer();
        }
    }

    private async Task RunRecordingWriterAsync(RecordingSession session)
    {
        try
        {
            await foreach (var item in session.Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (item.EntryType == RadarRecordingEntryType.RawBytes)
                {
                    await session.Writer.WriteDataAsync(item.Bytes, item.Timestamp).ConfigureAwait(false);
                }
                else
                {
                    await session.Writer.WriteConnectionStateAsync(item.State, item.Timestamp).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            session.Fail(exception);
            PublishLog($"Recording writer failed: {exception.Message}");
        }
    }

    private async Task StopRecordingCoreAsync()
    {
        Interlocked.Increment(ref _recordingSession);
        var session = Interlocked.Exchange(ref _activeRecording, null);
        if (session is not null)
        {
            await session.StopAcceptingAsync().ConfigureAwait(false);
            session.Queue.Writer.TryComplete();
            await session.WriterTask.ConfigureAwait(false);

            Interlocked.CompareExchange(ref _recordingWriter, null, session.Writer);
            Interlocked.CompareExchange(ref _recordingStream, null, session.Stream);
            try
            {
                await session.Writer.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await session.Stream.DisposeAsync().ConfigureAwait(false);
            }
            return;
        }

        var writer = Interlocked.Exchange(ref _recordingWriter, null);
        var stream = Interlocked.Exchange(ref _recordingStream, null);
        if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
    }

    // Lifecycle transitions always acquire the lifecycle lock before this recording lock.
    private async Task StopRecordingUnderLifecycleAsync()
    {
        await _recordingLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopRecordingCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _recordingLock.Release();
        }
    }

    private void ValidateConfiguredSource()
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Disabled sensors cannot be started.");
        }

        if (_options.SourceMode == RadarSensorSourceMode.Replay)
        {
            ValidateReplay(_options.ReplayFilePath, _options.ReplaySpeed);
        }
    }

    private static void ValidateReplay(string path, double speed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!double.IsFinite(speed) || speed is < 0.1d or > 8d)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "Replay speed must be finite and between 0.1 and 8.0.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Select an existing .radarrec file before starting Replay.", fullPath);
        }
    }

    private static Stream OpenRecordingStream(string path) => new FileStream(
        path,
        FileMode.Create,
        FileAccess.Write,
        FileShare.Read,
        64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private void PublishEmptyFrame()
    {
        var (sequence, timestamp) = NextSnapshotMetadata(DateTimeOffset.UtcNow);
        var snapshot = new RadarSensorRuntimeSnapshot(
            ScreenId, SensorId, sequence, timestamp, [], [], [], [], 0d, 0d, 0L, 0L, DroppedInputFrameCount);
        InvokeSafely(SnapshotUpdated, snapshot);
        InvokeSafely(DetectionFrameUpdated, new SensorDetectionFrame(SensorId, timestamp, []));
    }

    private (long Sequence, DateTimeOffset Timestamp) NextSnapshotMetadata(DateTimeOffset sourceTimestamp)
    {
        lock (_snapshotGate)
        {
            var timestamp = sourceTimestamp <= _lastSnapshotTimestamp
                ? _lastSnapshotTimestamp.AddTicks(1)
                : sourceTimestamp;
            _lastSnapshotTimestamp = timestamp;
            return (Interlocked.Increment(ref _snapshotSequence), timestamp);
        }
    }

    private void ObserveSourceCompletion(Task sourceTask) => _ = CompleteSourceAsync(sourceTask);

    private async Task CompleteSourceAsync(Task sourceTask)
    {
        await sourceTask.ConfigureAwait(false);
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_sourceTask, sourceTask)) return;
            var faulted = State == RadarSensorRuntimeState.Faulted;
            await StopCoreAsync().ConfigureAwait(false);
            if (faulted) SetState(RadarSensorRuntimeState.Faulted);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void SetState(RadarSensorRuntimeState state)
    {
        if ((RadarSensorRuntimeState)Interlocked.Exchange(ref _state, (int)state) != state)
        {
            InvokeSafely(StateChanged, state);
        }
    }

    private void PublishLog(string message)
    {
        var tagged = $"[{ScreenId}/{SensorId}] {message}";
        _logger.LogInformation("{Message}", tagged);
        InvokeSafely(LogReceived, tagged);
    }

    private static void AddSyntheticTarget(ICollection<RadarPoint> points, float distanceMeters, float centerAngle, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var angle = centerAngle + (index - (count - 1) / 2f) * 0.75f;
            var radians = angle * MathF.PI / 180f;
            var distance = distanceMeters + (index % 2 == 0 ? 0.01f : -0.01f);
            points.Add(new RadarPoint(
                (int)MathF.Round(distance * 100f),
                (int)MathF.Round(angle * 100f),
                angle,
                distance * MathF.Cos(radians),
                distance * MathF.Sin(radians)));
        }
    }

    private static RadarCluster CloneCluster(RadarCluster cluster) => new(
        cluster.ClusterIndex,
        Freeze(cluster.Points),
        cluster.CenterX,
        cluster.CenterY,
        cluster.WidthMeters,
        cluster.EstimatedDistanceMeters);

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> items) => Array.AsReadOnly(items.ToArray());

    private static async Task AwaitCooperativeTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The pipeline owns the cancellation token and treats it as a normal stop.
        }
    }

    private static void InvokeSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch
            {
                // A UI observer must not stop this sensor pipeline or its peers.
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class RecordingSession
    {
        private readonly object _producerGate = new();
        private TaskCompletionSource? _producersDrained;
        private bool _accepting = true;
        private int _activeProducers;

        public RecordingSession(RadarRecordingWriter writer, Stream stream, long generation, long sessionId)
        {
            Writer = writer;
            Stream = stream;
            Generation = generation;
            SessionId = sessionId;
            Queue = Channel.CreateBounded<RecordingWorkItem>(new BoundedChannelOptions(RecordingQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        }

        public RadarRecordingWriter Writer { get; }
        public Stream Stream { get; }
        public long Generation { get; }
        public long SessionId { get; }
        public Channel<RecordingWorkItem> Queue { get; }
        public object EnqueueGate { get; } = new();
        public Task WriterTask { get; set; } = Task.CompletedTask;
        public Exception? Failure { get; private set; }
        public int PendingCount => Queue.Reader.Count;

        public bool IsAccepting
        {
            get
            {
                lock (_producerGate)
                {
                    return _accepting;
                }
            }
        }

        public bool TryEnterProducer()
        {
            lock (_producerGate)
            {
                if (!_accepting)
                {
                    return false;
                }

                _activeProducers++;
                return true;
            }
        }

        public void ExitProducer()
        {
            TaskCompletionSource? completed = null;
            lock (_producerGate)
            {
                _activeProducers--;
                if (_activeProducers == 0 && !_accepting)
                {
                    completed = _producersDrained;
                }
            }

            completed?.TrySetResult();
        }

        public Task StopAcceptingAsync()
        {
            lock (_producerGate)
            {
                _accepting = false;
                if (_activeProducers == 0)
                {
                    return Task.CompletedTask;
                }

                _producersDrained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _producersDrained.Task;
            }
        }

        public void Fail(Exception exception)
        {
            Failure = exception;
            _ = StopAcceptingAsync();
            Queue.Writer.TryComplete(exception);
        }
    }

    private sealed record RecordingWorkItem(
        RadarRecordingEntryType EntryType,
        ReadOnlyMemory<byte> Bytes,
        RadarConnectionState State,
        DateTimeOffset Timestamp)
    {
        public static RecordingWorkItem ForBytes(ReadOnlyMemory<byte> bytes, DateTimeOffset timestamp) =>
            new(RadarRecordingEntryType.RawBytes, bytes, default, timestamp);

        public static RecordingWorkItem ForState(RadarConnectionState state, DateTimeOffset timestamp) =>
            new(RadarRecordingEntryType.ConnectionState, ReadOnlyMemory<byte>.Empty, state, timestamp);
    }

    private sealed record SensorRuntimeOptions(
        string ScreenId,
        string SensorId,
        bool Enabled,
        RadarSensorSourceMode SourceMode,
        RadarModel DeviceModel,
        string RadarIp,
        int Port,
        string LocalIp,
        bool AutoReconnect,
        string ReplayFilePath,
        double ReplaySpeed,
        bool ReplayLoop,
        int ScreenWidth,
        int ScreenHeight,
        double DefaultScanFrequencyHz,
        RadarTransformOptions Transform,
        RadarFilterOptions Filter,
        RadarClusteringOptions Clustering,
        HomographyCalibration Calibration,
        RadarOutputMapper OutputMapper,
        string ConfigurationSnapshotJson)
    {
        public static SensorRuntimeOptions Create(RadarScreenConfiguration screen, RadarSensorConfiguration sensor)
        {
            ArgumentNullException.ThrowIfNull(screen);
            ArgumentNullException.ThrowIfNull(sensor);
            var width = screen.EffectiveWidthPixels;
            var height = screen.EffectiveHeightPixels;
            if (!IsConfigurationId(screen.ScreenId) || !IsConfigurationId(sensor.SensorId) || width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(screen), "Screen and sensor identifiers and dimensions must be valid.");
            }

            var outputRect = sensor.OutputRectPixels;
            var mapper = new RadarOutputMapper(
                new ContractPixelRect(outputRect.X, outputRect.Y, outputRect.Width, outputRect.Height),
                width,
                height);
            var activePolygon = sensor.Range.ActivePolygon.Select(point => new Point2(point.X, point.Y)).ToArray();
            var calibrationCorners = sensor.Calibration.IsValid && sensor.Calibration.PhysicalCorners.Count == 4
                ? sensor.Calibration.PhysicalCorners.Select(point => new Point2(point.X, point.Y)).ToArray()
                : activePolygon;
            if (!HomographyCalibration.TryCreate(calibrationCorners, out var calibration, out var error))
            {
                throw new ArgumentException(error ?? "Sensor calibration or active polygon is invalid.", nameof(sensor));
            }

            var profile = RadarModelProfileFactory.Create(sensor.Device.DeviceModel);
            return new SensorRuntimeOptions(
                screen.ScreenId,
                sensor.SensorId,
                sensor.Enabled,
                sensor.SourceMode,
                sensor.Device.DeviceModel,
                sensor.Device.RadarIp,
                sensor.Device.Port,
                sensor.Device.LocalIp,
                sensor.Device.AutoReconnect,
                sensor.ReplayFilePath,
                sensor.ReplaySpeed,
                sensor.ReplayLoop,
                width,
                height,
                profile.DefaultScanFrequencyHz,
                new RadarTransformOptions
                {
                    RotationDegrees = sensor.Transform.RotationDegrees,
                    FlipX = sensor.Transform.FlipX,
                    FlipY = sensor.Transform.FlipY,
                    OffsetXMeters = sensor.Transform.OffsetXMeters,
                    OffsetYMeters = sensor.Transform.OffsetYMeters
                },
                new RadarFilterOptions
                {
                    MinimumDistanceMeters = sensor.Range.MinimumDistanceMeters,
                    MaximumDistanceMeters = Math.Min(sensor.Range.MaximumDistanceMeters, profile.MaximumDistanceMeters),
                    BlindZoneStartDegrees = profile.BlindZoneStartDegrees,
                    BlindZoneEndDegrees = profile.BlindZoneEndDegrees,
                    MinimumAngleDegrees = sensor.Range.MinimumAngleDegrees,
                    MaximumAngleDegrees = sensor.Range.MaximumAngleDegrees,
                    ActivePolygon = activePolygon,
                    MaskedPolygons = sensor.Range.MaskedPolygons
                        .Select(mask => (IReadOnlyList<Point2>)mask.Select(point => new Point2(point.X, point.Y)).ToArray())
                        .ToArray(),
                    LeftEdgeDeadZoneMeters = sensor.Range.EdgeDeadZones.LeftMeters,
                    RightEdgeDeadZoneMeters = sensor.Range.EdgeDeadZones.RightMeters,
                    TopEdgeDeadZoneMeters = sensor.Range.EdgeDeadZones.TopMeters,
                    BottomEdgeDeadZoneMeters = sensor.Range.EdgeDeadZones.BottomMeters
                },
                new RadarClusteringOptions
                {
                    BaseGapMeters = sensor.Clustering.BaseGapMeters,
                    DistanceScale = sensor.Clustering.DistanceScale,
                    MinimumClusterPointCount = sensor.Clustering.MinimumClusterPointCount,
                    MaximumClusterWidthMeters = sensor.Clustering.MaximumClusterWidthMeters
                },
                calibration!,
                mapper,
                RadarConfigurationStore.SerializeRuntimeSnapshot(sensor));
        }

        private static bool IsConfigurationId(string? value) =>
            value is { Length: > 0 and <= 64 } && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    }
}
