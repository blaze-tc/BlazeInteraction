using Microsoft.Extensions.Logging;
using Yuexin.Radar.Bridge.Wpf;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Services;

/// <summary>Owns the screen-scoped fusion boundaries and the single v2 IPC connection.</summary>
public sealed class RadarBridgeCoordinator : IRadarBridgeRuntime
{
    private readonly RadarAppConfiguration _configuration;
    private readonly ILogger<RadarBridgeCoordinator> _logger;
    private readonly IRadarSensorPipelineFactory _pipelineFactory;
    private readonly string? _configurationPath;
    private readonly Func<RadarAppConfiguration, CancellationToken, Task> _persistConfigurationAsync;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _topologyLock = new(1, 1);
    private readonly Dictionary<string, ScreenRuntime> _screens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<TransitionFrame> _transitionFrames = new();
    private TransitionFrame? _inFlightTransition;
    private readonly HashSet<Task> _retirementTasks = [];
    private ScreenRuntime[] _associatedSnapshot = [];
    private static readonly TimeSpan SendTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PipelineCleanupTimeout = TimeSpan.FromMilliseconds(250);
    private RadarPipeServer? _pipeServer;
    private Task? _pipeTask;
    private Task? _schedulerTask;
    private UnityClientStatus _unityStatus = UnityClientStatus.Disconnected;
    private long _batchSequence;
    private int _started;
    private int _disposed;

    public RadarBridgeCoordinator(
        RadarAppConfiguration configuration,
        ILogger<RadarBridgeCoordinator> logger,
        IRadarSensorPipelineFactory pipelineFactory,
        string? configurationPath = null,
        Func<RadarAppConfiguration, CancellationToken, Task>? persistConfigurationAsync = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _pipelineFactory = pipelineFactory ?? throw new ArgumentNullException(nameof(pipelineFactory));
        _configurationPath = configurationPath;
        _persistConfigurationAsync = persistConfigurationAsync ?? PersistWithStoreAsync;
    }

    public event Action<RadarSensorRuntimeSnapshot>? SensorSnapshotUpdated;
    public event Action<RadarScreenRuntimeSnapshot>? ScreenSnapshotUpdated;
    public event Action<RadarRuntimeSnapshot>? SnapshotUpdated;
    public event Action<string>? LogReceived;
    public event Action<RadarConnectionState>? ConnectionStateChanged;
    public event Action<UnityClientStatus>? UnityStatusChanged;

    public RadarConnectionState ConnectionState => PrimaryPipeline?.Pipeline.State switch
    {
        RadarSensorRuntimeState.Running or RadarSensorRuntimeState.Starting => RadarConnectionState.Connected,
        RadarSensorRuntimeState.Reconnecting => RadarConnectionState.Reconnecting,
        RadarSensorRuntimeState.Faulted => RadarConnectionState.Faulted,
        _ => RadarConnectionState.Disconnected
    };

    public UnityClientStatus UnityStatus => Volatile.Read(ref _unityStatus);

