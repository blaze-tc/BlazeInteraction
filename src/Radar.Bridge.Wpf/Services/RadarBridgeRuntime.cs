using System.Text.Json;
using System.Threading.Channels;
using System.IO;
using Microsoft.Extensions.Logging;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;
using Yuexin.Radar.Ipc;
using Yuexin.Radar.Processing;
using Yuexin.Radar.Protocol;

namespace Yuexin.Radar.Bridge.Wpf.Services;

public sealed class RadarBridgeRuntime : IRadarBridgeRuntime
{
    private readonly RadarAppConfiguration _configuration;
    private readonly ILogger<RadarBridgeRuntime> _logger;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _sourceTransitionLock = new(1, 1);
    private readonly RadarReplayGate _replayGate = new();
    private readonly Channel<RadarScanFrame> _latestFrames = Channel.CreateBounded<RadarScanFrame>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private RadarPipeServer? _pipeServer;
    private Task? _pipeTask;
    private Task? _processingTask;
    private RadarConnectionService? _connectionService;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _connectionTask;
    private CancellationTokenSource? _simulationCancellation;
    private Task? _simulationTask;
    private CancellationTokenSource? _replayCancellation;
    private Task? _replayTask;
    private RadarRecordingWriter? _recordingWriter;
    private Stream? _recordingStream;
    private int _disposed;
    private int _infrastructureStarted;
    private long _ipcSequence;
    private IReadOnlyList<RadarScreenInfo> _unityScreens = Array.Empty<RadarScreenInfo>();
    private UnityClientStatus _unityStatus = UnityClientStatus.Disconnected;

