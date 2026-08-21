using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;

namespace Blaze.Provider.Radar.Tests;

public sealed class RadarInteractionProviderTests
{
    [Fact]
    public void PluginDescriptorMatchesThePublishedRadarIdentityAndCapabilities()
    {
        var descriptor = new RadarPlugin().Descriptor;

        Assert.Equal("blaze.radar.f10f20", descriptor.Id);
        Assert.Equal("F10 / F20 激光雷达", descriptor.DisplayName);
        Assert.Equal(new Version(1, 0, 0), descriptor.Version);
        Assert.Equal("Radar", descriptor.Category);
        Assert.Equal(
            ["interaction-point", "preview", "multi-sensor", "calibration", "multi-surface"],
            descriptor.Capabilities);
    }

    [Fact]
    public void PluginCreatesAProviderWithTheStableRadarMainInstanceIdentity()
    {
        using var providerDirectory = new TemporaryDirectory();
        var provider = new RadarPlugin().CreateProvider(
            new ProviderCreateContext(providerDirectory.Path, EmptyServiceProvider.Instance));

        Assert.IsType<RadarInteractionProvider>(provider);
        Assert.Equal("radar-main", provider.ProviderInstanceId);
        Assert.Equal(ProviderRuntimeStatus.Created, provider.Status);
    }

    [Fact]
    public async Task LifecycleTransitionsInOrderAndStopsTheExistingRadarRuntimeExactlyOnce()
    {
        var runtime = new FakeRadarProviderRuntime();
        var provider = CreateProvider(runtime);
        var transitions = new List<(ProviderRuntimeStatus Previous, ProviderRuntimeStatus Current)>();
        provider.StatusChanged += (_, change) => transitions.Add((change.PreviousStatus, change.Status));
        var context = InitializationContext(Surface("front", true, 0));

        await provider.InitializeAsync(context, CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);
        await provider.StopAsync(CancellationToken.None);
        await provider.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();
        await provider.DisposeAsync();

        Assert.Equal(1, runtime.StartCallCount);
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
        Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
        Assert.Equal(
        [
            (ProviderRuntimeStatus.Created, ProviderRuntimeStatus.Initializing),
            (ProviderRuntimeStatus.Initializing, ProviderRuntimeStatus.Ready),
            (ProviderRuntimeStatus.Ready, ProviderRuntimeStatus.Starting),
            (ProviderRuntimeStatus.Starting, ProviderRuntimeStatus.Running),
            (ProviderRuntimeStatus.Running, ProviderRuntimeStatus.Stopping),
            (ProviderRuntimeStatus.Stopping, ProviderRuntimeStatus.Stopped)
        ], transitions);
    }