    public async Task StartInfrastructureAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Volatile.Read(ref _started) != 0) return;
            var server = new RadarPipeServer(new RadarPipeServerOptions
            {
                PipeName = _configuration.Ipc.PipeName,
                HeartbeatTimeout = TimeSpan.FromSeconds(3),
                AuthenticateHelloAsync = AuthenticateHelloAsync
            });
            server.ClientConnected += OnUnityConnected;
            server.ClientDisconnected += OnUnityDisconnected;
            server.ClientError += OnPipeError;
            server.MessageReceived += OnPipeMessage;
            _pipeServer = server;
            _pipeTask = server.RunAsync(_lifetime.Token);
            _schedulerTask = SchedulerAsync(_lifetime.Token);
            Volatile.Write(ref _started, 1);
            PublishLog($"[IPC] server started: {_configuration.Ipc.PipeName} / protocol v{IpcProtocolVersion.Current}");
        }
        catch
        {
            _pipeServer = null;
            _pipeTask = null;
            _schedulerTask = null;
            Volatile.Write(ref _started, 0);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task ApplyUnityTopologyAsync(HelloPayload hello, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!TryValidateTopology(hello, out var error)) throw new InvalidOperationException($"{error.Code}: {error.Message}");
        await _topologyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var advertised = hello.Screens.OrderBy(screen => screen.Order).ThenBy(screen => screen.ScreenId, StringComparer.OrdinalIgnoreCase).ToArray();
            var advertisedIds = new HashSet<string>(advertised.Select(screen => screen.ScreenId), StringComparer.OrdinalIgnoreCase);
            var candidate = CloneConfiguration(_configuration);
            var staged = new Dictionary<string, ScreenRuntime>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var definition in advertised)
                {
                    var configuration = FindOrCreateScreenConfiguration(candidate, definition);
                    var previousWidth = configuration.EffectiveWidthPixels;
                    var previousHeight = configuration.EffectiveHeightPixels;
                    UpdateUnityOwnedConfiguration(configuration, definition);
                    var info = ToScreenInfo(configuration);
                    var needsRuntime = !_screens.TryGetValue(definition.ScreenId, out var current) || current.IsRetiring ||
                        previousWidth != configuration.EffectiveWidthPixels || previousHeight != configuration.EffectiveHeightPixels;
                    if (needsRuntime) staged.Add(definition.ScreenId, CreateRuntime(configuration, info));
                    configuration.IsAssociated = true;
                }

                foreach (var screen in candidate.Screens.Where(screen => !advertisedIds.Contains(screen.ScreenId))) screen.IsAssociated = false;
                await PersistConfigurationAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                foreach (var runtime in staged.Values) await DisposeRuntimeAsync(runtime).ConfigureAwait(false);
                throw;
            }

            _configuration.SchemaVersion = candidate.SchemaVersion;
            _configuration.Ipc = candidate.Ipc;
            _configuration.Screens = candidate.Screens;

            foreach (var stale in _screens.Values.Where(runtime => runtime.Associated && !advertisedIds.Contains(runtime.Info.ScreenId)).ToArray())
            {
                stale.Associated = false;
                stale.IsRetiring = true;
                stale.RetirementCancellation.Cancel();
                stale.Configuration = candidate.Screens.Single(screen => string.Equals(screen.ScreenId, stale.Info.ScreenId, StringComparison.OrdinalIgnoreCase));
                QueueRetirement(stale, now, remove: true, replaceWith: null);
            }

            foreach (var definition in advertised)
            {
                var configuration = candidate.Screens.Single(screen => string.Equals(screen.ScreenId, definition.ScreenId, StringComparison.OrdinalIgnoreCase));
                var info = ToScreenInfo(configuration);

                if (_screens.TryGetValue(definition.ScreenId, out var runtime))
                {
                    if (staged.TryGetValue(definition.ScreenId, out var replacement))
                    {
                        runtime.Associated = false;
                        runtime.IsRetiring = true;
                        runtime.RetirementCancellation.Cancel();
                        QueueRetirement(runtime, now, remove: false, replacement);
                    }
                    else
                    {
                        runtime.Configuration = configuration;
                        runtime.Info = info;
                        runtime.Associated = true;
                        EnsureEnabledPipelines(runtime);
                    }
                }
                else
                {
                    runtime = staged[definition.ScreenId];
                    _screens.Add(definition.ScreenId, runtime);
                }

                if (!runtime.IsRetiring) runtime.Associated = true;
                configuration.IsAssociated = true;
            }

            RefreshAssociatedSnapshot();
        }
        finally
        {
            _topologyLock.Release();
        }
    }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _topologyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var candidate = CloneConfiguration(_configuration);
            var active = _screens.Values.Where(value => value.Associated).ToArray();
            var staged = new Dictionary<string, ScreenRuntime>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var runtime in active)
                {
                    var configuration = candidate.Screens.Single(screen => string.Equals(screen.ScreenId, runtime.Info.ScreenId, StringComparison.OrdinalIgnoreCase));
                    staged.Add(runtime.Info.ScreenId, CreateRuntime(configuration, ToScreenInfo(configuration)));
                }
                await PersistConfigurationAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                foreach (var runtime in staged.Values) await DisposeRuntimeAsync(runtime).ConfigureAwait(false);
                throw;
            }

            _configuration.SchemaVersion = candidate.SchemaVersion;
            _configuration.Ipc = candidate.Ipc;
            _configuration.Screens = candidate.Screens;
            foreach (var runtime in active)
            {
                runtime.Associated = false;
                runtime.IsRetiring = true;
                runtime.RetirementCancellation.Cancel();
                QueueRetirement(runtime, now, remove: false, staged[runtime.Info.ScreenId]);
            }
            RefreshAssociatedSnapshot();
        }
        finally
        {
            _topologyLock.Release();
        }
    }

    public Task ConnectSensorAsync(string screenId, string sensorId, CancellationToken cancellationToken = default) =>
        UsePipelineAsync(screenId, sensorId, (pipeline, token) => pipeline.StartAsync(token), cancellationToken);

    public Task DisconnectSensorAsync(string screenId, string sensorId) =>
        UsePipelineAsync(screenId, sensorId, (pipeline, _) => pipeline.StopAsync(), CancellationToken.None);

    public async Task ConnectScreenAsync(string screenId, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntime(screenId);
        foreach (var sensorId in SnapshotSensorIds(runtime))
            await ConnectSensorAsync(runtime.Info.ScreenId, sensorId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectScreenAsync(string screenId)
    {
        var runtime = GetRuntime(screenId);
        foreach (var sensorId in SnapshotSensorIds(runtime)) await DisconnectSensorAsync(runtime.Info.ScreenId, sensorId).ConfigureAwait(false);
    }

    public async Task ConnectAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in AssociatedRuntimes()) await ConnectScreenAsync(runtime.Info.ScreenId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var runtime in AssociatedRuntimes()) await DisconnectScreenAsync(runtime.Info.ScreenId).ConfigureAwait(false);
    }

    public async Task StartAllSimulationAsync(CancellationToken cancellationToken = default)
    {
        foreach (var runtime in AssociatedRuntimes())
        foreach (var sensorId in SnapshotSensorIds(runtime, simulationOnly: true))
            await ConnectSensorAsync(runtime.Info.ScreenId, sensorId, cancellationToken).ConfigureAwait(false);
    }

    public Task StartRecordingAsync(string screenId, string sensorId, string path, CancellationToken cancellationToken = default) =>
        UsePipelineAsync(screenId, sensorId, (pipeline, token) => pipeline.StartRecordingAsync(path, token), cancellationToken);

    public Task StopRecordingAsync(string screenId, string sensorId) => UsePipelineAsync(screenId, sensorId, (pipeline, _) => pipeline.StopRecordingAsync(), CancellationToken.None);

    public Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken = default) =>
        UsePipelineAsync(screenId, sensorId, (pipeline, token) => pipeline.ReplayAsync(path, speed, loop, token), cancellationToken);

    public void PauseReplay(string screenId, string sensorId) => UsePipeline(screenId, sensorId, pipeline => pipeline.PauseReplay());
    public void ResumeReplay(string screenId, string sensorId) => UsePipeline(screenId, sensorId, pipeline => pipeline.ResumeReplay());
    public void StepReplay(string screenId, string sensorId) => UsePipeline(screenId, sensorId, pipeline => pipeline.StepReplay());
    public Task StopReplayAsync(string screenId, string sensorId) => UsePipelineAsync(screenId, sensorId, (pipeline, _) => pipeline.StopReplayAsync(), CancellationToken.None);

    [Obsolete("Use ConnectSensorAsync or ConnectScreenAsync.")]
    public Task ConnectAsync(CancellationToken cancellationToken = default) => ConnectPrimarySensorAsync(cancellationToken);
    [Obsolete("Use DisconnectSensorAsync or DisconnectScreenAsync.")]
    public Task DisconnectAsync() => PrimaryPipeline?.Pipeline.StopAsync() ?? Task.CompletedTask;
    [Obsolete("Use StartAllSimulationAsync.")]
    public Task StartSimulationAsync(CancellationToken cancellationToken = default) => StartAllSimulationAsync(cancellationToken);
    [Obsolete("Use DisconnectAllAsync.")]
    public Task StopSimulationAsync() => DisconnectAllAsync();
    [Obsolete("Use StartRecordingAsync(screenId, sensorId, path).")]
    public Task StartRecordingAsync(string path, CancellationToken cancellationToken = default) => PrimaryPipeline?.Pipeline.StartRecordingAsync(path, cancellationToken) ?? Task.CompletedTask;
    [Obsolete("Use StopRecordingAsync(screenId, sensorId).")]
    public Task StopRecordingAsync() => PrimaryPipeline?.Pipeline.StopRecordingAsync() ?? Task.CompletedTask;
    [Obsolete("Use ReplaySensorAsync.")]
    public Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default) => PrimaryPipeline?.Pipeline.ReplayAsync(path, speed, loop, cancellationToken) ?? Task.CompletedTask;
    [Obsolete("Use PauseReplay(screenId, sensorId).")]
    public void PauseReplay() => PrimaryPipeline?.Pipeline.PauseReplay();
    [Obsolete("Use ResumeReplay(screenId, sensorId).")]
    public void ResumeReplay() => PrimaryPipeline?.Pipeline.ResumeReplay();
    [Obsolete("Use StepReplay(screenId, sensorId).")]
    public void StepReplay() => PrimaryPipeline?.Pipeline.StepReplay();
    [Obsolete("Use StopReplayAsync(screenId, sensorId).")]
    public Task StopReplayAsync() => PrimaryPipeline?.Pipeline.StopReplayAsync() ?? Task.CompletedTask;

    internal PointerBatchPayload TickForTest(DateTimeOffset timestamp, bool delivered = true, bool waitForRetirement = true)
    {
        PreparedBatch prepared;
        _topologyLock.Wait();
        try
        {
            prepared = PrepareNextBatch(timestamp, tickAll: true)!;
        }
        finally { _topologyLock.Release(); }
        if (delivered)
        {
            var cleanup = ConfirmTransitionAsync(prepared, CancellationToken.None).GetAwaiter().GetResult();
            if (cleanup is not null)
            {
                if (waitForRetirement) cleanup.GetAwaiter().GetResult();
                else TrackRetirement(cleanup);
            }
        }
        else
        {
            ReleaseTransitionAsync(prepared, CancellationToken.None).GetAwaiter().GetResult();
        }
        return prepared.Payload;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        _lifecycleLock.Release();
        var server = _pipeServer;
        if (server is not null)
        {
            server.ClientConnected -= OnUnityConnected;
            server.ClientDisconnected -= OnUnityDisconnected;
            server.ClientError -= OnPipeError;
            server.MessageReceived -= OnPipeMessage;
            await server.DisposeAsync().ConfigureAwait(false);
        }

        await _topologyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var runtime in _screens.Values.ToArray()) TrackRetirement(DisposeRuntimeAsync(runtime));
            _screens.Clear();
            Volatile.Write(ref _associatedSnapshot, []);
        }
        finally { _topologyLock.Release(); }

        await AwaitStoppedAsync(_schedulerTask).ConfigureAwait(false);
        await AwaitStoppedAsync(_pipeTask).ConfigureAwait(false);
        await AwaitRetirementsAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
        _topologyLock.Dispose();
        _lifetime.Dispose();
    }

    private async ValueTask<HelloAuthenticationResult> AuthenticateHelloAsync(HelloPayload hello, CancellationToken cancellationToken)
    {
        if (!TryValidateTopology(hello, out var error)) return HelloAuthenticationResult.Reject(error.Code, error.Message);
        try
        {
            await ApplyUnityTopologyAsync(hello, cancellationToken).ConfigureAwait(false);
            return HelloAuthenticationResult.Accept(new HelloAckPayload(
                BridgeVersion.Value,
                IpcProtocolVersion.Current,
                AssociatedRuntimes().SelectMany(runtime => runtime.Pipelines.Values).Any(value => value.Pipeline.State == RadarSensorRuntimeState.Running),
                "multi-screen",
                AssociatedRuntimes().Select(runtime => runtime.Info).ToArray()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PublishLog($"[IPC] topology rejected: {exception.Message}");
            return HelloAuthenticationResult.Reject("topology_reconciliation_failed", exception.Message);
        }
    }

    private async Task SchedulerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _topologyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                PreparedBatch? prepared;
                try { prepared = PrepareNextBatch(DateTimeOffset.UtcNow, tickAll: false); }
                finally { _topologyLock.Release(); }
                if (prepared is not null) await SendBatchAsync(prepared, cancellationToken).ConfigureAwait(false);
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            PublishLog($"[IPC] scheduler fault: {exception.Message}");
        }
    }

    private PreparedBatch? PrepareNextBatch(DateTimeOffset timestamp, bool tickAll)
    {
        if (_transitionFrames.Count > 0)
        {
            if (_inFlightTransition is not null) return null;
            var transition = _transitionFrames.Peek();
            _inFlightTransition = transition;
            var frames = AssociatedRuntimes()
                .Select(runtime => new TransitionFrame(runtime.Info, runtime.LastPointers))
                .ToDictionary(frame => frame.Screen.ScreenId, StringComparer.OrdinalIgnoreCase);
            frames[transition.Screen.ScreenId] = transition;
            return CreateBatch(timestamp, frames.Values
                .OrderBy(frame => frame.Screen.Order)
                .ThenBy(frame => frame.Screen.ScreenId, StringComparer.OrdinalIgnoreCase)
                .ToArray(), transition);
        }

        var runtimes = AssociatedRuntimes().ToArray();
        if (runtimes.Length == 0) return tickAll ? CreateBatch(timestamp, []) : null;
        var anyDue = tickAll;
        foreach (var runtime in runtimes)
        {
            if (tickAll || timestamp >= runtime.NextDue)
            {
                anyDue = true;
                RadarScreenFusionResult result;
                lock (runtime.Gate)
                {
                    result = runtime.Fusion.Tick(timestamp);
                    runtime.LastTargets = result.Targets;
                    runtime.LastPointers = result.Pointers;
                    runtime.NextDue = timestamp.AddMilliseconds(1000d / Math.Max(1, runtime.Configuration.Fusion.OutputRateHz));
                }
                PublishScreenSnapshot(runtime, result, timestamp);
            }
        }
        if (!anyDue) return null;
        return CreateBatch(timestamp, runtimes.Select(runtime => new TransitionFrame(runtime.Info, runtime.LastPointers)).ToArray());
    }

    private PreparedBatch CreateBatch(DateTimeOffset timestamp, IReadOnlyList<TransitionFrame> frames, TransitionFrame? transition = null)
    {
        var sequence = Interlocked.Increment(ref _batchSequence);
        var payload = new PointerBatchPayload(frames.Select(frame => new RadarScreenPointerFrame(
            frame.Screen,
            sequence,
            timestamp.ToUnixTimeMilliseconds(),
            frame.Pointers.ToArray())).ToArray());
        return new PreparedBatch(sequence, timestamp, payload, transition);
    }

    private async Task SendBatchAsync(PreparedBatch batch, CancellationToken cancellationToken)
    {
        var server = _pipeServer;
        if (server is null)
        {
            await ReleaseTransitionAsync(batch, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(SendTimeout);
        bool sent;
        try
        {
            sent = await server.SendAsync(IpcEnvelope.Create(
                IpcMessageType.PointerBatch,
                batch.Sequence,
                batch.Payload,
                batch.Timestamp.ToUnixTimeMilliseconds()), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await ReleaseTransitionAsync(batch, cancellationToken).ConfigureAwait(false);
            SetUnityStatus(UnityStatus with { LastError = $"IPC pointer batch send failed: {exception.Message}" });
            return;
        }
        if (!sent)
        {
            await ReleaseTransitionAsync(batch, cancellationToken).ConfigureAwait(false);
            SetUnityStatus(UnityStatus with { LastError = "IPC pointer batch send timed out or disconnected." });
            return;
        }
        SetUnityStatus(UnityStatus with { LastBatchSentAt = batch.Timestamp, LastBatchSequence = batch.Sequence, LastError = null });
        PublishLog($"[IPC] batch={batch.Sequence} screens={batch.Payload.Screens.Count} pointers={batch.Payload.Screens.Sum(frame => frame.Pointers.Count)} latencyMs=0.0");
        var cleanup = await ConfirmTransitionAsync(batch, cancellationToken).ConfigureAwait(false);
        if (cleanup is not null) TrackRetirement(cleanup);
    }

    private async Task<Task?> ConfirmTransitionAsync(PreparedBatch batch, CancellationToken cancellationToken)
    {
        await _topologyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return ConfirmTransitionUnderLock(batch); }
        finally { _topologyLock.Release(); }
    }

    private Task? ConfirmTransitionUnderLock(PreparedBatch batch)
    {
        var transition = batch.Transition;
        if (transition is null) return null;
        if (!ReferenceEquals(_inFlightTransition, transition) || _transitionFrames.Count == 0 || !ReferenceEquals(_transitionFrames.Peek(), transition)) return null;
        _transitionFrames.Dequeue();
        _inFlightTransition = null;
        return transition.OnDelivered?.Invoke();
    }

    private async Task ReleaseTransitionAsync(PreparedBatch batch, CancellationToken cancellationToken)
    {
        await _topologyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ReleaseTransitionUnderLock(batch); }
        finally { _topologyLock.Release(); }
    }

    private void ReleaseTransitionUnderLock(PreparedBatch batch)
    {
        if (batch.Transition is not null && ReferenceEquals(_inFlightTransition, batch.Transition)) _inFlightTransition = null;
    }

    private void TrackRetirement(Task task)
    {
        lock (_retirementTasks) _retirementTasks.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (_retirementTasks) _retirementTasks.Remove(completed);
            if (completed.IsFaulted) PublishLog($"[IPC] retirement cleanup fault: {completed.Exception?.GetBaseException().Message}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task AwaitRetirementsAsync()
    {
        Task[] pending;
        lock (_retirementTasks) pending = _retirementTasks.ToArray();
        if (pending.Length == 0) return;
        try { await Task.WhenAll(pending).WaitAsync(PipelineCleanupTimeout).ConfigureAwait(false); }
        catch (TimeoutException) { PublishLog("[IPC] retirement cleanup remains quarantined during shutdown."); }
        catch (Exception exception) { PublishLog($"[IPC] retirement cleanup shutdown fault: {exception.Message}"); }
    }

    private static RadarScreenConfiguration FindOrCreateScreenConfiguration(RadarAppConfiguration configurationRoot, RadarScreenDefinitionPayload definition)
    {
        configurationRoot.Screens ??= [];
        var configuration = configurationRoot.Screens.FirstOrDefault(screen => string.Equals(screen.ScreenId, definition.ScreenId, StringComparison.OrdinalIgnoreCase));
        if (configuration is not null) return configuration;
        configuration = new RadarScreenConfiguration
        {
            ScreenId = definition.ScreenId,
            UnityDisplayName = definition.Name,
            IsAssociated = true,
            IsPrimary = definition.IsPrimary,
            UnityOrder = definition.Order,
            UnityDefaultWidthPixels = definition.DefaultWidthPixels,
            UnityDefaultHeightPixels = definition.DefaultHeightPixels,
            ResolutionMode = RadarResolutionMode.FollowUnityDefault,
            WidthPixels = definition.DefaultWidthPixels,
            HeightPixels = definition.DefaultHeightPixels,
            Sensors = []
        };
        configurationRoot.Screens.Add(configuration);
        return configuration;
    }

    private static void UpdateUnityOwnedConfiguration(RadarScreenConfiguration configuration, RadarScreenDefinitionPayload definition)
    {
        configuration.ScreenId = definition.ScreenId;
        configuration.UnityDisplayName = definition.Name;
        configuration.IsPrimary = definition.IsPrimary;
        configuration.UnityOrder = definition.Order;
        configuration.UnityDefaultWidthPixels = definition.DefaultWidthPixels;
        configuration.UnityDefaultHeightPixels = definition.DefaultHeightPixels;
        if (configuration.ResolutionMode == RadarResolutionMode.FollowUnityDefault)
        {
            configuration.WidthPixels = definition.DefaultWidthPixels;
            configuration.HeightPixels = definition.DefaultHeightPixels;
        }
    }

    private ScreenRuntime CreateRuntime(RadarScreenConfiguration configuration, RadarScreenInfo info)
    {
        var runtime = new ScreenRuntime(configuration, info, CreateFusion(configuration, info));
        runtime.OperationsDrained.TrySetResult();
        try { EnsureEnabledPipelines(runtime); }
        catch
        {
            DisposeRuntimeAsync(runtime).GetAwaiter().GetResult();
            throw;
        }
        return runtime;
    }

    private static IRadarScreenFusionEngine CreateFusion(RadarScreenConfiguration configuration, RadarScreenInfo info) => new RadarScreenFusionEngine(new RadarScreenFusionOptions
    {
        Screen = info,
        SensorDataMaxAgeMilliseconds = configuration.Fusion.SensorDataMaxAgeMilliseconds,
        FusionDistancePixels = configuration.Fusion.FusionDistancePixels,
        MaximumAssociationDistancePixels = configuration.Tracking.MaximumAssociationDistancePixels,
        ConfirmFrames = configuration.Tracking.ConfirmFrames,
        LostFrames = configuration.Tracking.LostFrames,
        SmoothingAlpha = configuration.Tracking.SmoothingAlpha,
        InteractionMode = configuration.Interaction.Mode,
        DwellMilliseconds = configuration.Interaction.DwellMilliseconds,
        DwellRadiusNormalized = configuration.Interaction.DwellRadiusNormalized,
        DragThresholdNormalized = configuration.Interaction.DragThresholdNormalized,
        MinimumPressMilliseconds = configuration.Interaction.MinimumPressMilliseconds,
        MaximumClickMovementNormalized = configuration.Interaction.MaximumClickMovementNormalized
    });

    private void EnsureEnabledPipelines(ScreenRuntime runtime)
    {
        runtime.Configuration.Sensors ??= [];
        foreach (var sensor in runtime.Configuration.Sensors.Where(value => value.Enabled))
        {
            if (runtime.Pipelines.ContainsKey(sensor.SensorId)) continue;
            var pipeline = _pipelineFactory.Create(runtime.Configuration, sensor);
            var binding = new PipelineRuntime(sensor, pipeline);
            runtime.Pipelines.Add(sensor.SensorId, binding);
            binding.DetectionHandler = frame => OnDetectionFrame(runtime, binding, frame);
            binding.SnapshotHandler = snapshot => OnSensorSnapshot(runtime, binding, snapshot);
            binding.StateHandler = state => OnPipelineStateChanged(runtime, binding, state);
            binding.LogHandler = message => PublishLog($"[{runtime.Info.ScreenId}/{binding.Configuration.SensorId}] {message}");
            pipeline.DetectionFrameUpdated += binding.DetectionHandler;
            pipeline.SnapshotUpdated += binding.SnapshotHandler;
            pipeline.StateChanged += binding.StateHandler;
            pipeline.LogReceived += binding.LogHandler;
        }
    }

    private void OnDetectionFrame(ScreenRuntime runtime, PipelineRuntime binding, SensorDetectionFrame frame)
    {
        try
        {
            if (!runtime.Associated || runtime.IsRetiring) return;
            lock (runtime.Gate)
            {
                if (runtime.Associated && !runtime.IsRetiring) runtime.Fusion.Publish(frame);
            }
        }
        catch (Exception exception)
        {
            PublishLog($"[{runtime.Info.ScreenId}/{binding.Configuration.SensorId}] detection fault: {exception.Message}");
        }
    }

    private void OnSensorSnapshot(ScreenRuntime runtime, PipelineRuntime binding, RadarSensorRuntimeSnapshot snapshot)
    {
        binding.LastSnapshot = snapshot;
        InvokeSafely(SensorSnapshotUpdated, snapshot);
        if (runtime.Info.IsPrimary && string.Equals(binding.Configuration.SensorId, runtime.Pipelines.Keys.FirstOrDefault(), StringComparison.OrdinalIgnoreCase))
        {
            InvokeSafely(SnapshotUpdated, new RadarRuntimeSnapshot(snapshot.Sequence, snapshot.Timestamp, snapshot.RawPoints, snapshot.ValidPoints,
                snapshot.Clusters, [], [], snapshot.ScanFrequencyHz, snapshot.ReceivedBytesPerSecond, snapshot.CrcErrorCount, snapshot.DiscardedByteCount));
        }
    }

    private void OnPipelineStateChanged(ScreenRuntime runtime, PipelineRuntime binding, RadarSensorRuntimeState state)
    {
        if (state == RadarSensorRuntimeState.Faulted) PublishLog($"[{runtime.Info.ScreenId}/{binding.Configuration.SensorId}] pipeline faulted.");
        if (runtime.Info.IsPrimary && ReferenceEquals(PrimaryPipeline?.Pipeline, binding.Pipeline)) InvokeSafely(ConnectionStateChanged, ConnectionState);
    }

    private void PublishScreenSnapshot(ScreenRuntime runtime, RadarScreenFusionResult result, DateTimeOffset timestamp)
    {
        var sequence = Volatile.Read(ref _batchSequence) + 1;
        InvokeSafely(ScreenSnapshotUpdated, new RadarScreenRuntimeSnapshot(runtime.Info,
            runtime.Pipelines.Values.Select(binding => binding.LastSnapshot).Where(snapshot => snapshot is not null).Cast<RadarSensorRuntimeSnapshot>().ToArray(),
            result.Targets, result.Pointers, sequence, timestamp));
    }

    private void QueueRetirement(ScreenRuntime runtime, DateTimeOffset timestamp, bool remove, ScreenRuntime? replaceWith)
    {
        IReadOnlyList<RadarScreenPointer> ups;
        lock (runtime.Gate) ups = runtime.Fusion.SnapshotPressedPointers(timestamp);
        _transitionFrames.Enqueue(new TransitionFrame(runtime.Info, ups));
        _transitionFrames.Enqueue(new TransitionFrame(runtime.Info, [], () => CompleteRetirementAsync(runtime, timestamp, remove, replaceWith)));
        runtime.LastTargets = [];
        runtime.LastPointers = [];
    }

    private async Task CompleteRetirementAsync(ScreenRuntime runtime, DateTimeOffset timestamp, bool remove, ScreenRuntime? replaceWith)
    {
        lock (runtime.Gate) runtime.Fusion.Reset(timestamp);
        await DisposeRuntimeAsync(runtime).ConfigureAwait(false);
        await _topologyLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (!_screens.TryGetValue(runtime.Info.ScreenId, out var current) || !ReferenceEquals(current, runtime)) return;
            if (replaceWith is not null)
            {
                replaceWith.Associated = true;
                _screens[runtime.Info.ScreenId] = replaceWith;
            }
            else if (remove)
            {
                _screens.Remove(runtime.Info.ScreenId);
            }
            RefreshAssociatedSnapshot();
        }
        finally { _topologyLock.Release(); }
    }

    private async Task StopRuntimeAsync(ScreenRuntime runtime)
    {
        foreach (var pipeline in runtime.Pipelines.Values) await StopIsolatedAsync(runtime.Info.ScreenId, pipeline).ConfigureAwait(false);
    }

    private async Task DisposeRuntimeAsync(ScreenRuntime runtime)
    {
        await runtime.OperationsDrained.Task.ConfigureAwait(false);
        foreach (var binding in runtime.Pipelines.Values)
        {
            binding.Pipeline.DetectionFrameUpdated -= binding.DetectionHandler;
            binding.Pipeline.SnapshotUpdated -= binding.SnapshotHandler;
            binding.Pipeline.StateChanged -= binding.StateHandler;
            binding.Pipeline.LogReceived -= binding.LogHandler;
            await StopIsolatedAsync(runtime.Info.ScreenId, binding).ConfigureAwait(false);
            await AwaitBoundedAsync(binding.Pipeline.DisposeAsync().AsTask(), runtime.Info.ScreenId, binding.Configuration.SensorId, "dispose").ConfigureAwait(false);
        }
        runtime.Pipelines.Clear();
    }

    private async Task StartIsolatedAsync(string screenId, PipelineRuntime pipeline, CancellationToken cancellationToken)
    {
        try { await pipeline.Pipeline.StartAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) { PublishLog($"[{screenId}/{pipeline.Configuration.SensorId}] start fault: {exception.Message}"); }
    }

    private async Task StopIsolatedAsync(string screenId, PipelineRuntime pipeline)
    {
        await AwaitBoundedAsync(pipeline.Pipeline.StopAsync(), screenId, pipeline.Configuration.SensorId, "stop").ConfigureAwait(false);
    }

    private async Task AwaitBoundedAsync(Task task, string screenId, string sensorId, string operation)
    {
        try
        {
            await task.WaitAsync(PipelineCleanupTimeout, _lifetime.Token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            PublishLog($"[{screenId}/{sensorId}] {operation} quarantined after {PipelineCleanupTimeout.TotalMilliseconds:0}ms.");
            ObserveLate(task, screenId, sensorId, operation);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            ObserveLate(task, screenId, sensorId, operation);
        }
        catch (Exception exception)
        {
            PublishLog($"[{screenId}/{sensorId}] {operation} fault: {exception.Message}");
        }
    }

    private void ObserveLate(Task task, string screenId, string sensorId, string operation) => _ = task.ContinueWith(completed =>
    {
        if (completed.IsFaulted) PublishLog($"[{screenId}/{sensorId}] quarantined {operation} fault: {completed.Exception?.GetBaseException().Message}");
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private async Task ConnectPrimarySensorAsync(CancellationToken cancellationToken)
    {
        var primary = AssociatedRuntimes().FirstOrDefault(runtime => runtime.Info.IsPrimary);
        if (primary is null) return;
        var sensor = SnapshotSensorIds(primary).FirstOrDefault();
        if (sensor is not null) await ConnectSensorAsync(primary.Info.ScreenId, sensor, cancellationToken).ConfigureAwait(false);
    }

    private ScreenRuntime GetRuntime(string screenId)
    {
        var runtime = AssociatedRuntimes().FirstOrDefault(value => string.Equals(value.Info.ScreenId, screenId, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(screenId) || runtime is null)
            throw new InvalidOperationException($"Screen '{screenId}' is not associated with the active Unity topology.");
        return runtime;
    }

    private PipelineRuntime GetPipeline(string screenId, string sensorId)
    {
        var runtime = GetRuntime(screenId);
        PipelineRuntime? pipeline;
        lock (runtime.Gate)
        {
            runtime.Pipelines.TryGetValue(sensorId, out pipeline);
        }
        if (string.IsNullOrWhiteSpace(sensorId) || pipeline is null)
            throw new InvalidOperationException($"Sensor '{sensorId}' is not enabled on screen '{screenId}'.");
        return pipeline;
    }

    private async Task UsePipelineAsync(string screenId, string sensorId, Func<IRadarSensorPipeline, CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var lease = AcquirePipelineLease(screenId, sensorId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, lease.CancellationToken);
        await operation(lease.Pipeline.Pipeline, linked.Token).ConfigureAwait(false);
    }

    private void UsePipeline(string screenId, string sensorId, Action<IRadarSensorPipeline> operation)
    {
        using var lease = AcquirePipelineLease(screenId, sensorId);
        operation(lease.Pipeline.Pipeline);
    }

    private PipelineLease AcquirePipelineLease(string screenId, string sensorId)
    {
        var runtime = GetRuntime(screenId);
        lock (runtime.Gate)
        {
            if (runtime.IsRetiring || !runtime.Associated || string.IsNullOrWhiteSpace(sensorId) || !runtime.Pipelines.TryGetValue(sensorId, out var pipeline))
                throw new InvalidOperationException($"Sensor '{sensorId}' is not enabled on screen '{screenId}'.");
            if (runtime.OperationCount++ == 0) runtime.OperationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new PipelineLease(runtime, pipeline, runtime.RetirementCancellation.Token);
        }
    }

    private static void ReleasePipelineLease(ScreenRuntime runtime)
    {
        lock (runtime.Gate)
        {
            if (--runtime.OperationCount == 0) runtime.OperationsDrained.TrySetResult();
        }
    }

    private PipelineRuntime? PrimaryPipeline => AssociatedRuntimes().OrderBy(runtime => runtime.Info.Order).ThenBy(runtime => runtime.Info.ScreenId, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault(runtime => runtime.Info.IsPrimary)?.Pipelines.Values.FirstOrDefault();

    private ScreenRuntime[] AssociatedRuntimes() => Volatile.Read(ref _associatedSnapshot);

    private static string[] SnapshotSensorIds(ScreenRuntime runtime, bool simulationOnly = false)
    {
        lock (runtime.Gate)
        {
            if (runtime.IsRetiring || !runtime.Associated) throw new InvalidOperationException($"Screen '{runtime.Info.ScreenId}' is retiring.");
            return runtime.Pipelines.Values
                .Where(value => !simulationOnly || value.Configuration.SourceMode == RadarSensorSourceMode.Simulation)
                .Select(value => value.Configuration.SensorId)
                .ToArray();
        }
    }

    private void RefreshAssociatedSnapshot() => Volatile.Write(ref _associatedSnapshot, _screens.Values
        .Where(runtime => runtime.Associated && !runtime.IsRetiring)
        .OrderBy(runtime => runtime.Info.Order)
        .ThenBy(runtime => runtime.Info.ScreenId, StringComparer.OrdinalIgnoreCase)
        .ToArray());

    private static RadarScreenInfo ToScreenInfo(RadarScreenConfiguration configuration) => new(configuration.ScreenId, configuration.UnityDisplayName,
        configuration.EffectiveWidthPixels, configuration.EffectiveHeightPixels, configuration.IsPrimary, configuration.UnityOrder);

    private async Task PersistConfigurationAsync(RadarAppConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.CanPersist) await _persistConfigurationAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    private Task PersistWithStoreAsync(RadarAppConfiguration configuration, CancellationToken cancellationToken) =>
        _configurationPath is null ? Task.CompletedTask : RadarConfigurationStore.SaveAsync(_configurationPath, configuration, cancellationToken);

    private Task PersistConfigurationAsync(CancellationToken cancellationToken) => PersistConfigurationAsync(_configuration, cancellationToken);

    private static RadarAppConfiguration CloneConfiguration(RadarAppConfiguration source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<RadarAppConfiguration>(json) ?? throw new InvalidOperationException("Could not stage radar configuration.");
    }

    private void OnUnityConnected(HelloPayload hello) => SetUnityStatus(new UnityClientStatus(true, hello.UnityProcessId, hello.UnityVersion,
        AssociatedRuntimes().Select(runtime => runtime.Info).ToArray(), UnityStatus.LastBatchSentAt, UnityStatus.LastBatchSequence, null));
    private void OnUnityDisconnected() => SetUnityStatus(UnityClientStatus.Disconnected with { LastBatchSequence = UnityStatus.LastBatchSequence, LastBatchSentAt = UnityStatus.LastBatchSentAt });
    private void OnPipeError(Exception exception) => SetUnityStatus(UnityStatus with { LastError = exception.Message });
    private void OnPipeMessage(IpcEnvelope envelope) { if (envelope.MessageType == IpcMessageType.Shutdown) PublishLog("[IPC] Unity requested shutdown."); }
    private void SetUnityStatus(UnityClientStatus value) { Volatile.Write(ref _unityStatus, value); InvokeSafely(UnityStatusChanged, value); }
    private void PublishLog(string message) { _logger.LogInformation("{Message}", message); InvokeSafely(LogReceived, message); }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static bool TryValidateTopology(HelloPayload hello, out ErrorPayload error)
    {
        if (hello.Screens is null || hello.Screens.Count == 0) { error = new("invalid_screen_topology", "Unity Hello did not include any screens."); return false; }
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primary = 0;
        foreach (var screen in hello.Screens)
        {
            if (!IsConfigurationId(screen.ScreenId) || !ids.Add(screen.ScreenId) || string.IsNullOrWhiteSpace(screen.Name) || screen.DefaultWidthPixels <= 0 || screen.DefaultHeightPixels <= 0)
            { error = new("invalid_screen_topology", "Unity Hello contains an empty, duplicate, or invalid screen definition."); return false; }
            if (screen.IsPrimary) primary++;
        }
        if (primary != 1) { error = new("invalid_screen_topology", "Unity Hello must declare exactly one primary screen."); return false; }
        error = null!; return true;
    }

    private static bool IsConfigurationId(string? value) => value is { Length: >= 1 and <= 64 } && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
    private static async Task AwaitStoppedAsync(Task? task) { if (task is null) return; try { await task.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } }
    private static void InvokeSafely<T>(Action<T>? handlers, T value) { if (handlers is null) return; foreach (Action<T> handler in handlers.GetInvocationList()) try { handler(value); } catch { } }

    private sealed class ScreenRuntime(RadarScreenConfiguration configuration, RadarScreenInfo info, IRadarScreenFusionEngine fusion)
    {
        public object Gate { get; } = new();
        public RadarScreenConfiguration Configuration { get; set; } = configuration;
        public RadarScreenInfo Info { get; set; } = info;
        public IRadarScreenFusionEngine Fusion { get; } = fusion;
        public Dictionary<string, PipelineRuntime> Pipelines { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Associated { get; set; } = true;
        public bool IsRetiring { get; set; }
        public CancellationTokenSource RetirementCancellation { get; } = new();
        public int OperationCount { get; set; }
        public TaskCompletionSource OperationsDrained { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset NextDue { get; set; } = DateTimeOffset.MinValue;
        public IReadOnlyList<FusedScreenTarget> LastTargets { get; set; } = [];
        public IReadOnlyList<RadarScreenPointer> LastPointers { get; set; } = [];
    }

    private sealed class PipelineRuntime(RadarSensorConfiguration configuration, IRadarSensorPipeline pipeline)
    {
        public RadarSensorConfiguration Configuration { get; } = configuration;
        public IRadarSensorPipeline Pipeline { get; } = pipeline;
        public RadarSensorRuntimeSnapshot? LastSnapshot { get; set; }
        public Action<SensorDetectionFrame>? DetectionHandler { get; set; }
        public Action<RadarSensorRuntimeSnapshot>? SnapshotHandler { get; set; }
        public Action<RadarSensorRuntimeState>? StateHandler { get; set; }
        public Action<string>? LogHandler { get; set; }
    }

    private sealed class PipelineLease(ScreenRuntime runtime, PipelineRuntime pipeline, CancellationToken cancellationToken) : IDisposable
    {
        public PipelineRuntime Pipeline { get; } = pipeline;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public void Dispose() => ReleasePipelineLease(runtime);
    }

    private sealed record TransitionFrame(RadarScreenInfo Screen, IReadOnlyList<RadarScreenPointer> Pointers, Func<Task>? OnDelivered = null);
    private sealed record PreparedBatch(long Sequence, DateTimeOffset Timestamp, PointerBatchPayload Payload, TransitionFrame? Transition);
}
