using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Runtime;

public enum ProviderFrameRejectionReason
{
    InvalidLifecycle = 0,
    IdentityMismatch = 1,
    NonIncreasingSequence = 2
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

public sealed class ProviderManagerDiagnosticEventArgs : EventArgs
{
    public ProviderManagerDiagnosticEventArgs(
        string operation,
        Exception exception,
        ProviderIdentity? provider = null)
    {
        Operation = string.IsNullOrWhiteSpace(operation)
            ? throw new ArgumentException("A diagnostic operation is required.", nameof(operation))
            : operation;
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
        Provider = provider;
    }

    public string Operation { get; }
    public Exception Exception { get; }
    public ProviderIdentity? Provider { get; }
}

public sealed class ProviderManager : IAsyncDisposable
{
    private readonly object _stateGate = new();
    private readonly object _disposeGate = new();
    private readonly AsyncLocal<int> _eventDispatchDepth = new();
    private readonly Dictionary<string, ProviderRegistration> _registrations = new(StringComparer.Ordinal);
    private readonly List<PendingCleanup> _pendingCleanup = [];
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly ActivePointRegistry _activePoints = new();
    private ActiveSession? _current;
    private ManagerLifecycle _lifecycle;
    private Exception? _fault;
    private Task? _disposeTask;
    private long _generation;
    private bool _disposeRequested;
    private bool _disposed;

    public event EventHandler<InteractionFrameEventArgs>? FrameReceived;
    public event EventHandler<ProviderManagerStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<ProviderChangedEventArgs>? ProviderChanged;
    public event EventHandler<ProviderFrameRejectedEventArgs>? FrameRejected;
    public event EventHandler<ProviderManagerDiagnosticEventArgs>? Diagnostic;

