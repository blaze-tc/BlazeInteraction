using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Runtime;

public enum ProviderFrameRejectionReason
{
    InvalidLifecycle = 0,
    IdentityMismatch = 1
}

public sealed class ProviderManagerStatusChangedEventArgs : EventArgs
{
    public ProviderManagerStatusChangedEventArgs(
        ProviderIdentity provider,
        ProviderRuntimeStatus previousStatus,
        ProviderRuntimeStatus status,
        Exception? error)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        PreviousStatus = previousStatus;
        Status = status;
        Error = error;
    }

    public ProviderIdentity Provider { get; }
    public ProviderRuntimeStatus PreviousStatus { get; }
    public ProviderRuntimeStatus Status { get; }
    public Exception? Error { get; }
}

public sealed class ProviderChangedEventArgs : EventArgs
{
    public ProviderChangedEventArgs(ProviderIdentity? previous, ProviderIdentity? current)
    {
        Previous = previous;
        Current = current;
    }

    public ProviderIdentity? Previous { get; }
    public ProviderIdentity? Current { get; }
}

public sealed class ProviderFrameRejectedEventArgs : EventArgs
{
    public ProviderFrameRejectedEventArgs(
        InteractionFrame frame,
        ProviderFrameRejectionReason reason,
        string message)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        Reason = reason;
        Message = string.IsNullOrWhiteSpace(message)
            ? throw new ArgumentException("A rejection message is required.", nameof(message))
            : message;
    }

    public InteractionFrame Frame { get; }
    public ProviderFrameRejectionReason Reason { get; }
    public string Message { get; }
}

