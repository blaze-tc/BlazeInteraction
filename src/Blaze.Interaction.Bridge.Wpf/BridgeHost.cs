using System.Diagnostics;
using System.IO;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Ipc;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.Interaction.Runtime;

namespace Blaze.Interaction.Bridge.Wpf;

public sealed class BridgeHostOptions
{
    public required string ProvidersRoot { get; init; }
    public required string DataRoot { get; init; }
    public string? ProfilePath { get; init; }
    public int? ParentProcessId { get; init; }
    public string? PreferredProviderId { get; init; }
    public bool UseFallbackProviderWhenNoPreference { get; init; } = true;
    public string PipeName { get; init; } = InteractionIpcProtocol.PipeName;
    internal Action<PrefetchedProviderFactory, WeakReference>? ProviderFactoryObserved { get; init; }
}

public sealed record BridgeStartupDiagnostic(
    string ProviderId,
    string Stage,
    string Message);

internal sealed record BridgeAvailableProvider(
    string ProviderId,
    string ProviderInstanceId,
    string DisplayName,
    string Category,
    bool IsAvailable);

internal sealed record BridgeHostSnapshot(
    InteractionHostStatus Unity,
    BridgeAvailableProvider? ActiveProvider,
    ProviderRuntimeStatus? ProviderStatus);

internal sealed record BridgeProviderUiRegistration(
    IProviderSettingsViewFactory? SettingsViewFactory,
    PrefetchedProviderFactory ProviderFactory);

internal interface IBridgeMessageSink
{
    bool IsConnected { get; }
    bool IsAcknowledged { get; }
    ValueTask<bool> PublishFrameAsync(InteractionFrame frame, CancellationToken cancellationToken);
    bool DiscardStagedLatestFrame(string providerId, string providerInstanceId);
    ValueTask<bool> SendReliableFrameAsync(InteractionFrame frame, CancellationToken cancellationToken);
    ValueTask<bool> SendAsync(InteractionEnvelope message, CancellationToken cancellationToken);
}

internal interface IParentProcessMonitor
{
    Task WaitForExitAsync(int processId, CancellationToken cancellationToken);
}

public sealed class BridgeHost : IAsyncDisposable, IBridgeWindowHost
{
    private readonly ProviderManager _manager;
    private readonly IBridgeMessageSink _messageSink;
    private readonly Func<CancellationToken, Task> _runServerAsync;
    private readonly IParentProcessMonitor _parentMonitor;
    private readonly int? _parentProcessId;
    private string? _preferredProviderInstanceId;
    private readonly IReadOnlyDictionary<string, ProviderDescriptor> _descriptors;
    private readonly IReadOnlyDictionary<string, BridgeProviderUiRegistration> _providerUi;
    private readonly IReadOnlyList<object> _ownedResources;
    private readonly IServiceProvider _services;
    private readonly BridgeSettingsStore? _settingsStore;
    private readonly BridgeInteractionHostStatus? _hostStatus;
    private readonly InteractionPipeServer? _interactionServer;
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _helloGate = new(1, 1);
    private readonly CancellationTokenSource _outboundCancellation = new();
    private readonly object _outboundGate = new();
    private Task _outboundTail = Task.CompletedTask;
    private ProviderInitializationContext? _initializationContext;
    private IReadOnlyList<InteractionSurface>? _activeTopology;
    private BridgeHostSnapshot _currentSnapshot;
    private long _controlSequence;
    private int _disposed;