    public IReadOnlyList<ProviderIdentity> Providers
    {
        get
        {
            lock (_stateGate)
            {
                return _registrations.Values.Select(registration => registration.Identity).ToArray().AsReadOnly();
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

    public bool IsFaulted
    {
        get
        {
            lock (_stateGate)
            {
                return _lifecycle == ManagerLifecycle.Faulted;
            }
        }
    }

    public Exception? Fault
    {
        get
        {
            lock (_stateGate)
            {
                return _fault;
            }
        }
    }

    public void Register(
        ProviderDescriptor descriptor,
        string providerInstanceId,
        Func<IInteractionProvider> providerFactory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.IsNullOrWhiteSpace(providerInstanceId))
        {
            throw new ArgumentException("A provider instance ID is required.", nameof(providerInstanceId));
        }

        ArgumentNullException.ThrowIfNull(providerFactory);
        var registration = new ProviderRegistration(
            new ProviderIdentity
            {
                ProviderId = descriptor.Id,
                ProviderInstanceId = providerInstanceId
            },
            providerFactory);

        lock (_stateGate)
        {
            ThrowIfUnavailable();
            if (!_registrations.TryAdd(providerInstanceId, registration))
            {
                throw new InvalidOperationException(
                    $"A provider with instance ID '{providerInstanceId}' is already registered.");
            }
        }
    }

    public async Task SwitchAsync(
        string providerInstanceId,
        ProviderInitializationContext initializationContext,
        CancellationToken cancellationToken)
    {
        ThrowIfLifecycleReentry(nameof(SwitchAsync));
        if (string.IsNullOrWhiteSpace(providerInstanceId))
        {
            throw new ArgumentException("A provider instance ID is required.", nameof(providerInstanceId));
        }

        ArgumentNullException.ThrowIfNull(initializationContext);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProviderRegistration target;
            ProviderIdentity? previous;
            lock (_stateGate)
            {
                ThrowIfUnavailable();
                if (_lifecycle == ManagerLifecycle.Running
                    && string.Equals(_current?.Identity.ProviderInstanceId, providerInstanceId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!_registrations.TryGetValue(providerInstanceId, out target!))
                {
                    throw new KeyNotFoundException($"Provider instance '{providerInstanceId}' is not registered.");
                }

                previous = _lifecycle == ManagerLifecycle.Running ? _current?.Identity : null;
            }

            if (previous is not null)
            {
                try
                {
                    await DeactivateCurrentAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    PublishProviderChanged(previous, null);
                    throw;
                }
            }

            try
            {
                await CreateAndActivateAsync(target, initializationContext, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (previous is not null)
                {
                    PublishProviderChanged(previous, null);
                }

                throw;
            }

            PublishProviderChanged(previous, target.Identity);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        ThrowIfLifecycleReentry(nameof(StopAsync));
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProviderIdentity? previous;
            lock (_stateGate)
            {
                ThrowIfUnavailable();
                previous = _lifecycle == ManagerLifecycle.Running ? _current?.Identity : null;
            }

            if (previous is null)
            {
                return;
            }

            Exception? failure = null;
            try
            {
                await DeactivateCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            PublishProviderChanged(previous, null);
            if (failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        ThrowIfLifecycleReentry(nameof(DisposeAsync));
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                lock (_stateGate)
                {
                    _disposeRequested = true;
                }

                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task CreateAndActivateAsync(
        ProviderRegistration registration,
        ProviderInitializationContext initializationContext,
        CancellationToken cancellationToken)
    {
        IInteractionProvider provider;
        try
        {
            provider = registration.Factory()
                ?? throw new InvalidOperationException("The provider factory returned null.");
        }
        catch (Exception exception)
        {
            PublishDiagnostic("CreateProvider", exception, registration.Identity);
            throw;
        }

        string? actualInstanceId = null;
        Exception? identityFailure = null;
        try
        {
            actualInstanceId = provider.ProviderInstanceId;
            if (!string.Equals(actualInstanceId, registration.Identity.ProviderInstanceId, StringComparison.Ordinal))
            {
                identityFailure = new InvalidOperationException(
                    $"The provider factory returned instance ID '{actualInstanceId}' instead of the registered ID '{registration.Identity.ProviderInstanceId}'.");
            }
        }
        catch (Exception exception)
        {
            identityFailure = exception;
        }

        if (identityFailure is not null)
        {
            PublishDiagnostic("ValidateProviderInstance", identityFailure, registration.Identity);
            var disposeFailure = await TryDisposeProviderAsync(provider, registration.Identity).ConfigureAwait(false);
            if (disposeFailure is not null)
            {
                MarkFault(disposeFailure);
                throw new AggregateException(identityFailure, disposeFailure);
            }

            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(identityFailure).Throw();
        }

        var session = new ActiveSession(
            this,
            registration.Identity,
            provider,
            Interlocked.Increment(ref _generation));
        lock (_stateGate)
        {
            _current = session;
            _lifecycle = ManagerLifecycle.Initializing;
        }

        try
        {
            Subscribe(session);
            await provider.InitializeAsync(initializationContext, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                _lifecycle = ManagerLifecycle.Starting;
            }

            await provider.StartAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                _lifecycle = ManagerLifecycle.Running;
            }
        }
        catch (Exception activationFailure)
        {
            var cleanupFailures = await CleanupFailedActivationAsync(session).ConfigureAwait(false);
            if (cleanupFailures.Count == 0)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(activationFailure).Throw();
            }

            cleanupFailures.Insert(0, activationFailure);
            throw new AggregateException("Provider activation and cleanup failed.", cleanupFailures);
        }
    }

    private async Task DeactivateCurrentAsync(CancellationToken cancellationToken)
    {
        ActiveSession session;
        Task callbacksDrained;
        lock (_stateGate)
        {
            session = _current ?? throw new InvalidOperationException("There is no current provider to stop.");
            callbacksDrained = session.Callbacks.CloseAndGetDrainTask();
            _lifecycle = ManagerLifecycle.Stopping;
        }

        await callbacksDrained.ConfigureAwait(false);
        PublishCancellationFrames(session.Identity);
        Unsubscribe(session);

        Exception? stopFailure = null;
        try
        {
            await session.Provider.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopFailure = exception;
            PublishDiagnostic(nameof(IInteractionProvider.StopAsync), exception, session.Identity);
        }

        var disposeFailure = await TryDisposeProviderAsync(session.Provider, session.Identity).ConfigureAwait(false);
        _activePoints.Discard(session.Identity);
        lock (_stateGate)
        {
            _current = null;
            if (disposeFailure is null)
            {
                _lifecycle = ManagerLifecycle.Idle;
            }
            else
            {
                _fault = disposeFailure;
                _lifecycle = ManagerLifecycle.Faulted;
            }
        }

        if (stopFailure is not null && disposeFailure is not null)
        {
            throw new AggregateException("Provider stop and disposal failed.", stopFailure, disposeFailure);
        }

        if (disposeFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(disposeFailure).Throw();
        }

        if (stopFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
        }
    }

    private async Task<List<Exception>> CleanupFailedActivationAsync(ActiveSession session)
    {
        var failures = new List<Exception>();
        Task callbacksDrained;
        lock (_stateGate)
        {
            callbacksDrained = session.Callbacks.CloseAndGetDrainTask();
            _lifecycle = ManagerLifecycle.Stopping;
        }

        await callbacksDrained.ConfigureAwait(false);
        Unsubscribe(session);
        try
        {
            await session.Provider.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
            PublishDiagnostic(nameof(IInteractionProvider.StopAsync), exception, session.Identity);
        }

        var disposeFailure = await TryDisposeProviderAsync(session.Provider, session.Identity).ConfigureAwait(false);
        if (disposeFailure is not null)
        {
            failures.Add(disposeFailure);
        }

        _activePoints.Discard(session.Identity);
        lock (_stateGate)
        {
            _current = null;
            if (disposeFailure is null)
            {
                _lifecycle = ManagerLifecycle.Idle;
            }
            else
            {
                _fault = disposeFailure;
                _lifecycle = ManagerLifecycle.Faulted;
            }
        }

        return failures;
    }

    private async Task<Exception?> TryDisposeProviderAsync(
        IInteractionProvider provider,
        ProviderIdentity identity)
    {
        try
        {
            await provider.DisposeAsync().ConfigureAwait(false);
            lock (_stateGate)
            {
                _pendingCleanup.RemoveAll(item => ReferenceEquals(item.Provider, provider));
            }

            return null;
        }
        catch (Exception exception)
        {
            lock (_stateGate)
            {
                if (!_pendingCleanup.Any(item => ReferenceEquals(item.Provider, provider)))
                {
                    _pendingCleanup.Add(new PendingCleanup(provider, identity));
                }
            }

            PublishDiagnostic(nameof(IAsyncDisposable.DisposeAsync), exception, identity);
            return exception;
        }
    }

    private void Subscribe(ActiveSession session)
    {
        session.Provider.FrameReceived += session.FrameHandler;
        session.Provider.StatusChanged += session.StatusHandler;
    }

    private void Unsubscribe(ActiveSession session)
    {
        try
        {
            session.Provider.FrameReceived -= session.FrameHandler;
        }
        catch (Exception exception)
        {
            PublishDiagnostic("UnsubscribeFrame", exception, session.Identity);
        }

        try
        {
            session.Provider.StatusChanged -= session.StatusHandler;
        }
        catch (Exception exception)
        {
            PublishDiagnostic("UnsubscribeStatus", exception, session.Identity);
        }
    }

    private void HandleFrame(ActiveSession session, InteractionFrame frame)
    {
        if (!session.Callbacks.TryEnter())
        {
            return;
        }

        try
        {
            lock (session.FrameDispatchGate)
            {
                ManagerLifecycle lifecycle;
                bool isCurrent;
                lock (_stateGate)
                {
                    lifecycle = _lifecycle;
                    isCurrent = ReferenceEquals(_current, session) && _generation == session.Generation;
                }

                if (!isCurrent || lifecycle != ManagerLifecycle.Running)
                {
                    PublishFrameRejected(
                        new ProviderFrameRejectedEventArgs(
                            frame,
                            ProviderFrameRejectionReason.InvalidLifecycle,
                            "The provider emitted a frame outside its active running lifecycle."),
                        session.Identity);
                    return;
                }

                if (!HasExpectedIdentity(session.Identity, frame))
                {
                    PublishFrameRejected(
                        new ProviderFrameRejectedEventArgs(
                            frame,
                            ProviderFrameRejectionReason.IdentityMismatch,
                            "The frame identity does not match the active provider and surface."),
                        session.Identity);
                    return;
                }

                if (!session.TryAdvanceSequence(frame.SurfaceId, frame.Sequence))
                {
                    PublishFrameRejected(
                        new ProviderFrameRejectedEventArgs(
                            frame,
                            ProviderFrameRejectionReason.NonIncreasingSequence,
                            "The frame sequence must increase monotonically for each provider instance and surface."),
                        session.Identity);
                    return;
                }

                _activePoints.Apply(frame);
                InvokeSubscribers(FrameReceived, new InteractionFrameEventArgs(frame), nameof(FrameReceived), session.Identity);
            }
        }
        finally
        {
            session.Callbacks.Exit();
        }
    }

    private void HandleStatus(ActiveSession session, ProviderStatusChangedEventArgs status)
    {
        if (!session.Callbacks.TryEnter())
        {
            return;
        }

        try
        {
            lock (_stateGate)
            {
                if (!ReferenceEquals(_current, session) || _generation != session.Generation)
                {
                    return;
                }
            }

            InvokeSubscribers(
                StatusChanged,
                new ProviderManagerStatusChangedEventArgs(
                    session.Identity,
                    status.PreviousStatus,
                    status.Status,
                    status.Error),
                nameof(StatusChanged),
                session.Identity);
        }
        finally
        {
            session.Callbacks.Exit();
        }
    }

    private void PublishCancellationFrames(ProviderIdentity identity)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var frame in _activePoints.DrainCancellationFrames(identity, timestamp))
        {
            InvokeSubscribers(FrameReceived, new InteractionFrameEventArgs(frame), nameof(FrameReceived), identity);
        }
    }

    private void PublishProviderChanged(ProviderIdentity? previous, ProviderIdentity? current)
    {
        InvokeSubscribers(
            ProviderChanged,
            new ProviderChangedEventArgs(previous, current),
            nameof(ProviderChanged),
            current ?? previous);
    }

    private void PublishFrameRejected(ProviderFrameRejectedEventArgs args, ProviderIdentity identity)
    {
        InvokeSubscribers(FrameRejected, args, nameof(FrameRejected), identity);
    }

    private void InvokeSubscribers<TEventArgs>(
        EventHandler<TEventArgs>? subscribers,
        TEventArgs args,
        string operation,
        ProviderIdentity? identity)
        where TEventArgs : EventArgs
    {
        if (subscribers is null)
        {
            return;
        }

        foreach (var subscriber in subscribers.GetInvocationList().Cast<EventHandler<TEventArgs>>())
        {
            using var dispatch = EnterEventDispatch();
            try
            {
                subscriber(this, args);
            }
            catch (Exception exception)
            {
                PublishDiagnostic(operation, exception, identity);
            }
        }
    }

    private void PublishDiagnostic(string operation, Exception exception, ProviderIdentity? identity)
    {
        var subscribers = Diagnostic;
        if (subscribers is null)
        {
            return;
        }

        var args = new ProviderManagerDiagnosticEventArgs(operation, exception, identity);
        foreach (var subscriber in subscribers.GetInvocationList().Cast<EventHandler<ProviderManagerDiagnosticEventArgs>>())
        {
            using var dispatch = EnterEventDispatch();
            try
            {
                subscriber(this, args);
            }
            catch
            {
                // Diagnostic consumers cannot affect provider lifecycle or other consumers.
            }
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
        try
        {
            ProviderIdentity? previous;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                previous = _current?.Identity;
            }

            if (previous is not null)
            {
                try
                {
                    await DeactivateCurrentAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (errors ??= []).Add(exception);
                }

                PublishProviderChanged(previous, null);
            }

            PendingCleanup[] pending;
            lock (_stateGate)
            {
                pending = _pendingCleanup.ToArray();
            }

            foreach (var item in pending)
            {
                var retryFailure = await TryDisposeProviderAsync(item.Provider, item.Identity).ConfigureAwait(false);
                if (retryFailure is not null)
                {
                    (errors ??= []).Add(retryFailure);
                }
            }

            lock (_stateGate)
            {
                _registrations.Clear();
                _current = null;
                _disposed = true;
                _lifecycle = ManagerLifecycle.Disposed;
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

    private void MarkFault(Exception exception)
    {
        lock (_stateGate)
        {
            _fault = exception;
            _lifecycle = ManagerLifecycle.Faulted;
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested || _disposed, this);
        if (_lifecycle == ManagerLifecycle.Faulted)
        {
            throw new InvalidOperationException(
                "The provider manager is faulted because provider cleanup could not be confirmed.",
                _fault);
        }
    }

    private void ThrowIfLifecycleReentry(string operation)
    {
        if (_eventDispatchDepth.Value > 0)
        {
            throw new InvalidOperationException(
                $"{operation} cannot be called synchronously from a ProviderManager outbound event handler.");
        }
    }

    private IDisposable EnterEventDispatch()
    {
        _eventDispatchDepth.Value++;
        return new EventDispatchScope(this);
    }

    private sealed record ProviderRegistration(
        ProviderIdentity Identity,
        Func<IInteractionProvider> Factory);

    private sealed record PendingCleanup(
        IInteractionProvider Provider,
        ProviderIdentity Identity);

    private sealed class ActiveSession
    {
        private readonly Dictionary<string, long> _lastSequenceBySurface = new(StringComparer.Ordinal);

        internal ActiveSession(
            ProviderManager owner,
            ProviderIdentity identity,
            IInteractionProvider provider,
            long generation)
        {
            Identity = identity;
            Provider = provider;
            Generation = generation;
            FrameHandler = (_, args) => owner.HandleFrame(this, args.Frame);
            StatusHandler = (_, args) => owner.HandleStatus(this, args);
        }

        internal ProviderIdentity Identity { get; }
        internal IInteractionProvider Provider { get; }
        internal long Generation { get; }
        internal object FrameDispatchGate { get; } = new();
        internal CallbackAdmissionGate Callbacks { get; } = new();
        internal EventHandler<InteractionFrameEventArgs> FrameHandler { get; }
        internal EventHandler<ProviderStatusChangedEventArgs> StatusHandler { get; }

        internal bool TryAdvanceSequence(string surfaceId, long sequence)
        {
            if (_lastSequenceBySurface.TryGetValue(surfaceId, out var previous) && sequence <= previous)
            {
                return false;
            }

            _lastSequenceBySurface[surfaceId] = sequence;
            return true;
        }
    }

    private sealed class EventDispatchScope(ProviderManager owner) : IDisposable
    {
        private ProviderManager? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
            {
                current._eventDispatchDepth.Value--;
            }
        }
    }

    private sealed class CallbackAdmissionGate
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _drained;
        private bool _accepting = true;
        private int _inFlight;

        internal bool TryEnter()
        {
            lock (_gate)
            {
                if (!_accepting)
                {
                    return false;
                }

                _inFlight++;
                return true;
            }
        }

        internal void Exit()
        {
            TaskCompletionSource? drained = null;
            lock (_gate)
            {
                if (_inFlight <= 0)
                {
                    throw new InvalidOperationException("The callback admission count is unbalanced.");
                }

                _inFlight--;
                if (!_accepting && _inFlight == 0)
                {
                    drained = _drained;
                }
            }

            drained?.TrySetResult();
        }

        internal Task CloseAndGetDrainTask()
        {
            lock (_gate)
            {
                _accepting = false;
                if (_inFlight == 0)
                {
                    return Task.CompletedTask;
                }

                _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _drained.Task;
            }
        }
    }

    private enum ManagerLifecycle
    {
        Idle = 0,
        Initializing = 1,
        Starting = 2,
        Running = 3,
        Stopping = 4,
        Faulted = 5,
        Disposed = 6
    }
}
