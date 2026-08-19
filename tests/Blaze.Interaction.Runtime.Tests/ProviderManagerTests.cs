using System.Collections.Concurrent;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Runtime.Tests;

public sealed class ProviderManagerTests
{
    private static readonly ProviderInitializationContext InitializationContext = new(
        [Surface("FRONT"), Surface("LEFT")],
        EmptyServices.Instance);

    [Fact]
    public async Task SwitchAsync_InitializesAndStartsOnlyTheSelectedProviderThenPublishesChange()
    {
        var log = new List<string>();
        var first = new TestProvider("first", log);
        var second = new TestProvider("second", log);
        await using var manager = CreateManager(first, second);
        manager.ProviderChanged += (_, change) =>
            log.Add($"changed:{change.Previous?.ProviderInstanceId ?? "none"}->{change.Current?.ProviderInstanceId ?? "none"}");

        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        Assert.Equal(["first.initialize", "first.start", "changed:none->first"], log);
        Assert.Equal(new ProviderIdentity { ProviderId = "blaze.test", ProviderInstanceId = "first" }, manager.ActiveProvider);
        Assert.Equal(2, manager.Providers.Count);
        Assert.Equal(ProviderRuntimeStatus.Created, second.Status);
    }

    [Fact]
    public async Task SwitchAsync_ActivePointsAreCancelledBeforeOldProviderStopsAndNewProviderStarts()
    {
        var log = new List<string>();
        var first = new TestProvider("first", log);
        var second = new TestProvider("second", log);
        await using var manager = CreateManager(first, second);
        var forwarded = new List<InteractionFrame>();
        manager.FrameReceived += (_, args) =>
        {
            forwarded.Add(args.Frame);
            if (args.Frame.Points.Any(point => point.Phase == InteractionPhase.Cancel))
            {
                log.Add("cancel");
            }
        };
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        first.Emit(Frame("first", "FRONT", 10, Point("first", "FRONT", 7, InteractionPhase.Down)));
        first.Emit(Frame("first", "LEFT", 20, Point("first", "LEFT", 7, InteractionPhase.Hover)));
        log.Clear();

        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);

