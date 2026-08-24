using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yuexin.Radar.Bridge.Wpf;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;

namespace Blaze.Provider.Radar;

internal interface IRadarProviderRuntime : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task StartAllSimulationAsync(CancellationToken cancellationToken);
    Task StopAllSimulationAsync();
    Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken);
    void PauseReplay(string screenId, string sensorId);
    void ResumeReplay(string screenId, string sensorId);
    void StepReplay(string screenId, string sensorId);
    Task StopReplayAsync(string screenId, string sensorId);
}

internal delegate Task<IRadarProviderRuntime> RadarProviderRuntimeFactory(
    IReadOnlyList<InteractionSurface> surfaces,
    Func<PointerBatchPayload, CancellationToken, Task<bool>> publishPointerBatchAsync,
    CancellationToken cancellationToken);

public sealed class RadarInteractionProvider : IInteractionProvider
{
    private static readonly AsyncLocal<StatusDispatchToken?> CurrentStatusDispatch = new();
    private readonly RadarProviderRuntimeFactory _runtimeFactory;
    private readonly RadarFrameAdapter _adapter;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _disposeGate = new();
    private IRadarProviderRuntime? _runtime;
    private Task? _disposeOperation;
    private Exception? _terminalFailure;
    private int _status = (int)ProviderRuntimeStatus.Created;
    private int _disposed;

    internal RadarInteractionProvider(
        string providerInstanceId,
        RadarProviderRuntimeFactory runtimeFactory)
    {
        ProviderInstanceId = string.IsNullOrWhiteSpace(providerInstanceId)
            ? throw new ArgumentException("A provider instance identifier is required.", nameof(providerInstanceId))
            : providerInstanceId;
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _adapter = new RadarFrameAdapter(ProviderInstanceId);
    }

    internal RadarInteractionProvider(string providerInstanceId, ProviderCreateContext createContext)
        : this(
            providerInstanceId,
            (surfaces, publish, cancellationToken) => RadarCoordinatorRuntime.CreateAsync(
                createContext,
                surfaces,
                publish,
                cancellationToken))
    {
    }

    public string ProviderInstanceId { get; }

    public ProviderRuntimeStatus Status => (ProviderRuntimeStatus)Volatile.Read(ref _status);

    public event EventHandler<InteractionFrameEventArgs>? FrameReceived;

    public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged;

    internal object CreateSettingsView()
    {
        ThrowIfDisposed();
        return _runtime is RadarCoordinatorRuntime runtime
            ? runtime.CreateSettingsView()
            : throw new InvalidOperationException("The Radar provider must be initialized before its settings view can be created.");
    }

    public async Task InitializeAsync(
        ProviderInitializationContext context,
        CancellationToken cancellationToken)
    {
        ThrowIfStatusChangedCallbackReentry();
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Status != ProviderRuntimeStatus.Created)
                throw new InvalidOperationException($"The Radar provider cannot initialize from {Status}.");