    internal BridgeHost(
        ProviderManager manager,
        IBridgeMessageSink messageSink,
        Func<CancellationToken, Task> runServerAsync,
        IParentProcessMonitor parentMonitor,
        int? parentProcessId,
        string? defaultProviderInstanceId,
        IReadOnlyDictionary<string, ProviderDescriptor> descriptors,
        IReadOnlyList<object> ownedResources,
        IReadOnlyList<BridgeStartupDiagnostic>? startupDiagnostics = null,
        IReadOnlyDictionary<string, BridgeProviderUiRegistration>? providerUi = null,
        IServiceProvider? services = null,
        BridgeSettingsStore? settingsStore = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _messageSink = messageSink ?? throw new ArgumentNullException(nameof(messageSink));
        _runServerAsync = runServerAsync ?? throw new ArgumentNullException(nameof(runServerAsync));
        _parentMonitor = parentMonitor ?? throw new ArgumentNullException(nameof(parentMonitor));
        _parentProcessId = parentProcessId;
        _preferredProviderInstanceId = defaultProviderInstanceId;
        _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
        _providerUi = providerUi ?? new Dictionary<string, BridgeProviderUiRegistration>();
        _ownedResources = ownedResources ?? throw new ArgumentNullException(nameof(ownedResources));
        _services = services ?? EmptyServiceProvider.Instance;
        _settingsStore = settingsStore;
        _hostStatus = _services.GetService(typeof(IInteractionHostStatus)) as BridgeInteractionHostStatus;
        _interactionServer = _ownedResources.OfType<InteractionPipeServer>().SingleOrDefault();
        AvailableProviders = Array.AsReadOnly(_descriptors.Select(pair =>
            new BridgeAvailableProvider(
                pair.Value.Id,
                pair.Key,
                pair.Value.DisplayName,
                pair.Value.Category,
                IsAvailable: true)).ToArray());
        _currentSnapshot = new BridgeHostSnapshot(
            _hostStatus?.Current ?? InteractionHostStatus.Disconnected,
            ActiveProvider: null,
            ProviderStatus: null);
        StartupDiagnostics = Array.AsReadOnly((startupDiagnostics ?? []).ToArray());

        _manager.FrameReceived += OnFrameReceived;
        _manager.ProviderChanged += OnProviderChanged;
        _manager.StatusChanged += OnStatusChanged;
        if (_hostStatus is not null)
        {
            _hostStatus.Changed += OnUnityStatusChanged;
        }
        if (_hostStatus is not null && _interactionServer is not null)
        {
            _interactionServer.ClientConnected += OnClientConnected;
            _interactionServer.ClientDisconnected += OnClientDisconnected;
        }
    }

    public IReadOnlyList<BridgeStartupDiagnostic> StartupDiagnostics { get; }

    internal IReadOnlyList<BridgeAvailableProvider> AvailableProviders { get; }

