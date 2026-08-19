using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yuexin.Radar.Bridge.Wpf.Services;
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
    private readonly RadarProviderRuntimeFactory _runtimeFactory;
    private readonly RadarFrameAdapter _adapter;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _disposeGate = new();
    private IRadarProviderRuntime? _runtime;
    private Lazy<Task>? _disposeOperation;
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

    public async Task InitializeAsync(
        ProviderInitializationContext context,
        CancellationToken cancellationToken)
    {
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await StopAndDisposeRuntimeAfterFailureAsync(runtime).ConfigureAwait(false);
                TransitionTo(ProviderRuntimeStatus.Stopped);
                throw;
            }
            catch (Exception exception)
            {
                await StopAndDisposeRuntimeAfterFailureAsync(runtime).ConfigureAwait(false);
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
        if (Volatile.Read(ref _disposed) != 0) return;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
        Lazy<Task> operation;
        lock (_disposeGate)
        {
            operation = _disposeOperation ??= new Lazy<Task>(
                DisposeCoreAsync,
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        return new ValueTask(operation.Value);
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        Exception? failure = null;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
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
                        failure = exception;
                    }
                }

                try
                {
                    await runtime.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (failure is null)
                {
                    failure = exception;
                }
            }

            if (failure is null)
                TransitionTo(ProviderRuntimeStatus.Stopped);
            else
                TransitionTo(ProviderRuntimeStatus.Faulted, failure);
        }
        finally
        {
            _lifecycle.Release();
        }

        if (failure is not null) throw failure;
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

    private async Task StopAndDisposeRuntimeAfterFailureAsync(IRadarProviderRuntime runtime)
    {
        try
        {
            await runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        await DisposeRuntimeAfterFailureAsync().ConfigureAwait(false);
    }

    private async Task DisposeRuntimeAfterFailureAsync()
    {
        var runtime = Interlocked.Exchange(ref _runtime, null);
        if (runtime is null) return;
        try
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private void TransitionTo(ProviderRuntimeStatus status, Exception? error = null)
    {
        var previous = (ProviderRuntimeStatus)Interlocked.Exchange(ref _status, (int)status);
        if (previous == status && error is null) return;
        InvokeSafely(StatusChanged, new ProviderStatusChangedEventArgs(previous, status, error));
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

    private sealed class RadarCoordinatorRuntime : IRadarProviderRuntime
    {
        private readonly RadarBridgeCoordinator _coordinator;
        private int _disposed;

        private RadarCoordinatorRuntime(RadarBridgeCoordinator coordinator)
        {
            _coordinator = coordinator;
        }

        internal static async Task<IRadarProviderRuntime> CreateAsync(
            ProviderCreateContext createContext,
            IReadOnlyList<InteractionSurface> surfaces,
            Func<PointerBatchPayload, CancellationToken, Task<bool>> publishPointerBatchAsync,
            CancellationToken cancellationToken)
        {
            var services = createContext.Services;
            var configuration = services.GetService(typeof(RadarAppConfiguration)) as RadarAppConfiguration
                ?? await LoadProviderConfigurationAsync(createContext.ProviderDirectory, cancellationToken).ConfigureAwait(false);
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
                return new RadarCoordinatorRuntime(coordinator);
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

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            return _coordinator.DisposeAsync();
        }

        private static async Task<RadarAppConfiguration> LoadProviderConfigurationAsync(
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