            TransitionTo(ProviderRuntimeStatus.Initializing);
            try
            {
                var runtime = await _runtimeFactory(
                    context.Surfaces,
                    PublishPointerBatchAsync,
                    cancellationToken).ConfigureAwait(false);
                _runtime = runtime ?? throw new InvalidOperationException("The Radar runtime factory returned null.");
                TransitionTo(ProviderRuntimeStatus.Ready);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeRuntimeAfterFailureAsync().ConfigureAwait(false);
                TransitionTo(ProviderRuntimeStatus.Stopped);
                throw;
            }
            catch (Exception exception)
            {
                await DisposeRuntimeAfterFailureAsync().ConfigureAwait(false);
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
        ThrowIfStatusChangedCallbackReentry();
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (Status == ProviderRuntimeStatus.Running) return;
            if (Status != ProviderRuntimeStatus.Ready)
                throw new InvalidOperationException($"The Radar provider cannot start from {Status}.");

            var runtime = _runtime ?? throw new InvalidOperationException("The Radar provider was not initialized.");
            TransitionTo(ProviderRuntimeStatus.Starting);
            try
            {
                await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
                TransitionTo(ProviderRuntimeStatus.Running);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                var failure = await StopAndDisposeRuntimeAfterFailureAsync(runtime, exception).ConfigureAwait(false);
                if (ReferenceEquals(exception, failure))
                {
                    TransitionTo(ProviderRuntimeStatus.Stopped);
                    throw;
                }

                PreserveTerminalCleanupFailure(exception, failure);
                TransitionTo(ProviderRuntimeStatus.Faulted, failure);
                throw failure;
            }
            catch (Exception exception)
            {
                var failure = await StopAndDisposeRuntimeAfterFailureAsync(runtime, exception).ConfigureAwait(false);
                PreserveTerminalCleanupFailure(exception, failure);
                TransitionTo(ProviderRuntimeStatus.Faulted, failure);
                throw failure;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfStatusChangedCallbackReentry();
        ThrowIfTerminalFailure();
        var disposal = Volatile.Read(ref _disposeOperation);
        if (disposal is not null)
        {
            await disposal.ConfigureAwait(false);
            return;
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task? disposalStartedWhileWaiting = null;
        try
        {
            disposalStartedWhileWaiting = Volatile.Read(ref _disposeOperation);
            if (disposalStartedWhileWaiting is null)
            {
                ThrowIfTerminalFailure();
                if (Status == ProviderRuntimeStatus.Stopped) return;
                if (Status == ProviderRuntimeStatus.Created)
                {
                    TransitionTo(ProviderRuntimeStatus.Stopped);
                    return;
                }

                var runtime = _runtime;
                TransitionTo(ProviderRuntimeStatus.Stopping);
                try
                {
                    if (runtime is not null)
                        await runtime.StopAsync(cancellationToken).ConfigureAwait(false);
                    TransitionTo(ProviderRuntimeStatus.Stopped);
                }
                catch (Exception exception)
                {
                    var failure = await DisposeRuntimeAfterFailureAsync(exception).ConfigureAwait(false);
                    PreserveTerminalCleanupFailure(exception, failure);
                    TransitionTo(ProviderRuntimeStatus.Faulted, failure);
                    throw failure;
                }
            }
        }
        finally
        {
            _lifecycle.Release();
        }

        if (disposalStartedWhileWaiting is not null)
            await disposalStartedWhileWaiting.ConfigureAwait(false);
    }

    public Task StartAllSimulationAsync(CancellationToken cancellationToken = default) =>
        RunningRuntime().StartAllSimulationAsync(cancellationToken);

    public Task StopAllSimulationAsync() => RunningRuntime().StopAllSimulationAsync();

    public Task ReplaySensorAsync(
        string screenId,
        string sensorId,
        string path,
        double speed,
        bool loop,
        CancellationToken cancellationToken = default) =>
        RunningRuntime().ReplaySensorAsync(screenId, sensorId, path, speed, loop, cancellationToken);

    public void PauseReplay(string screenId, string sensorId) => RunningRuntime().PauseReplay(screenId, sensorId);

    public void ResumeReplay(string screenId, string sensorId) => RunningRuntime().ResumeReplay(screenId, sensorId);

    public void StepReplay(string screenId, string sensorId) => RunningRuntime().StepReplay(screenId, sensorId);

    public Task StopReplayAsync(string screenId, string sensorId) => RunningRuntime().StopReplayAsync(screenId, sensorId);

    public ValueTask DisposeAsync()
    {
        ThrowIfStatusChangedCallbackReentry();
        Task operation;
        TaskCompletionSource? completion = null;
        lock (_disposeGate)
        {
            if (_disposeOperation is null)
            {
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeOperation = completion.Task;
            }

            operation = _disposeOperation;
        }

        if (completion is not null)
            _ = CompleteDisposeAsync(completion);

        return new ValueTask(operation);
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        List<Exception>? failures = null;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfTerminalFailure();
            var runtime = Interlocked.Exchange(ref _runtime, null);
            if (runtime is not null)
            {
                if (Status is not ProviderRuntimeStatus.Stopped and not ProviderRuntimeStatus.Faulted)
                {
                    TransitionTo(ProviderRuntimeStatus.Stopping);
                    try
                    {
                        await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }

                try
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (failures ??= []).Add(exception);
                }
            }

            var failure = failures switch
            {
                null or { Count: 0 } => null,
                { Count: 1 } => failures[0],
                _ => new AggregateException("Multiple failures occurred while stopping and disposing the Radar runtime.", failures)
            };
            if (failure is null)
                TransitionTo(ProviderRuntimeStatus.Stopped);
            else
                TransitionTo(ProviderRuntimeStatus.Faulted, failure);

            if (failure is not null) throw failure;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private Task<bool> PublishPointerBatchAsync(
        PointerBatchPayload batch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Status != ProviderRuntimeStatus.Running) return Task.FromResult(false);

        IReadOnlyList<InteractionFrame> frames;
        try
        {
            frames = _adapter.Adapt(batch);
        }
        catch (Exception exception)
        {
            TransitionTo(ProviderRuntimeStatus.Faulted, exception);
            return Task.FromResult(false);
        }

        foreach (var frame in frames)
            InvokeSafely(FrameReceived, new InteractionFrameEventArgs(frame));
        return Task.FromResult(true);
    }

    private IRadarProviderRuntime RunningRuntime()
    {
        ThrowIfDisposed();
        if (Status != ProviderRuntimeStatus.Running || _runtime is not { } runtime)
            throw new InvalidOperationException("The Radar provider must be running for this operation.");
        return runtime;
    }

    private async Task<Exception> StopAndDisposeRuntimeAfterFailureAsync(
        IRadarProviderRuntime runtime,
        Exception primaryFailure)
    {
        var failures = new List<Exception> { primaryFailure };
        try
        {
            await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        var disposeFailure = await CaptureDisposeRuntimeFailureAsync().ConfigureAwait(false);
        if (disposeFailure is not null)
            failures.Add(disposeFailure);
        return CombineOperationAndCleanupFailures(failures);
    }

    private async Task DisposeRuntimeAfterFailureAsync()
    {
        _ = await CaptureDisposeRuntimeFailureAsync().ConfigureAwait(false);
    }

    private async Task<Exception> DisposeRuntimeAfterFailureAsync(Exception primaryFailure)
    {
        var failures = new List<Exception> { primaryFailure };
        var disposeFailure = await CaptureDisposeRuntimeFailureAsync().ConfigureAwait(false);
        if (disposeFailure is not null)
            failures.Add(disposeFailure);
        return CombineOperationAndCleanupFailures(failures);
    }

    private async Task<Exception?> CaptureDisposeRuntimeFailureAsync()
    {
        var runtime = Interlocked.Exchange(ref _runtime, null);
        if (runtime is null) return null;
        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Exception CombineOperationAndCleanupFailures(List<Exception> failures) =>
        failures.Count == 1
            ? failures[0]
            : new AggregateException("The Radar runtime operation and its cleanup both failed.", failures);

    private void PreserveTerminalCleanupFailure(Exception primaryFailure, Exception observedFailure)
    {
        if (!ReferenceEquals(primaryFailure, observedFailure))
            Volatile.Write(ref _terminalFailure, observedFailure);
    }

    private void ThrowIfTerminalFailure()
    {
        if (Volatile.Read(ref _terminalFailure) is { } failure)
            throw failure;
    }

    private void TransitionTo(ProviderRuntimeStatus status, Exception? error = null)
    {
        var previous = (ProviderRuntimeStatus)Interlocked.Exchange(ref _status, (int)status);
        if (previous == status && error is null) return;
        InvokeStatusChangedSafely(new ProviderStatusChangedEventArgs(previous, status, error));
    }

    private void InvokeStatusChangedSafely(ProviderStatusChangedEventArgs arguments)
    {
        var handlers = StatusChanged;
        if (handlers is null) return;
        foreach (EventHandler<ProviderStatusChangedEventArgs> handler in handlers.GetInvocationList())
        {
            var previous = CurrentStatusDispatch.Value;
            var token = new StatusDispatchToken(this);
            CurrentStatusDispatch.Value = token;
            try
            {
                handler(null, arguments);
            }
            catch
            {
            }
            finally
            {
                token.Deactivate();
                CurrentStatusDispatch.Value = previous;
            }
        }
    }

    private static void InvokeSafely<TEventArgs>(EventHandler<TEventArgs>? handlers, TEventArgs arguments)
        where TEventArgs : EventArgs
    {
        if (handlers is null) return;
        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(null, arguments);
            }
            catch
            {
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void ThrowIfStatusChangedCallbackReentry()
    {
        if (CurrentStatusDispatch.Value?.IsActiveFor(this) == true)
            throw new InvalidOperationException("Radar provider lifecycle operations cannot be invoked synchronously from a StatusChanged callback.");
    }

    private sealed class StatusDispatchToken(RadarInteractionProvider provider)
    {
        private readonly WeakReference<RadarInteractionProvider> _provider = new(provider);
        private int _active = 1;

        public bool IsActiveFor(RadarInteractionProvider provider) =>
            Volatile.Read(ref _active) != 0 &&
            _provider.TryGetTarget(out var target) &&
            ReferenceEquals(target, provider);

        public void Deactivate() => Volatile.Write(ref _active, 0);
    }

    internal sealed class RadarCoordinatorRuntime : IRadarProviderRuntime
    {
        private readonly RadarBridgeCoordinator _coordinator;
        private readonly RadarAppConfiguration _configuration;
        private readonly object _hostStatusGate = new();
        private readonly Queue<InteractionHostStatus> _hostStatusPublications = new();
        private readonly Action<InteractionHostStatus>? _beforeApplyHostStatus;
        private IInteractionHostStatusSubscription? _hostStatusSubscription;
        private long _lastHostStatusVersion = -1;
        private bool _isDrainingHostStatus;
        private int _disposed;

        private RadarCoordinatorRuntime(
            RadarBridgeCoordinator coordinator,
            RadarAppConfiguration configuration,
            IInteractionHostStatus? hostStatus,
            Action<InteractionHostStatus>? beforeApplyHostStatus)
        {
            _coordinator = coordinator;
            _configuration = configuration;
            _beforeApplyHostStatus = beforeApplyHostStatus;
            if (hostStatus is not null)
            {
                var subscription = hostStatus.Subscribe(OnHostStatusChanged);
                _hostStatusSubscription = subscription;
                try
                {
                    QueueHostStatus(subscription.Current);
                }
                catch
                {
                    _hostStatusSubscription = null;
                    subscription.Dispose();
                    throw;
                }
            }
        }

        internal RadarBridgeCoordinator Coordinator => _coordinator;

        internal static async Task<IRadarProviderRuntime> CreateAsync(
            ProviderCreateContext createContext,
            IReadOnlyList<InteractionSurface> surfaces,
            Func<PointerBatchPayload, CancellationToken, Task<bool>> publishPointerBatchAsync,
            CancellationToken cancellationToken,
            Action<InteractionHostStatus>? beforeApplyHostStatus = null)
        {
            var services = createContext.Services;
            var storedConfiguration = services.GetService(typeof(IProviderStorageContext)) as IProviderStorageContext;
            var hostStatus = services.GetService(typeof(IInteractionHostStatus)) as IInteractionHostStatus;
            var loadedConfiguration = storedConfiguration is null
                ? null
                : await RadarProviderConfiguration.LoadAsync(
                    createContext.ProviderDirectory,
                    storedConfiguration,
                    cancellationToken).ConfigureAwait(false);
            var configuration = services.GetService(typeof(RadarAppConfiguration)) as RadarAppConfiguration
                ?? loadedConfiguration?.Configuration
                ?? await LoadBundledDefaultAsync(createContext.ProviderDirectory, cancellationToken).ConfigureAwait(false);
            var loggerFactory = services.GetService(typeof(ILoggerFactory)) as ILoggerFactory
                ?? NullLoggerFactory.Instance;
            var pipelineFactory = services.GetService(typeof(IRadarSensorPipelineFactory)) as IRadarSensorPipelineFactory
                ?? new RadarSensorPipelineFactory(loggerFactory);
            var logger = services.GetService(typeof(ILogger<RadarBridgeCoordinator>)) as ILogger<RadarBridgeCoordinator>
                ?? loggerFactory.CreateLogger<RadarBridgeCoordinator>();
            var coordinator = new RadarBridgeCoordinator(
                configuration,
                logger,
                pipelineFactory,
                configurationPath: loadedConfiguration?.ConfigurationPath,
                sendPointerBatchAsync: publishPointerBatchAsync,
                enableLegacyIpc: false);
            try
            {
                await coordinator.ApplyUnityTopologyAsync(
                    new HelloPayload(
                        Environment.ProcessId,
                        "Blaze.Interaction.Provider/1.0.0",
                        surfaces.Select(surface => new RadarScreenDefinitionPayload(
                            surface.SurfaceId,
                            surface.Name,
                            surface.LogicalWidth,
                            surface.LogicalHeight,
                            surface.IsPrimary,
                            surface.Order)).ToArray()),
                    cancellationToken).ConfigureAwait(false);
                return new RadarCoordinatorRuntime(
                    coordinator,
                    configuration,
                    hostStatus,
                    beforeApplyHostStatus);
            }
            catch
            {
                await coordinator.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _coordinator.StartInfrastructureAsync(cancellationToken).ConfigureAwait(false);
            await _coordinator.ConnectAllAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await _coordinator.DisconnectAllAsync().ConfigureAwait(false);
            }
            finally
            {
                UnsubscribeHostStatus();
                await _coordinator.DisposeAsync().ConfigureAwait(false);
            }
        }

        public Task StartAllSimulationAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _coordinator.StartAllSimulationAsync(cancellationToken);
        }

        public Task StopAllSimulationAsync()
        {
            ThrowIfDisposed();
            return _coordinator.StopAllSimulationAsync();
        }

        public Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            return _coordinator.ReplaySensorAsync(screenId, sensorId, path, speed, loop, cancellationToken);
        }

        public void PauseReplay(string screenId, string sensorId)
        {
            ThrowIfDisposed();
            _coordinator.PauseReplay(screenId, sensorId);
        }

        public void ResumeReplay(string screenId, string sensorId)
        {
            ThrowIfDisposed();
            _coordinator.ResumeReplay(screenId, sensorId);
        }

        public void StepReplay(string screenId, string sensorId)
        {
            ThrowIfDisposed();
            _coordinator.StepReplay(screenId, sensorId);
        }

        public Task StopReplayAsync(string screenId, string sensorId)
        {
            ThrowIfDisposed();
            return _coordinator.StopReplayAsync(screenId, sensorId);
        }

        internal object CreateSettingsView()
        {
            ThrowIfDisposed();
            var viewModel = new MainViewModel(_configuration, _coordinator);
            try
            {
                var window = new MainWindow(viewModel, _coordinator);
                window.Closed += (_, _) => viewModel.Dispose();
                return window;
            }
            catch
            {
                viewModel.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            UnsubscribeHostStatus();
            return _coordinator.DisposeAsync();
        }

        private void OnHostStatusChanged(InteractionHostStatus status)
        {
            try
            {
                QueueHostStatus(status);
            }
            catch (ObjectDisposedException)
            {
                // A captured status callback may race provider shutdown.
            }
        }

        private void QueueHostStatus(InteractionHostStatus status)
        {
            ArgumentNullException.ThrowIfNull(status);
            var shouldDrain = false;
            lock (_hostStatusGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return;
                }

                _hostStatusPublications.Enqueue(status);
                if (!_isDrainingHostStatus)
                {
                    _isDrainingHostStatus = true;
                    shouldDrain = true;
                }
            }

            if (shouldDrain)
            {
                DrainHostStatusPublications();
            }
        }

        private void DrainHostStatusPublications()
        {
            while (true)
            {
                InteractionHostStatus status;
                lock (_hostStatusGate)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        _hostStatusPublications.Clear();
                        _isDrainingHostStatus = false;
                        return;
                    }

                    if (_hostStatusPublications.Count == 0)
                    {
                        _isDrainingHostStatus = false;
                        return;
                    }

                    status = _hostStatusPublications.Dequeue();
                    if (status.Version <= _lastHostStatusVersion)
                    {
                        continue;
                    }

                    _lastHostStatusVersion = status.Version;
                }

                _beforeApplyHostStatus?.Invoke(status);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    continue;
                }
                _coordinator.ApplyUnityConnectionStatus(new UnityClientStatus(
                    status.IsConnected,
                    status.ProcessId,
                    status.ClientVersion,
                    status.Surfaces.Select(surface => new RadarScreenInfo(
                        surface.SurfaceId,
                        surface.Name,
                        surface.LogicalWidth,
                        surface.LogicalHeight,
                        surface.IsPrimary,
                        surface.Order)).ToArray(),
                    null,
                    0,
                    null));
            }
        }

        private void UnsubscribeHostStatus()
        {
            Interlocked.Exchange(ref _hostStatusSubscription, null)?.Dispose();
        }

        private static async Task<RadarAppConfiguration> LoadBundledDefaultAsync(
            string providerDirectory,
            CancellationToken cancellationToken)
        {
            var profilePath = Path.Combine(providerDirectory, "profiles", "radar-default.json");
            return File.Exists(profilePath)
                ? await RadarConfigurationStore.LoadAsync(profilePath, cancellationToken).ConfigureAwait(false)
                : RadarAppConfiguration.CreateDefault();
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