    internal BridgeHostSnapshot CurrentSnapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _currentSnapshot;
            }
        }
    }

    internal event EventHandler? ActiveProviderChanged;
    internal event Action<BridgeHostSnapshot>? SnapshotChanged;

    IReadOnlyList<BridgeAvailableProvider> IBridgeProviderSelection.AvailableProviders =>
        AvailableProviders;

    Task IBridgeProviderSelection.SelectProviderAsync(
        string providerId,
        CancellationToken cancellationToken) =>
        SelectProviderAsync(providerId, cancellationToken);

    BridgeHostSnapshot IBridgeWindowHost.CurrentSnapshot => CurrentSnapshot;

    event EventHandler? IBridgeWindowHost.ActiveProviderChanged
    {
        add => ActiveProviderChanged += value;
        remove => ActiveProviderChanged -= value;
    }

    event Action<BridgeHostSnapshot>? IBridgeWindowHost.SnapshotChanged
    {
        add => SnapshotChanged += value;
        remove => SnapshotChanged -= value;
    }

    object? IBridgeWindowHost.CreateActiveProviderSettingsView(IProviderSettingsContext context) =>
        CreateActiveProviderSettingsView(context);

    public static BridgeHost Create(BridgeHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ProvidersRoot))
        {
            throw new ArgumentException("A Providers root directory is required.", nameof(options));
        }
        if (string.IsNullOrWhiteSpace(options.DataRoot))
        {
            throw new ArgumentException("A project data root directory is required.", nameof(options));
        }

        var discovery = BridgeProviderDiscovery.Discover(options.ProvidersRoot);
        var manager = new ProviderManager();
        var storage = new BridgeProviderStorageContext(options.DataRoot, options.ProfilePath);
        var hostStatus = new BridgeInteractionHostStatus();
        var services = new BridgeServiceProvider([storage, hostStatus]);
        var descriptors = new Dictionary<string, ProviderDescriptor>(StringComparer.Ordinal);
        var diagnostics = discovery.LoadResults
            .Where(result => !result.IsSuccess)
            .Select(result => new BridgeStartupDiagnostic(
                result.CatalogEntry.Manifest?.Id ?? Path.GetFileName(result.CatalogEntry.ProviderDirectory),
                "LoadProvider",
                result.Error ?? result.Failure?.ToString() ?? "Provider loading failed."))
            .ToList();
        var prefetched = new List<PrefetchedProviderFactory>();
        var providerUi = new Dictionary<string, BridgeProviderUiRegistration>(StringComparer.Ordinal);
        InteractionPipeServer? server = null;
        try
        {
            foreach (var loaded in discovery.LoadedProviders)
            {
                var providerDirectory = discovery.LoadResults
                    .Single(result => ReferenceEquals(result.Provider, loaded))
                    .CatalogEntry.ProviderDirectory;
                var context = new ProviderCreateContext(
                    providerDirectory,
                    services);
                var factory = BridgeProviderRegistrar.TryRegister(
                    manager,
                    loaded.Plugin,
                    context,
                    descriptors,
                    diagnostics);
                if (factory is not null)
                {
                    prefetched.Add(factory);
                    providerUi.Add(
                        factory.ProviderInstanceId,
                        new BridgeProviderUiRegistration(loaded.Plugin.SettingsViewFactory, factory));
                    options.ProviderFactoryObserved?.Invoke(
                        factory,
                        new WeakReference(loaded.LoadContext));
                }
            }

            var defaultInstanceId = SelectDefaultProviderInstance(
                descriptors,
                options.PreferredProviderId,
                options.UseFallbackProviderWhenNoPreference);
            BridgeHost? host = null;
            server = new InteractionPipeServer(new InteractionPipeServerOptions
            {
                PipeName = options.PipeName,
                ExpectedClientProcessId = options.ParentProcessId,
                CreateHelloAckAsync = (hello, cancellationToken) =>
                    host!.HandleHelloAsync(hello, cancellationToken)
            });
            host = new BridgeHost(
                manager,
                new PipeMessageSink(server),
                server.RunAsync,
                new ParentProcessMonitor(),
                options.ParentProcessId,
                defaultInstanceId,
                descriptors,
                [discovery, .. prefetched, server],
                diagnostics,
                providerUi,
                services,
                new BridgeSettingsStore(options.DataRoot));
            return host;
        }
        catch (Exception startupFailure)
        {
            var cleanupFailures = new List<Exception>();
            TryDisposeSynchronously(server, cleanupFailures);
            TryDisposeSynchronously(manager, cleanupFailures);
            foreach (var factory in prefetched.AsEnumerable().Reverse())
            {
                TryDisposeSynchronously(factory, cleanupFailures);
            }

            try
            {
                discovery.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            if (cleanupFailures.Count > 0)
            {
                cleanupFailures.Insert(0, startupFailure);
                throw new AggregateException("Bridge startup and cleanup failed.", cleanupFailures);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startupFailure).Throw();
            throw;
        }
    }

    private static void TryDisposeSynchronously(object? resource, ICollection<Exception> failures)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            switch (resource)
            {
                case IAsyncDisposable asyncDisposable:
                    asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    internal async ValueTask<HelloAckPayload> HandleHelloAsync(
        HelloPayload hello,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hello);
        await _helloGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var requestedTopology = Array.AsReadOnly(hello.Surfaces.ToArray());
            var initializationContext = new ProviderInitializationContext(
                requestedTopology,
                _services);
            var active = _manager.ActiveProvider;
            var targetInstanceId = active?.ProviderInstanceId ?? _preferredProviderInstanceId;
            if (active is not null && !TopologyEquals(_activeTopology, requestedTopology))
            {
                await _manager.StopAsync(cancellationToken).ConfigureAwait(false);
                if (!_messageSink.IsAcknowledged)
                {
                    _messageSink.DiscardStagedLatestFrame(active.ProviderId, active.ProviderInstanceId);
                }

                await FlushOutboundAsync().ConfigureAwait(false);
                active = null;
            }

            if (active is null && targetInstanceId is not null)
            {
                await _manager.SwitchAsync(
                    targetInstanceId,
                    initializationContext,
                    cancellationToken).ConfigureAwait(false);
                active = _manager.ActiveProvider;
            }

            _initializationContext = initializationContext;
            _activeTopology = requestedTopology;
            var capabilities = active is not null &&
                               _descriptors.TryGetValue(active.ProviderInstanceId, out var descriptor)
                ? descriptor.Capabilities
                : [];
            return new HelloAckPayload(
                "1.1.1",
                active is null
                    ? null
                    : new ProviderReferencePayload(active.ProviderId, active.ProviderInstanceId),
                capabilities);
        }
        finally
        {
            _helloGate.Release();
        }
    }

    internal async Task SwitchProviderAsync(
        string providerInstanceId,
        CancellationToken cancellationToken)
    {
        await _helloGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = _initializationContext
                ?? throw new InvalidOperationException("A Unity Hello topology is required before a provider can start.");
            await _manager.SwitchAsync(providerInstanceId, context, cancellationToken).ConfigureAwait(false);
            await FlushOutboundAsync().ConfigureAwait(false);
        }
        finally
        {
            _helloGate.Release();
        }
    }

    internal async Task SelectProviderAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A provider ID is required.", nameof(providerId));
        }

        var matches = AvailableProviders
            .Where(provider => string.Equals(provider.ProviderId, providerId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new KeyNotFoundException($"Provider '{providerId}' is not available.");
        }

        var target = matches.FirstOrDefault(provider => provider.IsAvailable)
            ?? throw new InvalidOperationException($"Provider '{providerId}' is unavailable.");

        await _helloGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_settingsStore is not null)
            {
                await _settingsStore.SaveAsync(
                        new BridgeSettings(BridgeSettings.CurrentSchemaVersion, target.ProviderId),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _preferredProviderInstanceId = target.ProviderInstanceId;
            var context = _initializationContext;
            if (context is null || string.Equals(
                    _manager.ActiveProvider?.ProviderInstanceId,
                    target.ProviderInstanceId,
                    StringComparison.Ordinal))
            {
                return;
            }

            await _manager.SwitchAsync(
                    target.ProviderInstanceId,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            await FlushOutboundAsync().ConfigureAwait(false);
        }
        finally
        {
            _helloGate.Release();
        }
    }

    internal object? CreateActiveProviderSettingsView(IProviderSettingsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var active = _manager.ActiveProvider;
        if (active is null ||
            !_providerUi.TryGetValue(active.ProviderInstanceId, out var registration) ||
            registration.SettingsViewFactory is null ||
            registration.ProviderFactory.CurrentProvider is not { } provider)
        {
            return null;
        }

        return registration.SettingsViewFactory.CreateView(provider, context);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var server = _runServerAsync(lifetime.Token);
        if (_parentProcessId is null)
        {
            await server.ConfigureAwait(false);
            return;
        }

        var parentExit = _parentMonitor.WaitForExitAsync(_parentProcessId.Value, lifetime.Token);
        var completed = await Task.WhenAny(server, parentExit).ConfigureAwait(false);
        lifetime.Cancel();
        try
        {
            await Task.WhenAll(server, parentExit).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }

        if (ReferenceEquals(completed, server))
        {
            await server.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            _hostStatus?.Terminate();
        }
        catch (Exception exception)
        {
            failure = AddFailure(failure, exception);
        }
        if (_hostStatus is not null)
        {
            _hostStatus.Changed -= OnUnityStatusChanged;
        }
        if (_interactionServer is not null)
        {
            _interactionServer.ClientConnected -= OnClientConnected;
            _interactionServer.ClientDisconnected -= OnClientDisconnected;
        }
        try
        {
            await _manager.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = AddFailure(failure, exception);
        }

        try
        {
            _outboundCancellation.CancelAfter(TimeSpan.FromSeconds(2));
            await FlushOutboundAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = AddFailure(failure, exception);
        }

        _manager.FrameReceived -= OnFrameReceived;
        _manager.ProviderChanged -= OnProviderChanged;
        _manager.StatusChanged -= OnStatusChanged;

        for (var index = _ownedResources.Count - 1; index >= 0; index--)
        {
            try
            {
                switch (_ownedResources[index])
                {
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;
                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch (Exception exception)
            {
                failure = AddFailure(failure, exception);
            }
        }

        _helloGate.Dispose();
        _outboundCancellation.Dispose();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static Exception AddFailure(Exception? current, Exception next) =>
        current is null ? next : new AggregateException(current, next);

    private static string? SelectDefaultProviderInstance(
        IReadOnlyDictionary<string, ProviderDescriptor> descriptors,
        string? preferredProviderId,
        bool useFallback)
    {
        if (!string.IsNullOrWhiteSpace(preferredProviderId))
        {
            return descriptors.FirstOrDefault(pair =>
                    string.Equals(pair.Value.Id, preferredProviderId, StringComparison.Ordinal))
                .Key;
        }

        if (!useFallback)
        {
            return null;
        }

        return descriptors.FirstOrDefault(pair =>
                string.Equals(pair.Value.Id, "blaze.radar.f10f20", StringComparison.Ordinal))
            .Key ?? descriptors.Keys.FirstOrDefault();
    }

    private static bool TopologyEquals(
        IReadOnlyList<InteractionSurface>? current,
        IReadOnlyList<InteractionSurface> requested)
    {
        return current is not null && current.SequenceEqual(requested);
    }

    private void OnFrameReceived(object? sender, InteractionFrameEventArgs eventArgs)
    {
        var frame = eventArgs.Frame;
        if (frame.Points.Any(point =>
                point.Phase == InteractionPhase.Down ||
                point.Phase == InteractionPhase.Up ||
                point.Phase == InteractionPhase.Cancel))
        {
            if (!_messageSink.IsAcknowledged)
            {
                return;
            }

            QueueOutbound(
                cancellationToken => _messageSink.SendReliableFrameAsync(frame, cancellationToken),
                requireSuccess: true);
            return;
        }

        try
        {
            _ = _messageSink.PublishFrameAsync(frame, _outboundCancellation.Token);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Latest-value Interaction frame publication failed: {0}", exception);
        }
    }

    private void OnClientConnected(object? sender, InteractionClientConnectedEventArgs eventArgs) =>
        _hostStatus?.ApplyConnected(eventArgs.Hello);

    private void OnClientDisconnected(object? sender, EventArgs eventArgs) =>
        _hostStatus?.ApplyDisconnected();

    private void OnProviderChanged(object? sender, ProviderChangedEventArgs eventArgs)
    {
        var activeProvider = eventArgs.Current is null
            ? null
            : AvailableProviders.FirstOrDefault(provider => string.Equals(
                provider.ProviderInstanceId,
                eventArgs.Current.ProviderInstanceId,
                StringComparison.Ordinal));
        UpdateSnapshot(snapshot => snapshot with
        {
            ActiveProvider = activeProvider,
            ProviderStatus = activeProvider is null ? null : ProviderRuntimeStatus.Running
        });

        try
        {
            ActiveProviderChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Provider UI notification failed: {0}", exception);
        }

        if (!_messageSink.IsAcknowledged ||
            (eventArgs.Previous is null && _initializationContext is null))
        {
            return;
        }

        var payload = new ProviderChangedPayload(
            eventArgs.Previous is null
                ? null
                : new ProviderReferencePayload(
                    eventArgs.Previous.ProviderId,
                    eventArgs.Previous.ProviderInstanceId),
            eventArgs.Current is null
                ? null
                : new ProviderReferencePayload(
                    eventArgs.Current.ProviderId,
                    eventArgs.Current.ProviderInstanceId),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        QueueOutbound(cancellationToken => _messageSink.SendAsync(
            InteractionEnvelope.Create(
                InteractionMessageType.ProviderChanged,
                Interlocked.Increment(ref _controlSequence),
                payload),
            cancellationToken),
            requireSuccess: false);
    }

    private void OnStatusChanged(object? sender, ProviderManagerStatusChangedEventArgs eventArgs)
    {
        UpdateSnapshot(snapshot =>
            string.Equals(
                snapshot.ActiveProvider?.ProviderInstanceId,
                eventArgs.Provider.ProviderInstanceId,
                StringComparison.Ordinal)
                ? snapshot with { ProviderStatus = eventArgs.Status }
                : snapshot);

        if (!_messageSink.IsAcknowledged)
        {
            return;
        }

        var payload = new StatusPayload(
            eventArgs.Error is null ? "provider_status" : "provider_error",
            eventArgs.Status.ToString(),
            eventArgs.Error?.Message ?? eventArgs.Status.ToString(),
            new ProviderReferencePayload(
                eventArgs.Provider.ProviderId,
                eventArgs.Provider.ProviderInstanceId),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        QueueOutbound(cancellationToken => _messageSink.SendAsync(
            InteractionEnvelope.Create(
                InteractionMessageType.Status,
                Interlocked.Increment(ref _controlSequence),
                payload),
            cancellationToken),
            requireSuccess: false);
    }

    private void OnUnityStatusChanged(InteractionHostStatus status) =>
        UpdateSnapshot(snapshot => snapshot with { Unity = status });

    private void UpdateSnapshot(Func<BridgeHostSnapshot, BridgeHostSnapshot> update)
    {
        Action<BridgeHostSnapshot>? handlers;
        BridgeHostSnapshot next;
        lock (_snapshotGate)
        {
            next = update(_currentSnapshot);
            if (Equals(next, _currentSnapshot))
            {
                return;
            }

            _currentSnapshot = next;
            handlers = SnapshotChanged;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Action<BridgeHostSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(next);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("Bridge UI status observer failed: {0}", exception);
            }
        }
    }

    private void QueueOutbound(
        Func<CancellationToken, ValueTask<bool>> operation,
        bool requireSuccess)
    {
        lock (_outboundGate)
        {
            _outboundTail = RunOutboundAfterAsync(
                _outboundTail,
                operation,
                requireSuccess,
                _outboundCancellation.Token);
        }
    }

    private static async Task RunOutboundAfterAsync(
        Task previous,
        Func<CancellationToken, ValueTask<bool>> operation,
        bool requireSuccess,
        CancellationToken cancellationToken)
    {
        await previous.ConfigureAwait(false);

        try
        {
            var sent = await operation(cancellationToken).ConfigureAwait(false);
            if (requireSuccess && !sent)
            {
                throw new IOException("The reliable Interaction IPC frame could not be queued.");
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            if (requireSuccess)
            {
                throw new IOException("The reliable Interaction IPC transaction was cancelled.", exception);
            }
        }
        catch (IOException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new IOException("The reliable Interaction IPC transaction failed.", exception);
        }
    }

    private async Task FlushOutboundAsync()
    {
        Task tail;
        lock (_outboundGate)
        {
            tail = _outboundTail;
        }

        try
        {
            await tail.ConfigureAwait(false);
        }
        finally
        {
            lock (_outboundGate)
            {
                if (ReferenceEquals(_outboundTail, tail))
                {
                    _outboundTail = Task.CompletedTask;
                }
            }
        }
    }

    private sealed class PipeMessageSink(InteractionPipeServer server) : IBridgeMessageSink
    {
        public bool IsConnected => server.IsClientConnected;

        public bool IsAcknowledged => server.IsClientAcknowledged;

        public ValueTask<bool> PublishFrameAsync(InteractionFrame frame, CancellationToken cancellationToken) =>
            server.PublishFrameAsync(frame, cancellationToken);

        public bool DiscardStagedLatestFrame(string providerId, string providerInstanceId) =>
            server.DiscardStagedLatestFrame(providerId, providerInstanceId);

        public ValueTask<bool> SendReliableFrameAsync(
            InteractionFrame frame,
            CancellationToken cancellationToken) =>
            server.SendReliableFrameAsync(frame, cancellationToken);

        public ValueTask<bool> SendAsync(InteractionEnvelope message, CancellationToken cancellationToken) =>
            server.SendAsync(message, cancellationToken);
    }

    private sealed class ParentProcessMonitor : IParentProcessMonitor
    {
        public async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
        {
            Process process;
            try
            {
                process = Process.GetProcessById(processId);
            }
            catch (ArgumentException)
            {
                return;
            }

            using (process)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        internal static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }

}

internal sealed class PrefetchedProviderFactory : IAsyncDisposable
{
    private readonly object _gate = new();
    private IInteractionProviderPlugin? _plugin;
    private ProviderCreateContext? _context;
    private IInteractionProvider? _prefetched;
    private bool _disposed;

    internal PrefetchedProviderFactory(
        IInteractionProviderPlugin plugin,
        ProviderCreateContext context)
    {
        _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        var provider = plugin.CreateProvider(context)
            ?? throw new InvalidOperationException("The provider plugin returned null.");
        _prefetched = provider;
        try
        {
            ProviderInstanceId = provider.ProviderInstanceId;
            ProviderReference = new WeakReference(provider);
        }
        catch (Exception activationException)
        {
            _prefetched = null;
            _plugin = null;
            _context = null;
            _disposed = true;
            try
            {
                provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(activationException, cleanupException);
            }

            throw;
        }
    }

    internal string ProviderInstanceId { get; }
    internal WeakReference ProviderReference { get; }

    internal IInteractionProvider? CurrentProvider
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? null : ProviderReference.Target as IInteractionProvider;
            }
        }
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _disposed;
            }
        }
    }

    internal IInteractionProvider Create()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_prefetched is not null)
            {
                var prefetched = _prefetched;
                _prefetched = null;
                return prefetched;
            }

            var provider = _plugin!.CreateProvider(_context!)
                ?? throw new InvalidOperationException("The provider plugin returned null.");
            ProviderReference.Target = provider;
            return provider;
        }
    }

    public async ValueTask DisposeAsync()
    {
        IInteractionProvider? provider;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            provider = _prefetched;
            _prefetched = null;
            _plugin = null;
            _context = null;
        }

        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal static class BridgeProviderRegistrar
{
    internal static PrefetchedProviderFactory? TryRegister(
        ProviderManager manager,
        IInteractionProviderPlugin plugin,
        ProviderCreateContext context,
        IDictionary<string, ProviderDescriptor> descriptors,
        ICollection<BridgeStartupDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(descriptors);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ProviderDescriptor? descriptor = null;
        PrefetchedProviderFactory? factory = null;
        try
        {
            descriptor = plugin.Descriptor;
            factory = new PrefetchedProviderFactory(plugin, context);
        }
        catch (Exception exception)
        {
            diagnostics.Add(new BridgeStartupDiagnostic(
                descriptor?.Id ?? "unknown",
                "CreateProvider",
                exception.Message));
            return null;
        }

        try
        {
            manager.Register(descriptor, factory.ProviderInstanceId, factory.Create);
            descriptors.Add(factory.ProviderInstanceId, descriptor);
            return factory;
        }
        catch (Exception exception)
        {
            try
            {
                factory.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception cleanupException)
            {
                exception = new AggregateException(exception, cleanupException);
            }

            diagnostics.Add(new BridgeStartupDiagnostic(
                descriptor.Id,
                "RegisterProvider",
                exception.Message));
            return null;
        }
    }
}

public sealed class BridgeProviderDiscoveryResult : IDisposable
{
    private int _disposed;

    internal BridgeProviderDiscoveryResult(
        IReadOnlyList<ProviderCatalogEntry> catalogEntries,
        IReadOnlyList<ProviderLoadResult> loadResults)
    {
        CatalogEntries = catalogEntries;
        LoadResults = loadResults;
        LoadedProviders = loadResults
            .Where(result => result.IsSuccess)
            .Select(result => result.Provider!)
            .ToArray();
    }

    public IReadOnlyList<ProviderCatalogEntry> CatalogEntries { get; }
    public IReadOnlyList<ProviderLoadResult> LoadResults { get; }
    public IReadOnlyList<LoadedProvider> LoadedProviders { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var provider in LoadedProviders.Reverse())
        {
            provider.Dispose();
        }
    }
}

public static class BridgeProviderDiscovery
{
    public static BridgeProviderDiscoveryResult Discover(string providersRoot)
    {
        var entries = new ProviderCatalog().Discover(providersRoot);
        var results = new ProviderLoader().LoadAll(entries);
        return new BridgeProviderDiscoveryResult(entries, results);
    }
}