    public RadarBridgeRuntime(
        RadarAppConfiguration configuration,
        ILogger<RadarBridgeRuntime> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event Action<RadarRuntimeSnapshot>? SnapshotUpdated;
    public event Action<string>? LogReceived;
    public event Action<RadarConnectionState>? ConnectionStateChanged;
    public event Action<UnityClientStatus>? UnityStatusChanged;

    public RadarConnectionState ConnectionState =>
        _connectionService?.State ?? (_simulationTask is not null ? RadarConnectionState.Connected : RadarConnectionState.Disconnected);

    public UnityClientStatus UnityStatus => _unityStatus;

    public Task StartInfrastructureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _infrastructureStarted, 1) != 0)
        {
            return Task.CompletedTask;
        }

        try
        {
            _pipeServer = new RadarPipeServer(new RadarPipeServerOptions
            {
                PipeName = _configuration.Ipc.PipeName,
                HeartbeatTimeout = TimeSpan.FromSeconds(3),
                HelloAckFactory = () => new HelloAckPayload(
                    BridgeVersion.Value,
                    IpcProtocolVersion.Current,
                    _connectionService?.State == RadarConnectionState.Connected || _simulationTask is not null,
                    "multi-screen",
                    [])
            });
            _pipeServer.ClientConnected += OnUnityConnected;
            _pipeServer.ClientDisconnected += OnUnityDisconnected;
            _pipeServer.ClientError += exception => PublishLog($"Unity IPC：{exception.Message}");
            _pipeServer.MessageReceived += OnIpcMessage;
            _pipeTask = _pipeServer.RunAsync(_lifetimeCancellation.Token);
            _processingTask = ProcessFramesAsync(_lifetimeCancellation.Token);
            PublishLog($"IPC 服务已启动：{_configuration.Ipc.PipeName} / 协议 v{IpcProtocolVersion.Current}");
            return Task.CompletedTask;
        }
        catch
        {
            _pipeServer = null;
            _pipeTask = null;
            _processingTask = null;
            Interlocked.Exchange(ref _infrastructureStarted, 0);
            throw;
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sourceTransitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartInfrastructureAsync(cancellationToken).ConfigureAwait(false);
            await StopSimulationCoreAsync().ConfigureAwait(false);
            await StopReplayCoreAsync().ConfigureAwait(false);
            await DisconnectCoreAsync().ConfigureAwait(false);

            await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var options = new RadarConnectionOptions
                {
                    RadarIp = _configuration.Device.RadarIp,
                    Port = _configuration.Device.Port,
                    LocalIp = _configuration.Device.LocalIp,
                    AutoReconnect = _configuration.Device.AutoReconnect
                };
                var service = new RadarConnectionService(options);
                service.StateChanged += OnConnectionStateChanged;
                service.ConnectionError += exception => PublishLog($"雷达连接：{exception.Message}");
                service.DataWarning += elapsed => PublishLog($"数据预警：{elapsed.TotalMilliseconds:0} ms 未收到雷达数据。");
                service.BytesReceived += OnRawBytesReceived;
                service.ScanFrameReceived += frame => _latestFrames.Writer.TryWrite(frame);
                _connectionService = service;
                _connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetimeCancellation.Token);
                _connectionTask = service.RunAsync(_connectionCancellation.Token);
                PublishLog($"正在连接 {_configuration.Device.RadarIp}:{_configuration.Device.Port}（{_configuration.Device.DeviceModel}）。");
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        finally
        {
            _sourceTransitionLock.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _sourceTransitionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _sourceTransitionLock.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var cancellation = _connectionCancellation;
            var task = _connectionTask;
            var service = _connectionService;
            _connectionCancellation = null;
            _connectionTask = null;
            _connectionService = null;
            cancellation?.Cancel();
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }

            cancellation?.Dispose();
            if (service is not null)
            {
                await service.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StartSimulationAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sourceTransitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartInfrastructureAsync(cancellationToken).ConfigureAwait(false);
            await DisconnectCoreAsync().ConfigureAwait(false);
            await StopReplayCoreAsync().ConfigureAwait(false);
            await StopSimulationCoreAsync().ConfigureAwait(false);

            _simulationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            _simulationTask = GenerateSimulationAsync(_simulationCancellation.Token);
            InvokeSafely(ConnectionStateChanged, RadarConnectionState.Connected);
            PublishLog("合成点云模拟已启动；现场验收仍须使用真实 .radarrec 数据。");
        }
        finally
        {
            _sourceTransitionLock.Release();
        }
    }

    public async Task StopSimulationAsync()
    {
        await _sourceTransitionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopSimulationCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _sourceTransitionLock.Release();
        }
    }

    private async Task StopSimulationCoreAsync()
    {
        var cancellation = Interlocked.Exchange(ref _simulationCancellation, null);
        var task = Interlocked.Exchange(ref _simulationTask, null);
        cancellation?.Cancel();
        if (task is not null)
        {
            await AwaitCooperativeTaskAsync(task).ConfigureAwait(false);
            InvokeSafely(ConnectionStateChanged, RadarConnectionState.Disconnected);
        }

        cancellation?.Dispose();
    }

    public async Task StartRecordingAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await StopRecordingAsync().ConfigureAwait(false);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var writer = new RadarRecordingWriter(stream, leaveOpen: true);
        var header = new RadarRecordingHeader(
            _configuration.Device.DeviceModel,
            JsonSerializer.Serialize(_configuration),
            null,
            DateTimeOffset.UtcNow);
        await writer.InitializeAsync(header, cancellationToken).ConfigureAwait(false);
        _recordingStream = stream;
        _recordingWriter = writer;
        PublishLog($"开始录制原始 TCP 数据：{fullPath}");
    }

    public async Task StopRecordingAsync()
    {
        var writer = Interlocked.Exchange(ref _recordingWriter, null);
        var stream = Interlocked.Exchange(ref _recordingStream, null);
        if (writer is not null)
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            PublishLog("雷达数据录制已停止。");
        }

        if (stream is not null)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ReplayAsync(
        string path,
        double speed,
        bool loop,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (speed is not (0.5 or 1.0 or 2.0))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "Replay speed must be 0.5, 1.0 or 2.0.");
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The radar recording does not exist.", fullPath);
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _sourceTransitionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartInfrastructureAsync(cancellationToken).ConfigureAwait(false);
            await DisconnectCoreAsync().ConfigureAwait(false);
            await StopSimulationCoreAsync().ConfigureAwait(false);
            await StopReplayCoreAsync().ConfigureAwait(false);
            _replayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            _replayTask = ReplayCoreAsync(fullPath, speed, loop, _replayCancellation.Token);
            PublishLog($"开始回放：{path} / {speed:0.0}x{(loop ? " / 循环" : string.Empty)}");
        }
        finally
        {
            _sourceTransitionLock.Release();
        }
    }

    public void PauseReplay()
    {
        _replayGate.Pause();
        PublishLog("回放已暂停。");
    }

    public void ResumeReplay()
    {
        _replayGate.Resume();
        PublishLog("回放已继续。");
    }

    public void StepReplay()
    {
        _replayGate.Step();
        PublishLog("回放单圈步进。");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        await _sourceTransitionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            await StopSimulationCoreAsync().ConfigureAwait(false);
            await StopReplayCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _sourceTransitionLock.Release();
        }
        await StopRecordingAsync().ConfigureAwait(false);
        _latestFrames.Writer.TryComplete();

        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync().ConfigureAwait(false);
        }

        if (_pipeTask is not null)
        {
            await AwaitCooperativeTaskAsync(_pipeTask).ConfigureAwait(false);
        }

        if (_processingTask is not null)
        {
            await AwaitCooperativeTaskAsync(_processingTask).ConfigureAwait(false);
        }

        _lifecycleLock.Dispose();
        _sourceTransitionLock.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task ProcessFramesAsync(CancellationToken cancellationToken)
    {
        var profile = RadarModelProfileFactory.Create(_configuration.Device.DeviceModel);
        var tracker = new RadarTargetTracker(ToTrackingOptions());
        var pointerMachine = new PointerStateMachine(ToPointerOptions());
        var previousTimestamp = DateTimeOffset.MinValue;
        var previousBytes = 0L;

        await foreach (var frame in _latestFrames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            profile = RadarModelProfileFactory.Create(_configuration.Device.DeviceModel);
            var transformed = frame.Points
                .Select(point => RadarCoordinateConverter.ApplyTransform(point, ToTransformOptions()))
                .ToArray();
            var valid = RadarPointFilter.Apply(transformed, ToFilterOptions(profile));
            var clusters = new SequentialPointClusterer(ToClusteringOptions()).Cluster(valid);
            var tracking = tracker.Update(clusters);
            var calibration = CreateOutputMapper();
            var mappedTargets = tracking.ObservedTargets
                .Select(target => MapTarget(target, calibration))
                .ToArray();
            var pointerTargets = mappedTargets
                .Where(target => float.IsFinite(target.NormalizedX) && float.IsFinite(target.NormalizedY) &&
                                 target.NormalizedX is >= 0f and <= 1f && target.NormalizedY is >= 0f and <= 1f)
                .ToArray();
            var pointers = pointerMachine.Update(pointerTargets, frame.Timestamp);

            var elapsed = previousTimestamp == DateTimeOffset.MinValue
                ? 1d / profile.DefaultScanFrequencyHz
                : Math.Max(0.001, (frame.Timestamp - previousTimestamp).TotalSeconds);
            var frequency = 1d / elapsed;
            var receivedBytes = _connectionService?.Metrics.ReceivedByteCount ?? frame.Points.Count * 4L;
            var receiveRate = Math.Max(0d, (receivedBytes - previousBytes) / elapsed);
            previousTimestamp = frame.Timestamp;
            previousBytes = receivedBytes;

            var snapshot = new RadarRuntimeSnapshot(
                frame.Sequence,
                frame.Timestamp,
                frame.Points,
                valid,
                clusters,
                mappedTargets,
                pointers,
                frequency,
                receiveRate,
                _connectionService?.Metrics.CrcErrorCount ?? 0,
                _connectionService?.Metrics.DiscardedByteCount ?? 0);
            InvokeSafely(SnapshotUpdated, snapshot);

            var screens = Volatile.Read(ref _unityScreens);
            if (_pipeServer is not null && screens.Count > 0)
            {
                var sequence = Interlocked.Increment(ref _ipcSequence);
                var timestampUnixMilliseconds = frame.Timestamp.ToUnixTimeMilliseconds();
                var screenFrames = screens.Select(screen => new RadarScreenPointerFrame(
                    screen,
                    sequence,
                    timestampUnixMilliseconds,
                    pointers.Select(pointer => new RadarScreenPointer(
                        pointer.PointerId,
                        pointer.Phase,
                        pointer.NormalizedX,
                        pointer.NormalizedY,
                        pointer.NormalizedX * screen.WidthPixels,
                        pointer.NormalizedY * screen.HeightPixels,
                        pointer.Confidence,
                        pointer.TimestampUnixMilliseconds)).ToArray())).ToArray();
                var sent = await _pipeServer.SendAsync(
                    IpcEnvelope.Create(
                        IpcMessageType.PointerBatch,
                        sequence,
                        new PointerBatchPayload(screenFrames),
                        timestampUnixMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                if (sent)
                {
                    _unityStatus = _unityStatus with
                    {
                        LastFrameSentAt = DateTimeOffset.UtcNow,
                        PointerCount = pointers.Count
                    };
                    InvokeSafely(UnityStatusChanged, _unityStatus);
                }
            }
        }
    }

    private async Task GenerateSimulationAsync(CancellationToken cancellationToken)
    {
        var profile = RadarModelProfileFactory.Create(_configuration.Device.DeviceModel);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / profile.DefaultScanFrequencyHz));
        var sequence = 0L;
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            sequence++;
            var phase = sequence * 0.045f;
            var points = new List<RadarPoint>();
            AddSyntheticTarget(points, 1.8f, 60f + MathF.Sin(phase) * 30f, 9);
            AddSyntheticTarget(points, 2.8f, 170f + MathF.Cos(phase * 0.8f) * 18f, 7);
            points.Sort((left, right) => left.AngleRaw.CompareTo(right.AngleRaw));
            _latestFrames.Writer.TryWrite(new RadarScanFrame(sequence, DateTimeOffset.UtcNow, points));
        }
    }

    private async Task ReplayCoreAsync(string path, double speed, bool loop, CancellationToken cancellationToken)
    {
        do
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var reader = new RadarRecordingReader(stream, leaveOpen: true);
            await reader.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            var decoder = new RadarByteStreamDecoder();
            var builder = new RadarScanFrameBuilder(minimumValidPointCount: 2);
            DateTimeOffset? previousTimestamp = null;

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
                        _latestFrames.Writer.TryWrite(frame);
                    }
                }
            }
        }
        while (loop && !cancellationToken.IsCancellationRequested);
    }

    public async Task StopReplayAsync()
    {
        await _sourceTransitionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopReplayCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _sourceTransitionLock.Release();
        }
    }

    private async Task StopReplayCoreAsync()
    {
        var cancellation = Interlocked.Exchange(ref _replayCancellation, null);
        var task = Interlocked.Exchange(ref _replayTask, null);
        cancellation?.Cancel();
        _replayGate.Resume();
        if (task is not null)
        {
            await AwaitCooperativeTaskAsync(task).ConfigureAwait(false);
        }

        cancellation?.Dispose();
    }

    private void OnConnectionStateChanged(RadarConnectionState state)
    {
        InvokeSafely(ConnectionStateChanged, state);
        _ = RecordConnectionStateAsync(state);
    }

    private void OnRawBytesReceived(ReadOnlyMemory<byte> bytes)
    {
        _ = RecordBytesAsync(bytes);
    }

    private async Task RecordBytesAsync(ReadOnlyMemory<byte> bytes)
    {
        var writer = _recordingWriter;
        if (writer is null)
        {
            return;
        }

        try
        {
            await writer.WriteDataAsync(bytes, DateTimeOffset.UtcNow, _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            PublishLog($"录制写入失败：{exception.Message}");
        }
    }

    private async Task RecordConnectionStateAsync(RadarConnectionState state)
    {
        var writer = _recordingWriter;
        if (writer is null)
        {
            return;
        }

        try
        {
            await writer.WriteConnectionStateAsync(state, DateTimeOffset.UtcNow, _lifetimeCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            PublishLog($"录制状态写入失败：{exception.Message}");
        }
    }

    private void OnUnityConnected(HelloPayload hello)
    {
        var screens = hello.Screens.Select(screen => new RadarScreenInfo(
            screen.ScreenId,
            screen.Name,
            screen.DefaultWidthPixels,
            screen.DefaultHeightPixels,
            screen.IsPrimary,
            screen.Order)).ToArray();
        Volatile.Write(ref _unityScreens, screens);
        var statusScreen = screens.FirstOrDefault(screen => screen.IsPrimary) ?? screens.FirstOrDefault();
        _unityStatus = new UnityClientStatus(
            true,
            hello.UnityProcessId,
            hello.UnityVersion,
            statusScreen?.WidthPixels ?? 0,
            statusScreen?.HeightPixels ?? 0,
            null,
            0,
            null);
        InvokeSafely(UnityStatusChanged, _unityStatus);
        PublishLog($"Unity 已连接：PID {hello.UnityProcessId} / {hello.UnityVersion} / {_unityStatus.ScreenWidth}x{_unityStatus.ScreenHeight}");
    }

    private void OnUnityDisconnected()
    {
        Volatile.Write(ref _unityScreens, Array.Empty<RadarScreenInfo>());
        if (!_unityStatus.IsConnected)
        {
            return;
        }

        _unityStatus = UnityClientStatus.Disconnected;
        InvokeSafely(UnityStatusChanged, _unityStatus);
        PublishLog("Unity IPC 已断开。");
    }

    private void OnIpcMessage(IpcEnvelope message)
    {
        if (message.MessageType == IpcMessageType.Shutdown)
        {
            PublishLog("Unity 请求关闭 RadarBridge。");
        }
    }

    private HomographyCalibration? CreateCalibrationMapper()
    {
        var calibration = _configuration.Calibration;
        if (!calibration.IsValid || calibration.PhysicalCorners.Count != 4)
        {
            return null;
        }

        var corners = calibration.PhysicalCorners.Select(point => new Point2(point.X, point.Y)).ToArray();
        return HomographyCalibration.TryCreate(corners, out var mapper, out _) ? mapper : null;
    }

    private HomographyCalibration? CreateOutputMapper()
    {
        var savedCalibration = CreateCalibrationMapper();
        if (savedCalibration is not null)
        {
            return savedCalibration;
        }

        var activeRegion = _configuration.Range.ActivePolygon
            .Select(point => new Point2(point.X, point.Y))
            .ToArray();
        return HomographyCalibration.TryCreate(activeRegion, out var regionMapper, out _)
            ? regionMapper
            : null;
    }

    private static RadarTarget MapTarget(RadarTarget target, HomographyCalibration? calibration)
    {
        if (calibration is not null && calibration.TryMap(target.PhysicalX, target.PhysicalY, out var x, out var y))
        {
            return target with { NormalizedX = x, NormalizedY = y };
        }

        return target with { NormalizedX = float.NaN, NormalizedY = float.NaN };
    }

    private RadarTransformOptions ToTransformOptions() => new()
    {
        RotationDegrees = _configuration.Transform.RotationDegrees,
        FlipX = _configuration.Transform.FlipX,
        FlipY = _configuration.Transform.FlipY,
        OffsetXMeters = _configuration.Transform.OffsetXMeters,
        OffsetYMeters = _configuration.Transform.OffsetYMeters
    };

    private RadarFilterOptions ToFilterOptions(IRadarModelProfile profile) => new()
    {
        MinimumDistanceMeters = _configuration.Range.MinimumDistanceMeters,
        MaximumDistanceMeters = Math.Min(_configuration.Range.MaximumDistanceMeters, profile.MaximumDistanceMeters),
        BlindZoneStartDegrees = profile.BlindZoneStartDegrees,
        BlindZoneEndDegrees = profile.BlindZoneEndDegrees,
        MinimumAngleDegrees = _configuration.Range.MinimumAngleDegrees,
        MaximumAngleDegrees = _configuration.Range.MaximumAngleDegrees,
        ActivePolygon = _configuration.Range.ActivePolygon.Select(point => new Point2(point.X, point.Y)).ToArray(),
        MaskedPolygons = _configuration.Range.MaskedPolygons
            .Select(mask => (IReadOnlyList<Point2>)mask.Select(point => new Point2(point.X, point.Y)).ToArray())
            .ToArray(),
        LeftEdgeDeadZoneMeters = _configuration.Range.EdgeDeadZones.LeftMeters,
        RightEdgeDeadZoneMeters = _configuration.Range.EdgeDeadZones.RightMeters,
        TopEdgeDeadZoneMeters = _configuration.Range.EdgeDeadZones.TopMeters,
        BottomEdgeDeadZoneMeters = _configuration.Range.EdgeDeadZones.BottomMeters
    };

    private RadarClusteringOptions ToClusteringOptions() => new()
    {
        BaseGapMeters = _configuration.Clustering.BaseGapMeters,
        DistanceScale = _configuration.Clustering.DistanceScale,
        MinimumClusterPointCount = _configuration.Clustering.MinimumClusterPointCount,
        MaximumClusterWidthMeters = _configuration.Clustering.MaximumClusterWidthMeters
    };

    private RadarTrackingOptions ToTrackingOptions() => new()
    {
        ConfirmFrames = _configuration.Tracking.ConfirmFrames,
        LostFrames = _configuration.Tracking.LostFrames,
        MaximumAssociationDistanceMeters = _configuration.Tracking.MaximumAssociationDistanceMeters,
        SmoothingAlpha = _configuration.Tracking.SmoothingAlpha
    };

    private RadarPointerOptions ToPointerOptions() => new()
    {
        Mode = _configuration.Interaction.Mode,
        LostFrames = _configuration.Tracking.LostFrames,
        DwellDuration = TimeSpan.FromMilliseconds(_configuration.Interaction.DwellMilliseconds),
        DwellRadiusNormalized = _configuration.Interaction.DwellRadiusNormalized,
        MinimumPressDuration = TimeSpan.FromMilliseconds(_configuration.Interaction.MinimumPressMilliseconds),
        MaximumClickMovementNormalized = _configuration.Interaction.MaximumClickMovementNormalized,
        DragThresholdNormalized = _configuration.Interaction.DragThresholdNormalized
    };

    private static void AddSyntheticTarget(List<RadarPoint> points, float distanceMeters, float angleDegrees, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var offset = (index - (count - 1) / 2f) * 0.22f;
            var angle = angleDegrees + offset;
            var distance = distanceMeters + MathF.Sin(index * 1.7f) * 0.015f;
            var radians = angle * MathF.PI / 180f;
            points.Add(new RadarPoint(
                (int)MathF.Round(distance * 100f),
                (int)MathF.Round(angle * 16f),
                angle,
                distance * MathF.Cos(radians),
                distance * MathF.Sin(radians)));
        }
    }

    private void PublishLog(string message)
    {
        _logger.LogInformation("{Message}", message);
        InvokeSafely(LogReceived, $"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private void InvokeSafely<T>(Action<T>? handlers, T value)
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
            catch (Exception exception)
            {
                _logger.LogError(exception, "RadarBridge observer failed while handling {EventType}.", typeof(T).Name);
            }
        }
    }

    private static async Task AwaitCooperativeTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the expected stop path.
        }
        catch (ObjectDisposedException)
        {
            // Disposing a pipe or timer unblocks an in-flight wait during shutdown.
        }
    }
}

public static class BridgeVersion
{
    public const string Value = "1.1.5";
}
