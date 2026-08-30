using System.Collections.Concurrent;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Runtime.Tests;

#pragma warning disable xUnit1031 // These tests intentionally exercise synchronous lifecycle reentry.

public sealed class ProviderManagerTests
{
    private static readonly ProviderInitializationContext InitializationContext = new(
        [Surface("FRONT"), Surface("LEFT")],
        EmptyServices.Instance);

    [Fact]
    public async Task FrameReceived_SynchronousStopReentryFailsFastWithoutDeadlockAndExternalSwitchStillWorks()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        var manager = CreateManager(first, second);
        var diagnostics = new List<ProviderManagerDiagnosticEventArgs>();
        manager.Diagnostic += (_, args) => diagnostics.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        manager.FrameReceived += (_, _) =>
            manager.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

        var emission = Task.Run(
            () => first.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down))));
        var completed = await CompletesWithinAsync(emission);

        Assert.True(completed, "FrameReceived lifecycle reentry deadlocked.");
        Assert.Contains(diagnostics, item => item.Exception is InvalidOperationException);
        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        Assert.Equal("second", manager.ActiveProvider?.ProviderInstanceId);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task StatusChanged_SynchronousSwitchReentryFailsFastWithoutDeadlock()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        var manager = CreateManager(first, second);
        var diagnostics = new List<ProviderManagerDiagnosticEventArgs>();
        manager.Diagnostic += (_, args) => diagnostics.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        manager.StatusChanged += (_, _) =>
            manager.SwitchAsync("second", InitializationContext, CancellationToken.None).GetAwaiter().GetResult();

        var emission = Task.Run(
            () => first.EmitStatus(ProviderRuntimeStatus.Running, ProviderRuntimeStatus.Running));
        var completed = await CompletesWithinAsync(emission);

        Assert.True(completed, "StatusChanged lifecycle reentry deadlocked.");
        Assert.Contains(diagnostics, item => item.Exception is InvalidOperationException);
        Assert.Equal("first", manager.ActiveProvider?.ProviderInstanceId);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task ProviderChanged_SynchronousSwitchReentryFailsFastWithoutDeadlock()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        var manager = CreateManager(first, second);
        var diagnostics = new List<ProviderManagerDiagnosticEventArgs>();
        manager.Diagnostic += (_, args) => diagnostics.Add(args);
        manager.ProviderChanged += (_, _) =>
            manager.SwitchAsync("second", InitializationContext, CancellationToken.None).GetAwaiter().GetResult();

        var switching = Task.Run(
            () => manager.SwitchAsync("first", InitializationContext, CancellationToken.None).GetAwaiter().GetResult());
        var completed = await CompletesWithinAsync(switching);

        Assert.True(completed, "ProviderChanged lifecycle reentry deadlocked.");
        Assert.Contains(diagnostics, item => item.Exception is InvalidOperationException);
        Assert.Equal("first", manager.ActiveProvider?.ProviderInstanceId);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task Diagnostic_SynchronousDisposeReentryFailsFastAndLaterDiagnosticSubscriberRuns()
    {
        var provider = new TestProvider("first");
        var manager = CreateManager(provider);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        Exception? reentry = null;
        var laterSubscriberCalls = 0;
        manager.Diagnostic += (_, _) =>
        {
            try
            {
                manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                reentry = exception;
            }
        };
        manager.Diagnostic += (_, _) => laterSubscriberCalls++;
        manager.FrameReceived += (_, _) => throw new InvalidOperationException("consumer");

        var emission = Task.Run(
            () => provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down))));
        var completed = await CompletesWithinAsync(emission);

        Assert.True(completed, "Diagnostic lifecycle reentry deadlocked.");
        Assert.IsType<InvalidOperationException>(reentry);
        Assert.Equal(1, laterSubscriberCalls);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task FrameReceived_AsyncContinuationMayStopAfterHandlerAndEmitReturn()
    {
        var provider = new TestProvider("first");
        var manager = CreateManager(provider);
        var releaseContinuation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var emitHasReturned = false;
        Task<bool>? continuation = null;
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        manager.FrameReceived += (_, _) =>
            continuation = StopAfterReleaseAsync(releaseContinuation.Task, manager, () => emitHasReturned);

        provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down)));
        emitHasReturned = true;
        Assert.NotNull(continuation);
        Assert.False(continuation.IsCompleted);

        releaseContinuation.SetResult();
        var observedEmitReturn = await continuation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(observedEmitReturn);
        Assert.Null(manager.ActiveProvider);
        Assert.Equal(1, provider.StopCount);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task ProviderFrame_NestedEmitIsDeferredUntilCurrentFrameReachesEverySubscriber()
    {
        var provider = new TestProvider("first");
        await using var manager = CreateManager(provider);
        var firstConsumer = new List<InteractionPhase>();
        var secondConsumer = new List<InteractionPhase>();
        var callbackDepth = 0;
        var maximumCallbackDepth = 0;
        manager.FrameReceived += (_, args) =>
        {
            callbackDepth++;
            maximumCallbackDepth = Math.Max(maximumCallbackDepth, callbackDepth);
            try
            {
                firstConsumer.Add(args.Frame.Points.Single().Phase);
                if (args.Frame.Sequence == 1)
                {
                    provider.Emit(
                        Frame("first", "FRONT", 2, Point("first", "FRONT", 7, InteractionPhase.Up)));
                }
            }
            finally
            {
                callbackDepth--;
            }
        };
        manager.FrameReceived += (_, args) =>
            secondConsumer.Add(args.Frame.Points.Single().Phase);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 7, InteractionPhase.Down)));
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal(1, maximumCallbackDepth);
        Assert.Equal([InteractionPhase.Down, InteractionPhase.Up], firstConsumer);
        Assert.Equal([InteractionPhase.Down, InteractionPhase.Up], secondConsumer);
    }

    [Fact]
    public async Task ProviderFrame_NestedBurstBeyondCapacityIsRejectedWithoutUnboundedBuffering()
    {
        var provider = new TestProvider("first");
        await using var manager = CreateManager(provider);
        var publishedCount = 0;
        var rejections = new List<ProviderFrameRejectedEventArgs>();
        var diagnostics = new List<ProviderManagerDiagnosticEventArgs>();
        manager.FrameReceived += (_, args) =>
        {
            if (args.Frame.Sequence == 1)
            {
                for (var sequence = 2; sequence <= 130; sequence++)
                {
                    provider.Emit(
                        Frame("first", "FRONT", sequence, Point("first", "FRONT", 7, InteractionPhase.Move)));
                }
            }
        };
        manager.FrameReceived += (_, _) => publishedCount++;
        manager.FrameRejected += (_, args) => rejections.Add(args);
        manager.Diagnostic += (_, args) => diagnostics.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 7, InteractionPhase.Down)));

        Assert.True(publishedCount < 130);
        Assert.Contains(rejections, item => item.Message.Contains("capacity", StringComparison.OrdinalIgnoreCase));
        Assert.All(rejections, item => Assert.Equal(ProviderFrameRejectionReason.CapacityExceeded, item.Reason));
        Assert.Contains(diagnostics, item => item.Operation == "FrameQueueCapacityExceeded");
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProviderFrame_NonIncreasingSequenceIsRejectedWithoutReactivatingEndedPoint()
    {
        var provider = new TestProvider("first");
        await using var manager = CreateManager(provider);
        var frames = new List<InteractionFrame>();
        var rejections = new List<ProviderFrameRejectedEventArgs>();
        manager.FrameReceived += (_, args) => frames.Add(args.Frame);
        manager.FrameRejected += (_, args) => rejections.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 7, InteractionPhase.Down)));
        provider.Emit(Frame("first", "FRONT", 2, Point("first", "FRONT", 7, InteractionPhase.Up)));
        provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 7, InteractionPhase.Down)));
        await manager.StopAsync(CancellationToken.None);

        Assert.Equal([1L, 2L], frames.Select(frame => frame.Sequence));
        Assert.Equal(
            [InteractionPhase.Down, InteractionPhase.Up],
            frames.SelectMany(frame => frame.Points).Select(point => point.Phase));
        var rejection = Assert.Single(rejections);
        Assert.Equal(ProviderFrameRejectionReason.NonIncreasingSequence, rejection.Reason);
    }

    [Fact]
    public async Task ProviderFrame_ConcurrentOutOfOrderArrivalSerializesRegistryAndPublicationMonotonically()
    {
        var provider = new TestProvider("first");
        await using var manager = CreateManager(provider);
        var enteredSequenceTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseSequenceTwo = new ManualResetEventSlim();
        var published = new List<long>();
        var rejections = new List<ProviderFrameRejectedEventArgs>();
        manager.FrameReceived += (_, args) =>
        {
            if (args.Frame.Sequence == 2)
            {
                enteredSequenceTwo.TrySetResult();
                Assert.True(releaseSequenceTwo.Wait(TimeSpan.FromSeconds(5)));
            }
        };
        manager.FrameReceived += (_, args) => published.Add(args.Frame.Sequence);
        manager.FrameRejected += (_, args) => rejections.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        var newer = Task.Run(
            () => provider.Emit(Frame("first", "FRONT", 2, Point("first", "FRONT", 7, InteractionPhase.Down))));
        await enteredSequenceTwo.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var older = Task.Run(
            () => provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 7, InteractionPhase.Down))));
        await Task.Delay(100);
        var olderCompletedBeforeRelease = older.IsCompleted;
        releaseSequenceTwo.Set();
        await Task.WhenAll(newer, older).WaitAsync(TimeSpan.FromSeconds(5));
        await manager.StopAsync(CancellationToken.None);

        Assert.False(olderCompletedBeforeRelease);
        Assert.Equal([2L, 3L], published);
        Assert.All(published.Zip(published.Skip(1)), pair => Assert.True(pair.First < pair.Second));
        var rejection = Assert.Single(rejections);
        Assert.Equal(ProviderFrameRejectionReason.NonIncreasingSequence, rejection.Reason);
    }

    [Fact]
    public async Task SwitchAsync_WaitsForAdmittedFrameCallbackBeforeCancelAndStop()
    {
        var timeline = new List<string>();
        var provider = new TestProvider("first", timeline);
        var replacement = new TestProvider("second", timeline);
        await using var manager = CreateManager(provider, replacement);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var callbackFinished = false;
        manager.FrameReceived += (_, args) =>
        {
            if (args.Frame.Points.All(point => point.Phase != InteractionPhase.Cancel))
            {
                timeline.Add("callback-start");
                entered.TrySetResult();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                callbackFinished = true;
                timeline.Add("callback-end");
            }
            else
            {
                timeline.Add("cancel");
            }
        };
        manager.ProviderChanged += (_, _) => timeline.Add("changed");
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        timeline.Clear();
        var emission = Task.Run(
            () => provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down))));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var switching = manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        await Task.Delay(100);
        var completedEarly = switching.IsCompleted;
        var stopCountBeforeRelease = provider.StopCount;
        release.Set();
        await Task.WhenAll(emission, switching).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(completedEarly);
        Assert.Equal(0, stopCountBeforeRelease);
        Assert.True(callbackFinished);
        Assert.Equal(
            [
                "callback-start", "callback-end", "cancel", "first.stop", "first.dispose",
                "second.initialize", "second.start", "changed"
            ],
            timeline);
    }

    [Fact]
    public async Task SwitchAsync_WaitsForAdmittedStatusCallbackBeforeDeactivation()
    {
        var timeline = new List<string>();
        var provider = new TestProvider("first", timeline);
        var replacement = new TestProvider("second", timeline);
        await using var manager = CreateManager(provider, replacement);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        manager.StatusChanged += (_, _) =>
        {
            timeline.Add("status-start");
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            timeline.Add("status-end");
        };
        manager.ProviderChanged += (_, _) => timeline.Add("changed");
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        timeline.Clear();
        var emission = Task.Run(
            () => provider.EmitStatus(ProviderRuntimeStatus.Running, ProviderRuntimeStatus.Running));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var switching = manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        await Task.Delay(100);
        var completedEarly = switching.IsCompleted;
        var stopCountBeforeRelease = provider.StopCount;
        release.Set();
        await Task.WhenAll(emission, switching).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(completedEarly);
        Assert.Equal(0, stopCountBeforeRelease);
        Assert.Equal(
            [
                "status-start", "status-end", "first.stop", "first.dispose",
                "second.initialize", "second.start", "changed"
            ],
            timeline);
    }

    [Fact]
    public async Task SwitchAsync_EventSubscriberFailureIsDiagnosedWithoutBreakingCancelOrLifecycle()
    {
        var log = new List<string>();
        var running = 0;
        var maximumRunning = 0;
        var provider = new TestProvider("first", log)
        {
            OnStarted = () => maximumRunning = Math.Max(maximumRunning, Interlocked.Increment(ref running)),
            OnStopped = () => Interlocked.Decrement(ref running)
        };
        var replacement = new TestProvider("second", log)
        {
            OnStarted = () => maximumRunning = Math.Max(maximumRunning, Interlocked.Increment(ref running)),
            OnStopped = () => Interlocked.Decrement(ref running)
        };
        await using var manager = CreateManager(provider, replacement);
        var diagnostics = new List<ProviderManagerDiagnosticEventArgs>();
        manager.Diagnostic += (_, args) => diagnostics.Add(args);
        manager.FrameReceived += (_, args) =>
        {
            if (args.Frame.Points.Any(point => point.Phase == InteractionPhase.Cancel))
            {
                throw new InvalidOperationException("consumer");
            }
        };
        manager.FrameReceived += (_, args) =>
        {
            if (args.Frame.Points.Any(point => point.Phase == InteractionPhase.Cancel))
            {
                log.Add("cancel-observed");
            }
        };
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down)));
        log.Clear();

        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);

        Assert.Equal(
            ["cancel-observed", "first.stop", "first.dispose", "second.initialize", "second.start"],
            log.Take(5));
        Assert.Equal("second", manager.ActiveProvider?.ProviderInstanceId);
        Assert.Equal(1, maximumRunning);
        var diagnostic = Assert.Single(diagnostics, item => item.Operation == nameof(manager.FrameReceived));
        Assert.Equal("consumer", diagnostic.Exception.Message);
    }

    [Fact]
    public async Task StopAsync_StopFailureFailsClosedDisposesOldAndAllowsLaterCleanSwitch()
    {
        var provider = new TestProvider("first") { StopError = new InvalidOperationException("stop") };
        var replacement = new TestProvider("second");
        await using var manager = CreateManager(provider, replacement);
        var frames = new List<InteractionFrame>();
        manager.FrameReceived += (_, args) => frames.Add(args.Frame);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        provider.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StopAsync(CancellationToken.None));
        provider.Emit(Frame("first", "FRONT", 2, Point("first", "FRONT", 1, InteractionPhase.Move)));
        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);

        Assert.DoesNotContain(
            frames,
            frame => frame.Sequence == 2 && frame.Points.Any(point => point.Phase == InteractionPhase.Move));
        Assert.Equal(1, provider.DisposeCount);
        Assert.Equal("second", manager.ActiveProvider?.ProviderInstanceId);
    }

    [Fact]
    public async Task SwitchAsync_FactoryCreatesFreshInstanceAndDisposesOldBeforeCreatingReplacement()
    {
        var log = new List<string>();
        var firstInstances = new List<TestProvider>();
        var secondInstances = new List<TestProvider>();
        await using var manager = new ProviderManager();
        manager.Register(Descriptor(), "first", () =>
        {
            log.Add("first.create");
            var provider = new TestProvider("first", log);
            firstInstances.Add(provider);
            return provider;
        });
        manager.Register(Descriptor(), "second", () =>
        {
            log.Add("second.create");
            var provider = new TestProvider("second", log);
            secondInstances.Add(provider);
            return provider;
        });
        manager.FrameReceived += (_, args) =>
        {
            if (args.Frame.Points.Any(point => point.Phase == InteractionPhase.Cancel))
            {
                log.Add("cancel");
            }
        };
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        firstInstances[0].Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down)));
        log.Clear();

        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        Assert.Equal(
            [
                "cancel", "first.stop", "first.dispose", "second.create", "second.initialize", "second.start",
                "second.stop", "second.dispose", "first.create", "first.initialize", "first.start"
            ],
            log);
        Assert.Equal(2, firstInstances.Count);
        Assert.Single(secondInstances);
        Assert.Equal(1, firstInstances[0].DisposeCount);
        Assert.Equal(0, firstInstances[1].DisposeCount);
        Assert.Equal(1, secondInstances[0].DisposeCount);
    }

    [Fact]
    public async Task SwitchAsync_FactoryInstanceIdMismatchIsDisposedBeforeInitialization()
    {
        var mismatched = new TestProvider("wrong");
        await using var manager = new ProviderManager();
        manager.Register(Descriptor(), "expected", () => mismatched);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("expected", InitializationContext, CancellationToken.None));

        Assert.Equal(0, mismatched.InitializeCount);
        Assert.Equal(1, mismatched.DisposeCount);
        Assert.Null(manager.ActiveProvider);
    }

    [Fact]
    public async Task SwitchAsync_EventSubscriptionFailureStopsDisposesAndUnhooksPartialSubscription()
    {
        var provider = new ThrowingSubscriptionProvider("first");
        await using var manager = new ProviderManager();
        manager.Register(Descriptor(), "first", () => provider);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("first", InitializationContext, CancellationToken.None));

        Assert.Equal(0, provider.FrameSubscriberCount);
        Assert.Equal(1, provider.StopCount);
        Assert.Equal(1, provider.DisposeCount);
        Assert.Null(manager.ActiveProvider);
    }

    [Fact]
    public async Task SwitchAsync_ProviderInstanceIdGetterThrowStillDisposesCreatedInstanceExactlyOnce()
    {
        var provider = new IdentityGetterProvider(_ => throw new InvalidOperationException("identity"));
        var manager = new ProviderManager();
        manager.Register(Descriptor(), "first", () => provider);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("first", InitializationContext, CancellationToken.None));
        await manager.DisposeAsync();

        Assert.Equal("identity", error.Message);
        Assert.Equal(1, provider.GetterCount);
        Assert.Equal(1, provider.DisposeCount);
        Assert.Null(manager.ActiveProvider);
    }

    [Fact]
    public async Task SwitchAsync_ChangingProviderInstanceIdGetterIsReadOnceAndMismatchIsDisposed()
    {
        var provider = new IdentityGetterProvider(
            call => call == 1 ? "wrong" : throw new InvalidOperationException("second-read"));
        var manager = new ProviderManager();
        manager.Register(Descriptor(), "first", () => provider);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("first", InitializationContext, CancellationToken.None));
        await manager.DisposeAsync();

        Assert.Contains("wrong", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, provider.GetterCount);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task StopAsync_StopAndDisposeFailureFaultsManagerAndRejectsEveryLaterSwitch()
    {
        var old = new TestProvider("first")
        {
            StopError = new InvalidOperationException("stop"),
            DisposeError = new InvalidOperationException("dispose")
        };
        var candidateFactoryCalls = 0;
        await using var manager = new ProviderManager();
        manager.Register(Descriptor(), "first", () => old);
        manager.Register(Descriptor(), "second", () =>
        {
            candidateFactoryCalls++;
            return new TestProvider("second");
        });
        var frames = new List<InteractionFrame>();
        manager.FrameReceived += (_, args) => frames.Add(args.Frame);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        old.Emit(Frame("first", "FRONT", 1, Point("first", "FRONT", 1, InteractionPhase.Down)));

        await Assert.ThrowsAnyAsync<Exception>(() => manager.StopAsync(CancellationToken.None));
        old.Emit(Frame("first", "FRONT", 2, Point("first", "FRONT", 1, InteractionPhase.Move)));
        var switchError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("second", InitializationContext, CancellationToken.None));

        Assert.True(manager.IsFaulted);
        Assert.Contains("faulted", switchError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(manager.ActiveProvider);
        Assert.DoesNotContain(
            frames,
            frame => frame.Sequence == 2 && frame.Points.Any(point => point.Phase == InteractionPhase.Move));
        Assert.Equal(0, candidateFactoryCalls);
        old.DisposeError = null;
    }

    [Fact]
    public async Task DisposeAsync_RetriesCreatedProviderWhoseEarlierDisposalCouldNotBeConfirmed()
    {
        var provider = new TestProvider("first")
        {
            StopError = new InvalidOperationException("stop"),
            DisposeError = new InvalidOperationException("dispose")
        };
        var manager = new ProviderManager();
        manager.Register(Descriptor(), "first", () => provider);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => manager.StopAsync(CancellationToken.None));
        provider.DisposeError = null;

        await manager.DisposeAsync();

        Assert.Equal(2, provider.DisposeCount);
    }

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

        Assert.Equal(
            ["cancel", "cancel", "first.stop", "first.dispose", "second.initialize", "second.start"],
            log.Take(6));
        var cancellations = forwarded.Where(frame => frame.Points.Any(point => point.Phase == InteractionPhase.Cancel)).ToArray();
        Assert.Equal(2, cancellations.Length);
        Assert.Equal(["FRONT", "LEFT"], cancellations.Select(frame => frame.SurfaceId));
        Assert.All(cancellations.SelectMany(frame => frame.Points), point => Assert.Equal(InteractionPhase.Cancel, point.Phase));
        Assert.All(cancellations, frame => Assert.True(frame.Sequence is 11 or 21));
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
    public async Task SwitchAsync_AlreadyCapturedOldFrameCallbackCannotCrossCompletedGeneration()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        await using var manager = CreateManager(first, second);
        var frames = new List<InteractionFrame>();
        var rejections = new List<ProviderFrameRejectedEventArgs>();
        manager.FrameReceived += (_, args) => frames.Add(args.Frame);
        manager.FrameRejected += (_, args) => rejections.Add(args);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        var staleCallback = first.CaptureFrameCallback(
            Frame("first", "FRONT", 77, Point("first", "FRONT", 7, InteractionPhase.Move)));

        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);
        frames.Clear();
        rejections.Clear();
        staleCallback();

        Assert.Empty(frames);
        Assert.Empty(rejections);
    }

    [Fact]
    public async Task OutboundEvents_IsolateEverySubscriberIncludingDiagnosticConsumers()
    {
        var first = new TestProvider("first");
        var second = new TestProvider("second");
        await using var manager = CreateManager(first, second);
        var diagnostics = new List<ProviderManagerDiagnosticEventArgs>();
        var statusObserved = 0;
        var rejectionObserved = 0;
        var changesObserved = 0;
        manager.Diagnostic += (_, _) => throw new InvalidOperationException("diagnostic-consumer");
        manager.Diagnostic += (_, args) => diagnostics.Add(args);
        manager.StatusChanged += (_, _) => throw new InvalidOperationException("status-consumer");
        manager.StatusChanged += (_, _) => statusObserved++;
        manager.FrameRejected += (_, _) => throw new InvalidOperationException("rejection-consumer");
        manager.FrameRejected += (_, _) => rejectionObserved++;
        manager.ProviderChanged += (_, _) => throw new InvalidOperationException("change-consumer");
        manager.ProviderChanged += (_, _) => changesObserved++;
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);
        diagnostics.Clear();
        changesObserved = 0;

        first.EmitStatus(ProviderRuntimeStatus.Running, ProviderRuntimeStatus.Running);
        first.Emit(Frame("wrong", "FRONT", 1, Point("wrong", "FRONT", 1, InteractionPhase.Move)));
        await manager.SwitchAsync("second", InitializationContext, CancellationToken.None);

        Assert.Equal(1, statusObserved);
        Assert.Equal(1, rejectionObserved);
        Assert.Equal(1, changesObserved);
        Assert.Contains(diagnostics, item => item.Operation == nameof(manager.StatusChanged));
        Assert.Contains(diagnostics, item => item.Operation == nameof(manager.FrameRejected));
        Assert.Contains(diagnostics, item => item.Operation == nameof(manager.ProviderChanged));
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
    public async Task SwitchAsync_InitializationFailureStopsDisposesAndKeepsFactoryRegistration()
    {
        var log = new List<string>();
        var provider = new TestProvider("first", log) { InitializeError = new InvalidOperationException("init") };
        await using var manager = CreateManager(provider);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("first", InitializationContext, CancellationToken.None));

        Assert.Equal("init", error.Message);
        Assert.Equal(["first.initialize", "first.stop", "first.dispose"], log);
        Assert.Null(manager.ActiveProvider);
        Assert.Single(manager.Providers);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task SwitchAsync_FailedCandidateCanBeRetriedThroughFactoryWithFreshInstance()
    {
        var failed = new TestProvider("first") { InitializeError = new InvalidOperationException("init") };
        var recovered = new TestProvider("first");
        var factoryCalls = 0;
        await using var manager = new ProviderManager();
        manager.Register(Descriptor(), "first", () => ++factoryCalls == 1 ? failed : recovered);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SwitchAsync("first", InitializationContext, CancellationToken.None));
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        Assert.Equal(2, factoryCalls);
        Assert.Equal(1, failed.DisposeCount);
        Assert.Equal(1, recovered.StartCount);
        Assert.Equal("first", manager.ActiveProvider?.ProviderInstanceId);
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
    public async Task StopAsync_CancellationFailsClosedAndDisposesCurrentProvider()
    {
        var provider = new TestProvider("first") { StopError = new OperationCanceledException() };
        await using var manager = CreateManager(provider);
        await manager.SwitchAsync("first", InitializationContext, CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() => manager.StopAsync(CancellationToken.None));

        Assert.Null(manager.ActiveProvider);
        Assert.Equal(1, provider.DisposeCount);
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

        Assert.Equal(["cancel", "first.stop", "first.dispose", "changed:none"], log);
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
        Assert.Equal(0, second.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => manager.SwitchAsync("second", InitializationContext, CancellationToken.None));
    }

    private static ProviderManager CreateManager(params TestProvider[] providers)
    {
        var manager = new ProviderManager();
        foreach (var provider in providers)
        {
            manager.Register(Descriptor(), provider.ProviderInstanceId, () => provider);
        }

        return manager;
    }

    private static async Task<bool> CompletesWithinAsync(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(750))) != task)
        {
            return false;
        }

        await task;
        return true;
    }

    private static async Task<bool> StopAfterReleaseAsync(
        Task release,
        ProviderManager manager,
        Func<bool> emitHasReturned)
    {
        await release;
        var observedEmitReturn = emitHasReturned();
        await manager.StopAsync(CancellationToken.None);
        return observedEmitReturn;
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
        public Exception? DisposeError { get; set; }
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
            return DisposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposeError);
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

        internal Action CaptureFrameCallback(InteractionFrame frame)
        {
            var handlers = FrameReceived;
            return () => handlers?.Invoke(this, new InteractionFrameEventArgs(frame));
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        internal static EmptyServices Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }

    private sealed class ThrowingSubscriptionProvider(string providerInstanceId) : IInteractionProvider
    {
        private EventHandler<InteractionFrameEventArgs>? _frameReceived;

        public string ProviderInstanceId { get; } = providerInstanceId;
        public ProviderRuntimeStatus Status => ProviderRuntimeStatus.Created;
        public int FrameSubscriberCount => _frameReceived?.GetInvocationList().Length ?? 0;
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public event EventHandler<InteractionFrameEventArgs>? FrameReceived
        {
            add => _frameReceived += value;
            remove => _frameReceived -= value;
        }

        public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged
        {
            add => throw new InvalidOperationException("subscribe");
            remove { }
        }

        public Task InitializeAsync(
            ProviderInitializationContext context,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IdentityGetterProvider(Func<int, string> getter) : IInteractionProvider
    {
        public string ProviderInstanceId => getter(++GetterCount);
        public ProviderRuntimeStatus Status => ProviderRuntimeStatus.Created;
        public int GetterCount { get; private set; }
        public int DisposeCount { get; private set; }

        public event EventHandler<InteractionFrameEventArgs>? FrameReceived
        {
            add { }
            remove { }
        }

        public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(
            ProviderInitializationContext context,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