        Assert.Equal(["cancel", "cancel", "first.stop", "second.initialize", "second.start"], log.Take(5));
        var cancellations = forwarded.Where(frame => frame.Points.Any(point => point.Phase == InteractionPhase.Cancel)).ToArray();
        Assert.Equal(2, cancellations.Length);
        Assert.Equal(["FRONT", "LEFT"], cancellations.Select(frame => frame.SurfaceId));
        Assert.All(cancellations.SelectMany(frame => frame.Points), point => Assert.Equal(InteractionPhase.Cancel, point.Phase));
        Assert.All(cancellations, frame => Assert.True(frame.Sequence is 11 or 21));
    }

    [Fact]
    public async Task SwitchAsync_ReverseSwitchRestartsOriginalInstanceWithoutOverlappingProviders()
    {
        var running = 0;
        var maximumRunning = 0;
        var log = new List<string>();
        var first = new TestProvider("first", log)
        {
            OnStarted = () => maximumRunning = Math.Max(maximumRunning, Interlocked.Increment(ref running)),
            OnStopped = () => Interlocked.Decrement(ref running)
        };
        var second = new TestProvider("second", log)
        {
            OnStarted = () => maximumRunning = Math.Max(maximumRunning, Interlocked.Increment(ref running)),
            OnStopped = () => Interlocked.Decrement(ref running)
        };
        await using var manager = CreateManager(first, second);

        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        Assert.Equal(1, maximumRunning);
        Assert.Equal(1, running);
        Assert.Equal(2, first.InitializeCount);
        Assert.Equal(2, first.StartCount);
        Assert.Equal(1, first.StopCount);
        Assert.Equal("first", manager.ActiveProvider?.ProviderInstanceId);
    }

    [Fact]
    public async Task SwitchAsync_RepeatedAndConcurrentRequestsAreSerializedAndNeverDoubleStart()
    {
        var transition = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maximumRunning = 0;
        var first = new TestProvider("first")
        {
            OnStarted = () => maximumRunning = Math.Max(maximumRunning, Interlocked.Increment(ref running)),
            OnStopped = () => Interlocked.Decrement(ref running)
        };
        var second = new TestProvider("second")
        {
            BeforeStartAsync = async _ =>
            {
                transition.TrySetResult();
                await release.Task;
            },
            OnStarted = () => maximumRunning = Math.Max(maximumRunning, Interlocked.Increment(ref running)),
            OnStopped = () => Interlocked.Decrement(ref running)
        };
        await using var manager = CreateManager(first, second);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        var toSecond = manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        await transition.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var repeated = manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        Assert.False(repeated.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(toSecond, repeated);

        Assert.Equal(1, maximumRunning);
        Assert.Equal(1, second.StartCount);
        Assert.Equal("second", manager.ActiveProvider?.ProviderInstanceId);
    }

    [Fact]
    public async Task FrameAndStatusEvents_ForwardOnlyFromCurrentProviderUsingProviderNeutralIdentity()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        await using var manager = CreateManager(first, second);
        var frames = new List<InteractionFrame>();
        var statuses = new List<ProviderManagerStatusChangedEventArgs>();
        manager.FrameReceived += (_, args) => frames.Add(args.Frame);
        manager.StatusChanged += (_, args) => statuses.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        statuses.Clear();

        var expected = Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Move));
        first.Emit(expected);
        first.EmitStatus(ProviderRuntimeStatus.Running, ProviderRuntimeStatus.Faulted, new InvalidOperationException("sensor"));
        second.Emit(Frame("second", "FRONT", 2, Point("second", "FRONT", 2, InteractionPhase.Move)));

        Assert.Same(expected, Assert.Single(frames));
        var status = Assert.Single(statuses);
        Assert.Equal("blaze.test", status.Provider.ProviderId);
        Assert.Equal("first", status.Provider.ProviderInstanceId);
        Assert.Equal(ProviderRuntimeStatus.Faulted, status.Status);
        Assert.IsType<InvalidOperationException>(status.Error);
    }

    [Fact]
    public async Task SwitchAsync_OldProviderEventsAreUnsubscribedAfterSwitch()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        await using var manager = CreateManager(first, second);
        var frames = new List<InteractionFrame>();
        var statuses = new List<ProviderManagerStatusChangedEventArgs>();
        manager.FrameReceived += (_, args) => frames.Add(args.Frame);
        manager.StatusChanged += (_, args) => statuses.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        frames.Clear();
        statuses.Clear();

        first.Emit(Frame("first", "FRONT", 3, Point("first", "FRONT", 3, InteractionPhase.Move)));
        first.EmitStatus(ProviderRuntimeStatus.Stopped, ProviderRuntimeStatus.Running);

        Assert.Empty(frames);
        Assert.Empty(statuses);
    }

    [Fact]
    public async Task SwitchAsync_AlreadyCapturedOldStatusCallbackCannotCrossInstanceBoundary()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        await using var manager = CreateManager(first, second);
        var statuses = new List<ProviderManagerStatusChangedEventArgs>();
        manager.StatusChanged += (_, args) => statuses.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        var staleCallback = first.CaptureStatusCallback(
            ProviderRuntimeStatus.Running,
            ProviderRuntimeStatus.Faulted);

        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        statuses.Clear();
        staleCallback();

        Assert.Empty(statuses);
    }

    [Fact]
    public async Task ProviderFrame_WithMismatchedIdentityIsRejectedWithoutChangingActivePoints()
    {
        var provider = new TestProvider("first");
        await using var manager = CreateManager(provider);
        var forwarded = new List<InteractionFrame>();
        var rejected = new List<ProviderFrameRejectedEventArgs>();
        manager.FrameReceived += (_, args) => forwarded.Add(args.Frame);
        manager.FrameRejected += (_, args) => rejected.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        provider.Emit(Frame("wrong", "FRONT", 1, Point("wrong", "FRONT", 4, InteractionPhase.Down)));
        provider.Emit(Frame("first", "FRONT", 2, Point("first", "LEFT", 5, InteractionPhase.Down)));
        await manager.StopAsync(CancellationToken.None);

        Assert.Empty(forwarded);
        Assert.Equal(2, rejected.Count);
        Assert.All(rejected, item => Assert.Equal(ProviderFrameRejectionReason.IdentityMismatch, item.Reason));
    }

    [Fact]
    public async Task ProviderFrame_EmittedBeforeStartCompletesIsRejectedAsInvalidLifecycle()
    {
        var provider = new TestProvider("first");
        provider.DuringInitialize = () =>
            provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down)));
        await using var manager = CreateManager(provider);
        var rejected = new List<ProviderFrameRejectedEventArgs>();
        manager.FrameRejected += (_, args) => rejected.Add(args);

        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        var item = Assert.Single(rejected);
        Assert.Equal(ProviderFrameRejectionReason.InvalidLifecycle, item.Reason);
    }

    [Fact]
    public async Task SwitchAsync_InitializationFailureStopsDisposesAndRemovesFailedProvider()
    {
        var log = new List<string>();
        var provider = new TestProvider("first", log) { InitializeError = new InvalidOperationException("init") };
        await using var manager = CreateManager(provider);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("first", InitializationContext, CancellationToken.None));

        Assert.Equal("init", error.Message);
        Assert.Equal(["first.initialize", "first.stop", "first.dispose"], log);
        Assert.Null(manager.ActiveProvider);
        Assert.Empty(manager.Providers);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task SwitchAsync_StartFailureStopsDisposesAndLeavesNoActiveProvider()
    {
        var log = new List<string>();
        var provider = new TestProvider("first", log) { StartError = new InvalidOperationException("start") };
        await using var manager = CreateManager(provider);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("first", InitializationContext, CancellationToken.None));

        Assert.Equal("start", error.Message);
        Assert.Equal(["first.initialize", "first.start", "first.stop", "first.dispose"], log);
        Assert.Null(manager.ActiveProvider);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task SwitchAsync_ReplacementFailurePublishesDeterministicInactiveRollback()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second") { StartError = new InvalidOperationException("start") };
        await using var manager = CreateManager(first, second);
        var changes = new List<string>();
        manager.ProviderChanged += (_, args) => changes.Add(
            $"{args.Previous?.ProviderInstanceId ?? "none"}->{args.Current?.ProviderInstanceId ?? "none"}");
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("second", InitializationContext, CancellationToken.None));

        Assert.Equal(["none->first", "first->none"], changes);
        Assert.Null(manager.ActiveProvider);
    }

    [Fact]
    public async Task LifecycleOperations_ReceiveTheCallersCancellationToken()
    {
        var provider = new TestProvider("first");
        await using var manager = CreateManager(provider);
        using var switchSource = new CancellationTokenSource();
        using var stopSource = new CancellationTokenSource();

        await manager.SwitchAsync("first", InitializationContext, switchSource.Token);
        await manager.StopAsync(stopSource.Token);

        Assert.Equal(switchSource.Token, provider.InitializeTokens.Single());
        Assert.Equal(switchSource.Token, provider.StartTokens.Single());
        Assert.Equal(stopSource.Token, provider.StopTokens.Single());
    }

    [Fact]
    public async Task StopAsync_CancellationKeepsCurrentProviderAndPropagatesCancellation()
    {
        var provider = new TestProvider("first") { StopError = new OperationCanceledException() };
        await using var manager = CreateManager(provider);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.StopAsync(CancellationToken.None));

        Assert.Equal("first", manager.ActiveProvider?.ProviderInstanceId);
        provider.StopError = null;
    }

    [Fact]
    public async Task StopAsync_CancelsTrackedPointsStopsProviderAndPublishesInactiveChange()
    {
        var log = new List<string>();
        var provider = new TestProvider("first", log);
        await using var manager = CreateManager(provider);
        manager.FrameReceived += (_, args) =>
        {
            if (args.Frame.Points.Any(point => point.Phase == InteractionPhase.Cancel))
            {
                log.Add("cancel");
            }
        };
        manager.ProviderChanged += (_, args) => log.Add($"changed:{args.Current?.ProviderInstanceId ?? "none"}");
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        provider.Emit(Frame("first", "FRONT", 9, Point("first", "FRONT", 1, InteractionPhase.Down)));
        log.Clear();

        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(["cancel", "first.stop", "changed:none"], log);
        Assert.Null(manager.ActiveProvider);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentStopsActiveAndDisposesEveryRegisteredProviderOnce()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        var manager = CreateManager(first, second);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.Equal(1, first.StopCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => manager.SwitchAsync("second", InitializationContext, CancellationToken.None));
    }

    private static ProviderManager CreateManager(params TestProvider[] providers)
    {
        var manager = new ProviderManager();
        foreach (var provider in providers)
        {
            manager.Register(Descriptor(), provider);
        }

        return manager;
    }

    private static ProviderDescriptor Descriptor() => new(
        "blaze.test",
        "Test provider",
        new Version(1, 0, 0),
        "Test",
        ["interaction-point"]);

    private static InteractionSurface Surface(string surfaceId) => new()
    {
        SurfaceId = surfaceId,
        Name = surfaceId,
        LogicalWidth = 1920,
        LogicalHeight = 1080,
        IsPrimary = surfaceId == "FRONT",
        Order = surfaceId == "FRONT" ? 0 : 1
    };

    private static InteractionFrame Frame(
        string providerInstanceId,
        string surfaceId,
        long sequence,
        params InteractionPoint[] points) => new()
        {
            ProviderId = "blaze.test",
            ProviderInstanceId = providerInstanceId,
            SurfaceId = surfaceId,
            Sequence = sequence,
            TimestampUnixMs = 1_700_000_000_000 + sequence,
            Points = points
        };

    private static InteractionPoint Point(
        string providerInstanceId,
        string surfaceId,
        long id,
        InteractionPhase phase) => new()
        {
            Id = id,
            SurfaceId = surfaceId,
            ProviderId = "blaze.test",
            ProviderInstanceId = providerInstanceId,
            SourceId = "source-1",
            Phase = phase,
            NormalizedPosition = new Vector2Data(0.25f, 0.75f),
            PixelPosition = new Vector2Data(480f, 810f),
            Confidence = 0.9f,
            TimestampUnixMs = 1_700_000_000_000 + id
        };

    private sealed class TestProvider : IInteractionProvider
    {
        private readonly List<string>? _log;

        internal TestProvider(string providerInstanceId, List<string>? log = null)
        {
            ProviderInstanceId = providerInstanceId;
            _log = log;
        }

        public string ProviderInstanceId { get; }
        public ProviderRuntimeStatus Status { get; private set; } = ProviderRuntimeStatus.Created;
        public Exception? InitializeError { get; set; }
        public Exception? StartError { get; set; }
        public Exception? StopError { get; set; }
        public Action? DuringInitialize { get; set; }
        public Func<CancellationToken, Task>? BeforeStartAsync { get; set; }
        public Action? OnStarted { get; set; }
        public Action? OnStopped { get; set; }
        public int InitializeCount { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ConcurrentQueue<CancellationToken> InitializeTokens { get; } = new();
        public ConcurrentQueue<CancellationToken> StartTokens { get; } = new();
        public ConcurrentQueue<CancellationToken> StopTokens { get; } = new();

        public event EventHandler<InteractionFrameEventArgs>? FrameReceived;
        public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged;

        public Task InitializeAsync(ProviderInitializationContext context, CancellationToken cancellationToken)
        {
            InitializeTokens.Enqueue(cancellationToken);
            InitializeCount++;
            _log?.Add($"{ProviderInstanceId}.initialize");
            Status = ProviderRuntimeStatus.Initializing;
            DuringInitialize?.Invoke();
            if (InitializeError is not null)
            {
                throw InitializeError;
            }

            Status = ProviderRuntimeStatus.Ready;
            return Task.CompletedTask;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            StartTokens.Enqueue(cancellationToken);
            StartCount++;
            _log?.Add($"{ProviderInstanceId}.start");
            Status = ProviderRuntimeStatus.Starting;
            if (BeforeStartAsync is not null)
            {
                await BeforeStartAsync(cancellationToken);
            }

            if (StartError is not null)
            {
                throw StartError;
            }

            Status = ProviderRuntimeStatus.Running;
            OnStarted?.Invoke();
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopTokens.Enqueue(cancellationToken);
            StopCount++;
            _log?.Add($"{ProviderInstanceId}.stop");
            Status = ProviderRuntimeStatus.Stopping;
            if (StopError is not null)
            {
                throw StopError;
            }

            Status = ProviderRuntimeStatus.Stopped;
            OnStopped?.Invoke();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _log?.Add($"{ProviderInstanceId}.dispose");
            return ValueTask.CompletedTask;
        }

        internal void Emit(InteractionFrame frame) => FrameReceived?.Invoke(this, new InteractionFrameEventArgs(frame));

        internal void EmitStatus(ProviderRuntimeStatus previous, ProviderRuntimeStatus current, Exception? error = null)
        {
            Status = current;
            StatusChanged?.Invoke(this, new ProviderStatusChangedEventArgs(previous, current, error));
        }

        internal Action CaptureStatusCallback(ProviderRuntimeStatus previous, ProviderRuntimeStatus current)
        {
            var handlers = StatusChanged;
            return () => handlers?.Invoke(this, new ProviderStatusChangedEventArgs(previous, current));
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        internal static EmptyServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