public sealed class ProviderManager : IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly object _disposeGate = new();
    private readonly Dictionary<string, ProviderEntry> _providers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly ActivePointRegistry _activePoints = new();
    private ProviderEntry? _current;
    private ManagerLifecycle _lifecycle;
    private Task? _disposeTask;
    private bool _disposed;

    public event EventHandler<InteractionFrameEventArgs>? FrameReceived;
    public event EventHandler<ProviderManagerStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<ProviderChangedEventArgs>? ProviderChanged;
    public event EventHandler<ProviderFrameRejectedEventArgs>? FrameRejected;

    public IReadOnlyList<ProviderIdentity> Providers
    {
        get
        {
            lock (_stateGate)
            {
                return _providers.Values.Select(entry => entry.Identity).ToArray().AsReadOnly();
            }
        }
    }

    public ProviderIdentity? ActiveProvider
    {
        get
        {
            lock (_stateGate)
            {
                return _lifecycle == ManagerLifecycle.Running ? _current?.Identity : null;
            }
        }
    }

    public void Register(ProviderDescriptor descriptor, IInteractionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.ProviderInstanceId))
        {
            throw new ArgumentException("The provider instance ID cannot be null, empty, or whitespace.", nameof(provider));
        }

        var entry = new ProviderEntry(
            this,
            new ProviderIdentity
            {
                ProviderId = descriptor.Id,
                ProviderInstanceId = provider.ProviderInstanceId
            },
            provider);

        lock (_stateGate)
        {
            ThrowIfDisposed();
            if (!_providers.TryAdd(provider.ProviderInstanceId, entry))
            {
                throw new InvalidOperationException(
                    $"A provider with instance ID '{provider.ProviderInstanceId}' is already registered.");
            }
        }
    }

    public async Task SwitchAsync(
        string providerInstanceId,
        ProviderInitializationContext initializationContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerInstanceId))
        {
            throw new ArgumentException("A provider instance ID is required.", nameof(providerInstanceId));
        }

        ArgumentNullException.ThrowIfNull(initializationContext);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProviderEntry target;
            ProviderIdentity? previous;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                if (_lifecycle == ManagerLifecycle.Running
                    && string.Equals(_current?.Identity.ProviderInstanceId, providerInstanceId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!_providers.TryGetValue(providerInstanceId, out target!))
                {
                    throw new KeyNotFoundException($"Provider instance '{providerInstanceId}' is not registered.");
                }

                previous = _lifecycle == ManagerLifecycle.Running ? _current?.Identity : null;
            }

            if (previous is not null)
            {
                await DeactivateCurrentAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await ActivateAsync(target, initializationContext, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (previous is not null)
                {
                    ProviderChanged?.Invoke(this, new ProviderChangedEventArgs(previous, null));
                }

                throw;
            }

            ProviderChanged?.Invoke(this, new ProviderChangedEventArgs(previous, target.Identity));
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProviderIdentity? previous;
            lock (_stateGate)
            {
                ThrowIfDisposed();
                previous = _lifecycle == ManagerLifecycle.Running ? _current?.Identity : null;
            }

            if (previous is null)
            {
                return;
            }

            await DeactivateCurrentAsync(cancellationToken).ConfigureAwait(false);
            ProviderChanged?.Invoke(this, new ProviderChangedEventArgs(previous, null));
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task ActivateAsync(
        ProviderEntry entry,
        ProviderInitializationContext initializationContext,
        CancellationToken cancellationToken)
    {
        Subscribe(entry);
        lock (_stateGate)
        {
            _current = entry;
            _lifecycle = ManagerLifecycle.Initializing;
        }

        try
        {
            await entry.Provider.InitializeAsync(initializationContext, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                _lifecycle = ManagerLifecycle.Starting;
            }

            await entry.Provider.StartAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                _lifecycle = ManagerLifecycle.Running;
            }
        }
        catch
        {
            await CleanupFailedActivationAsync(entry).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DeactivateCurrentAsync(CancellationToken cancellationToken)
    {
        ProviderEntry entry;
        lock (_stateGate)
        {
            entry = _current ?? throw new InvalidOperationException("There is no current provider to stop.");
            _lifecycle = ManagerLifecycle.Stopping;
        }

        PublishCancellationFrames(entry.Identity);
        try
        {
            await entry.Provider.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_stateGate)
            {
                _lifecycle = ManagerLifecycle.Running;
            }

            throw;
        }

        Unsubscribe(entry);
        lock (_stateGate)
        {
            _current = null;
            _lifecycle = ManagerLifecycle.Idle;
        }
    }

    private async Task CleanupFailedActivationAsync(ProviderEntry entry)
    {
        lock (_stateGate)
        {
            _lifecycle = ManagerLifecycle.Stopping;
        }

        try
        {
            await entry.Provider.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup must continue so a failed provider cannot remain subscribed or undisposed.
        }

        Unsubscribe(entry);
        _activePoints.Discard(entry.Identity);
        try
        {
            await entry.Provider.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Preserve the activation exception while still removing the failed instance.
        }

        lock (_stateGate)
        {
            _providers.Remove(entry.Identity.ProviderInstanceId);
            _current = null;
            _lifecycle = ManagerLifecycle.Idle;
        }
    }

    private void Subscribe(ProviderEntry entry)
    {
        entry.Provider.FrameReceived += entry.FrameHandler;
        entry.Provider.StatusChanged += entry.StatusHandler;
    }

    private void Unsubscribe(ProviderEntry entry)
    {
        entry.Provider.FrameReceived -= entry.FrameHandler;
        entry.Provider.StatusChanged -= entry.StatusHandler;
    }

    private void HandleFrame(ProviderEntry entry, InteractionFrame frame)
    {
        ManagerLifecycle lifecycle;
        bool isCurrent;
        lock (_stateGate)
        {
            lifecycle = _lifecycle;
            isCurrent = ReferenceEquals(_current, entry);
        }

        if (!isCurrent || lifecycle != ManagerLifecycle.Running)
        {
            FrameRejected?.Invoke(
                this,
                new ProviderFrameRejectedEventArgs(
                    frame,
                    ProviderFrameRejectionReason.InvalidLifecycle,
                    "The provider emitted a frame outside its active running lifecycle."));
            return;
        }

        if (!HasExpectedIdentity(entry.Identity, frame))
        {
            FrameRejected?.Invoke(
                this,
                new ProviderFrameRejectedEventArgs(
                    frame,
                    ProviderFrameRejectionReason.IdentityMismatch,
                    "The frame identity does not match the active provider and surface."));
            return;
        }

        _activePoints.Apply(frame);
        FrameReceived?.Invoke(this, new InteractionFrameEventArgs(frame));
    }

    private void HandleStatus(ProviderEntry entry, ProviderStatusChangedEventArgs status)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_current, entry))
            {
                return;
            }
        }

        StatusChanged?.Invoke(
            this,
            new ProviderManagerStatusChangedEventArgs(
                entry.Identity,
                status.PreviousStatus,
                status.Status,
                status.Error));
    }

    private void PublishCancellationFrames(ProviderIdentity identity)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var frame in _activePoints.DrainCancellationFrames(identity, timestamp))
        {
            FrameReceived?.Invoke(this, new InteractionFrameEventArgs(frame));
        }
    }

    private static bool HasExpectedIdentity(ProviderIdentity identity, InteractionFrame frame)
    {
        return string.Equals(identity.ProviderId, frame.ProviderId, StringComparison.Ordinal)
               && string.Equals(identity.ProviderInstanceId, frame.ProviderInstanceId, StringComparison.Ordinal)
               && frame.Points.All(point =>
                   string.Equals(frame.ProviderId, point.ProviderId, StringComparison.Ordinal)
                   && string.Equals(frame.ProviderInstanceId, point.ProviderInstanceId, StringComparison.Ordinal)
                   && string.Equals(frame.SurfaceId, point.SurfaceId, StringComparison.Ordinal));
    }

    private async Task DisposeCoreAsync()
    {
        await _transitionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        List<Exception>? errors = null;
        ProviderEntry[] providers;
        try
        {
            ProviderEntry? current;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                current = _current;
                if (current is not null)
                {
                    _lifecycle = ManagerLifecycle.Stopping;
                }
            }

            if (current is not null)
            {
                try
                {
                    PublishCancellationFrames(current.Identity);
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }

                try
                {
                    await current.Provider.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }

                Unsubscribe(current);
            }

            lock (_stateGate)
            {
                providers = _providers.Values.ToArray();
                _providers.Clear();
                _current = null;
                _lifecycle = ManagerLifecycle.Disposed;
            }

            foreach (var entry in providers)
            {
                try
                {
                    await entry.Provider.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }
            }

            _activePoints.Dispose();
        }
        finally
        {
            _transitionGate.Release();
        }

        if (errors is not null)
        {
            throw new AggregateException("One or more provider resources failed to dispose.", errors);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class ProviderEntry
    {
        internal ProviderEntry(
            ProviderManager owner,
            ProviderIdentity identity,
            IInteractionProvider provider)
        {
            Identity = identity;
            Provider = provider;
            FrameHandler = (_, args) => owner.HandleFrame(this, args.Frame);
            StatusHandler = (_, args) => owner.HandleStatus(this, args);
        }

        internal ProviderIdentity Identity { get; }
        internal IInteractionProvider Provider { get; }
        internal EventHandler<InteractionFrameEventArgs> FrameHandler { get; }
        internal EventHandler<ProviderStatusChangedEventArgs> StatusHandler { get; }
    }

    private enum ManagerLifecycle
    {
        Idle = 0,
        Initializing = 1,
        Starting = 2,
        Running = 3,
        Stopping = 4,
        Disposed = 5
    }
}