    [Fact]
    public async Task InitializeProductionRuntimeAppliesInteractionSurfacesToTheExistingRadarCoordinator()
    {
        using var providerDirectory = new TemporaryDirectory();
        var configuration = new RadarAppConfiguration { Screens = [] };
        var services = new DictionaryServiceProvider(new Dictionary<Type, object>
        {
            [typeof(RadarAppConfiguration)] = configuration,
            [typeof(IRadarSensorPipelineFactory)] = new RadarSensorPipelineFactory(NullLoggerFactory.Instance)
        });
        var provider = new RadarPlugin().CreateProvider(new ProviderCreateContext(providerDirectory.Path, services));

        await provider.InitializeAsync(
            InitializationContext(
                Surface("left", false, 0, 1600, 900),
                Surface("front", true, 1, 4096, 1536)),
            CancellationToken.None);

        Assert.Equal(ProviderRuntimeStatus.Ready, provider.Status);
        Assert.Equal(["left", "front"], configuration.Screens.OrderBy(screen => screen.UnityOrder).Select(screen => screen.ScreenId));
        var front = configuration.Screens.Single(screen => screen.ScreenId == "front");
        Assert.True(front.IsAssociated);
        Assert.True(front.IsPrimary);
        Assert.Equal(4096, front.UnityDefaultWidthPixels);
        Assert.Equal(1536, front.UnityDefaultHeightPixels);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ExistingPointerBatchOutputIsPublishedOncePerSurfaceAndSubscriberFailuresAreIsolated()
    {
        var runtime = new FakeRadarProviderRuntime();
        var provider = CreateProvider(runtime);
        var received = new List<InteractionFrame>();
        provider.FrameReceived += (_, _) => throw new InvalidOperationException("consumer fault");
        provider.FrameReceived += (_, frame) => received.Add(frame.Frame);
        provider.StatusChanged += (_, _) => throw new InvalidOperationException("status consumer fault");
        await provider.InitializeAsync(InitializationContext(Surface("left", false, 0), Surface("front", true, 1)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);

        var delivered = await runtime.PublishAsync(new PointerBatchPayload(
        [
            Frame("left", 10, RadarPointerPhase.Hover),
            Frame("front", 10, RadarPointerPhase.Down)
        ]));

        Assert.True(delivered);
        Assert.Equal(["left", "front"], received.Select(frame => frame.SurfaceId));
        Assert.Equal(ProviderRuntimeStatus.Running, provider.Status);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task StartFailureCleansUpRadarRuntimeAndMovesProviderToFaulted()
    {
        var runtime = new FakeRadarProviderRuntime { StartException = new IOException("start failed") };
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<IOException>(() => provider.StartAsync(CancellationToken.None));

        Assert.Equal("start failed", exception.Message);
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
        await provider.DisposeAsync();
        Assert.Equal(1, runtime.DisposeCallCount);
    }

    [Fact]
    public async Task StartAndCleanupFailuresArePreservedInOrderAndSharedByLaterLifecycleCalls()
    {
        var startFailure = new IOException("start failed");
        var stopFailure = new InvalidOperationException("cleanup stop failed");
        var disposeFailure = new ApplicationException("cleanup dispose failed");
        var runtime = new FakeRadarProviderRuntime
        {
            StartException = startFailure,
            StopException = stopFailure,
            DisposeException = disposeFailure
        };
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);

        var startException = await Record.ExceptionAsync(() => provider.StartAsync(CancellationToken.None));
        var aggregate = Assert.IsType<AggregateException>(startException);
        Assert.Equal([startFailure, stopFailure, disposeFailure], aggregate.InnerExceptions);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);

        var stopException = await Record.ExceptionAsync(() => provider.StopAsync(CancellationToken.None));
        var disposeException = await Record.ExceptionAsync(() => provider.DisposeAsync().AsTask());

        Assert.Same(startException, stopException);
        Assert.Same(startException, disposeException);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
    }

    [Fact]
    public async Task StartCancellationAndCleanupFailuresArePreservedInOrderAndSharedByLaterLifecycleCalls()
    {
        using var cancellation = new CancellationTokenSource();
        var cancellationFailure = new OperationCanceledException(cancellation.Token);
        var stopFailure = new InvalidOperationException("cleanup stop failed");
        var disposeFailure = new ApplicationException("cleanup dispose failed");
        var runtime = new FakeRadarProviderRuntime
        {
            BeforeStart = cancellation.Cancel,
            StartException = cancellationFailure,
            StopException = stopFailure,
            DisposeException = disposeFailure
        };
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);

        var startException = await Record.ExceptionAsync(() => provider.StartAsync(cancellation.Token));
        var aggregate = Assert.IsType<AggregateException>(startException);
        Assert.Equal([cancellationFailure, stopFailure, disposeFailure], aggregate.InnerExceptions);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);

        var stopException = await Record.ExceptionAsync(() => provider.StopAsync(CancellationToken.None));
        var disposeException = await Record.ExceptionAsync(() => provider.DisposeAsync().AsTask());

        Assert.Same(startException, stopException);
        Assert.Same(startException, disposeException);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
    }

    [Fact]
    public async Task StopAndDisposeFailuresArePreservedInOrderAndSharedByLaterLifecycleCalls()
    {
        var stopFailure = new IOException("stop failed");
        var disposeFailure = new InvalidOperationException("cleanup dispose failed");
        var runtime = new FakeRadarProviderRuntime
        {
            StopException = stopFailure,
            DisposeException = disposeFailure
        };
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);

        var stopException = await Record.ExceptionAsync(() => provider.StopAsync(CancellationToken.None));
        var aggregate = Assert.IsType<AggregateException>(stopException);
        Assert.Equal([stopFailure, disposeFailure], aggregate.InnerExceptions);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);

        var repeatedStopException = await Record.ExceptionAsync(() => provider.StopAsync(CancellationToken.None));
        var disposeException = await Record.ExceptionAsync(() => provider.DisposeAsync().AsTask());

        Assert.Same(stopException, repeatedStopException);
        Assert.Same(stopException, disposeException);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
    }

    [Fact]
    public async Task InitializeCancellationIsForwardedAndLeavesNoLiveRadarRuntime()
    {
        CancellationToken observed = default;
        var factoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new RadarInteractionProvider(
            "radar-instance-a",
            async (_, _, cancellationToken) =>
            {
                observed = cancellationToken;
                factoryEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new FakeRadarProviderRuntime();
            });
        using var cancellation = new CancellationTokenSource();

        var initializing = provider.InitializeAsync(
            InitializationContext(Surface("front", true, 0)),
            cancellation.Token);
        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initializing);

        Assert.Equal(cancellation.Token, observed);
        Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SimulationAndReplayCommandsDelegateToTheExistingCoordinatorRuntime()
    {
        var runtime = new FakeRadarProviderRuntime();
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        await provider.StartAllSimulationAsync(cancellation.Token);
        await provider.StopAllSimulationAsync();
        await provider.ReplaySensorAsync("front", "f1", "capture.radarrec", 2d, true, cancellation.Token);
        provider.PauseReplay("front", "f1");
        provider.ResumeReplay("front", "f1");
        provider.StepReplay("front", "f1");
        await provider.StopReplayAsync("front", "f1");

        Assert.Equal(cancellation.Token, runtime.SimulationToken);
        Assert.Equal(("front", "f1", "capture.radarrec", 2d, true, cancellation.Token), runtime.ReplayRequest);
        Assert.Equal(["pause", "resume", "step", "stop"], runtime.ReplayOperations);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentDisposeCallsWaitForTheSameSingleCleanup()
    {
        var runtime = new FakeRadarProviderRuntime();
        runtime.BlockStop();
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);

        var first = provider.DisposeAsync().AsTask();
        await runtime.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = provider.DisposeAsync().AsTask();

        Assert.False(second.IsCompleted);
        runtime.ReleaseStop();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
        Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
    }

    [Fact]
    public async Task ConcurrentDisposeCallsObserveTheSameCleanupFailure()
    {
        var cleanupFailure = new IOException("dispose failed");
        var runtime = new FakeRadarProviderRuntime { DisposeException = cleanupFailure };
        runtime.BlockStop();
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);

        var first = provider.DisposeAsync().AsTask();
        await runtime.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = provider.DisposeAsync().AsTask();
        Assert.False(second.IsCompleted);
        runtime.ReleaseStop();

        var firstFailure = await Record.ExceptionAsync(() => first);
        var secondFailure = await Record.ExceptionAsync(() => second);
        Assert.Same(cleanupFailure, firstFailure);
        Assert.Same(firstFailure, secondFailure);
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
    }

    [Fact]
    public async Task ConcurrentDisposeCallsShareBothStopAndDisposeCleanupFailures()
    {
        var stopFailure = new IOException("stop failed");
        var disposeFailure = new InvalidOperationException("dispose failed");
        var runtime = new FakeRadarProviderRuntime
        {
            StopException = stopFailure,
            DisposeException = disposeFailure
        };
        runtime.BlockStop();
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);

        var first = provider.DisposeAsync().AsTask();
        await runtime.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = provider.DisposeAsync().AsTask();
        runtime.ReleaseStop();

        var firstFailure = await Record.ExceptionAsync(() => first);
        var secondFailure = await Record.ExceptionAsync(() => second);
        var aggregate = Assert.IsType<AggregateException>(firstFailure);
        Assert.Equal([stopFailure, disposeFailure], aggregate.InnerExceptions);
        Assert.Same(firstFailure, secondFailure);
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
    }

    [Fact]
    public async Task StopStartedAfterDisposeWaitsForAndSharesItsCleanupFailure()
    {
        var cleanupFailure = new IOException("stop failed");
        var runtime = new FakeRadarProviderRuntime { StopException = cleanupFailure };
        runtime.BlockStop();
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);

        var dispose = provider.DisposeAsync().AsTask();
        await runtime.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stop = provider.StopAsync(CancellationToken.None);

        Assert.False(stop.IsCompleted);
        runtime.ReleaseStop();
        var disposeFailure = await Record.ExceptionAsync(() => dispose);
        var stopFailure = await Record.ExceptionAsync(() => stop);
        Assert.Same(cleanupFailure, disposeFailure);
        Assert.Same(disposeFailure, stopFailure);
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
    }

    [Fact]
    public async Task SynchronousStatusHandlerDisposeReentryFailsFastWithoutPoisoningLaterContinuations()
    {
        var runtime = new FakeRadarProviderRuntime();
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);
        var continueAfterHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reentryFailure = null;
        Task? continuation = null;
        provider.StatusChanged += (_, change) =>
        {
            if (change.Status != ProviderRuntimeStatus.Stopping) return;
#pragma warning disable xUnit1031 // This regression test intentionally exercises synchronous event-handler reentry.
            reentryFailure = Record.Exception(() => provider.DisposeAsync().GetAwaiter().GetResult());
#pragma warning restore xUnit1031
            continuation = Task.Run(async () =>
            {
                await continueAfterHandler.Task;
                await provider.DisposeAsync();
                await provider.StopAsync(CancellationToken.None);
            });
        };

        await provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        var invalidOperation = Assert.IsType<InvalidOperationException>(reentryFailure);
        Assert.Contains("StatusChanged", invalidOperation.Message, StringComparison.Ordinal);
        continueAfterHandler.SetResult();
        Assert.NotNull(continuation);
        await continuation.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
    }

    [Fact]
    public async Task SynchronousInitializingStatusHandlerInitializeReentryFailsFastWithoutDeadlocking()
    {
        var runtime = new FakeRadarProviderRuntime();
        var provider = CreateProvider(runtime);
        var context = InitializationContext(Surface("front", true, 0));
        Exception? reentryFailure = null;
        provider.StatusChanged += (_, change) =>
        {
            if (change.Status != ProviderRuntimeStatus.Initializing) return;
#pragma warning disable xUnit1031 // This regression test intentionally exercises synchronous event-handler reentry.
            reentryFailure = Record.Exception(() => provider.InitializeAsync(context, CancellationToken.None).GetAwaiter().GetResult());
#pragma warning restore xUnit1031
        };

        await Task.Run(() => provider.InitializeAsync(context, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(1));

        var invalidOperation = Assert.IsType<InvalidOperationException>(reentryFailure);
        Assert.Contains("StatusChanged", invalidOperation.Message, StringComparison.Ordinal);
        Assert.Equal(ProviderRuntimeStatus.Ready, provider.Status);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task SynchronousStartingStatusHandlerStartReentryFailsFastWithoutDeadlocking()
    {
        var runtime = new FakeRadarProviderRuntime();
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        Exception? reentryFailure = null;
        provider.StatusChanged += (_, change) =>
        {
            if (change.Status != ProviderRuntimeStatus.Starting) return;
#pragma warning disable xUnit1031 // This regression test intentionally exercises synchronous event-handler reentry.
            reentryFailure = Record.Exception(() => provider.StartAsync(CancellationToken.None).GetAwaiter().GetResult());
#pragma warning restore xUnit1031
        };

        await Task.Run(() => provider.StartAsync(CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(1));

        var invalidOperation = Assert.IsType<InvalidOperationException>(reentryFailure);
        Assert.Contains("StatusChanged", invalidOperation.Message, StringComparison.Ordinal);
        Assert.Equal(ProviderRuntimeStatus.Running, provider.Status);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task StopRacingDisposeCompletesOneStopAndOneDisposeWithoutSemaphoreTeardownFaults()
    {
        var runtime = new FakeRadarProviderRuntime();
        runtime.BlockStop();
        var provider = CreateProvider(runtime);
        await provider.InitializeAsync(InitializationContext(Surface("front", true, 0)), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);

        var stop = provider.StopAsync(CancellationToken.None);
        await runtime.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var dispose = provider.DisposeAsync().AsTask();
        runtime.ReleaseStop();

        await Task.WhenAll(stop, dispose).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, runtime.StopCallCount);
        Assert.Equal(1, runtime.DisposeCallCount);
        Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
    }

    private static RadarInteractionProvider CreateProvider(FakeRadarProviderRuntime runtime) => new(
        "radar-instance-a",
        (_, publish, _) =>
        {
            runtime.Publish = publish;
            return Task.FromResult<IRadarProviderRuntime>(runtime);
        });

    private static ProviderInitializationContext InitializationContext(params InteractionSurface[] surfaces) =>
        new(surfaces, EmptyServiceProvider.Instance);

    private static InteractionSurface Surface(string id, bool primary, int order, int width = 1920, int height = 1080) => new()
    {
        SurfaceId = id,
        Name = id,
        LogicalWidth = width,
        LogicalHeight = height,
        IsPrimary = primary,
        Order = order
    };

    private static RadarScreenPointerFrame Frame(string surfaceId, long sequence, RadarPointerPhase phase) => new(
        new RadarScreenInfo(surfaceId, surfaceId, 1920, 1080, surfaceId == "front", surfaceId == "front" ? 1 : 0),
        sequence,
        1_000 + sequence,
        [new RadarScreenPointer(1, phase, .5f, .5f, 960f, 540f, 1f, 999 + sequence)]);

    private sealed class FakeRadarProviderRuntime : IRadarProviderRuntime
    {
        public Func<PointerBatchPayload, CancellationToken, Task<bool>> Publish { get; set; } = null!;
        public Action? BeforeStart { get; init; }
        public Exception? StartException { get; init; }
        public Exception? StopException { get; init; }
        public Exception? DisposeException { get; init; }
        public int StartCallCount { get; private set; }
        public int StopCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken SimulationToken { get; private set; }
        public (string ScreenId, string SensorId, string Path, double Speed, bool Loop, CancellationToken Token) ReplayRequest { get; private set; }
        public List<string> ReplayOperations { get; } = [];
        private TaskCompletionSource? _stopRelease;

        public Task<bool> PublishAsync(PointerBatchPayload batch) => Publish(batch, CancellationToken.None);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCallCount++;
            BeforeStart?.Invoke();
            return StartException is null ? Task.CompletedTask : Task.FromException(StartException);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopCallCount++;
            StopEntered.TrySetResult();
            if (_stopRelease is not null)
                await _stopRelease.Task.WaitAsync(cancellationToken);
            if (StopException is not null)
                throw StopException;
        }

        public void BlockStop() => _stopRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseStop() => _stopRelease?.TrySetResult();

        public Task StartAllSimulationAsync(CancellationToken cancellationToken)
        {
            SimulationToken = cancellationToken;
            return Task.CompletedTask;
        }

        public Task StopAllSimulationAsync() => Task.CompletedTask;

        public Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken)
        {
            ReplayRequest = (screenId, sensorId, path, speed, loop, cancellationToken);
            return Task.CompletedTask;
        }

        public void PauseReplay(string screenId, string sensorId) => ReplayOperations.Add("pause");
        public void ResumeReplay(string screenId, string sensorId) => ReplayOperations.Add("resume");
        public void StepReplay(string screenId, string sensorId) => ReplayOperations.Add("step");

        public Task StopReplayAsync(string screenId, string sensorId)
        {
            ReplayOperations.Add("stop");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class DictionaryServiceProvider(IReadOnlyDictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.TryGetValue(serviceType, out var service) ? service : null;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Blaze.Provider.Radar.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
