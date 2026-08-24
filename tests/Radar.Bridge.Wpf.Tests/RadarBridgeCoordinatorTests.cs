using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Pipes;
using Yuexin.Radar.Bridge.Wpf;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarBridgeCoordinatorTests
{
    [Fact]
    public async Task Coordinator_ProviderModeAppliesAuthenticatedUnityConnectionStatusWithoutLegacyIpc()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [ScreenConfiguration("front", "f1", 1920, 1080)]
        };
        var factory = new FakePipelineFactory();
        await using var coordinator = new RadarBridgeCoordinator(
            configuration,
            NullLogger<RadarBridgeCoordinator>.Instance,
            factory,
            sendPointerBatchAsync: (_, _) => Task.FromResult(true),
            enableLegacyIpc: false);

        coordinator.ApplyUnityConnectionStatus(new UnityClientStatus(
            true,
            42,
            "2021.3.45f1",
            [new RadarScreenInfo("front", "Front", 1920, 1080, true, 0)],
            null,
            0,
            null));

        Assert.True(coordinator.UnityStatus.IsConnected);
        Assert.Equal(42, coordinator.UnityStatus.ProcessId);
        Assert.Equal("front", Assert.Single(coordinator.UnityStatus.Screens).ScreenId);

        coordinator.ApplyUnityConnectionStatus(UnityClientStatus.Disconnected);
        Assert.False(coordinator.UnityStatus.IsConnected);
    }

    [Fact]
    public async Task MainViewModel_InitializesSensorStateThatWasRunningBeforeWindowCreation()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [ScreenConfiguration("front", "f1", 1920, 1080)]
        };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        await coordinator.ConnectSensorAsync("front", "f1");

        using var viewModel = new MainViewModel(configuration, coordinator);

        Assert.Equal(RadarSensorRuntimeState.Running, viewModel.SelectedScreen!.Sensors.Single().RuntimeState);
        Assert.Equal(1, viewModel.ConnectedRadarCount);
        Assert.Equal("雷达：1/1 已连接", viewModel.RadarConnectionText);
    }

    [Fact]
    public async Task Coordinator_UnityStatusSubscription_DrainsInitialSnapshotOutsideItsLockInOrder()
    {
        var initialEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<bool>();
        await using var coordinator = new RadarBridgeCoordinator(
            new RadarAppConfiguration(),
            NullLogger<RadarBridgeCoordinator>.Instance,
            new FakePipelineFactory(),
            sendPointerBatchAsync: (_, _) => Task.FromResult(true),
            enableLegacyIpc: false);

        var subscribe = Task.Run(() => coordinator.SubscribeUnityStatus(status =>
        {
            received.Add(status.IsConnected);
            if (initialEntered.TrySetResult()) releaseInitial.Task.GetAwaiter().GetResult();
        }));
        await initialEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var update = Task.Run(() => coordinator.ApplyUnityConnectionStatus(
            new UnityClientStatus(true, 42, "2021.3.45f1", [], null, 0, null)));
        await update.WaitAsync(TimeSpan.FromSeconds(5));
        releaseInitial.TrySetResult();
        await subscribe.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([false, true], received);
    }

    [Fact]
    public async Task Coordinator_SensorStateSubscription_DrainsInitialAndConcurrentStateInOrder()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var initialEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<RadarSensorRuntimeStateSnapshot>();
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));

        var subscribe = Task.Run(() => coordinator.SubscribeSensorStates(snapshot =>
        {
            received.Add(snapshot);
            if (initialEntered.TrySetResult()) releaseInitial.Task.GetAwaiter().GetResult();
        }));
        await initialEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.ConnectSensorAsync("front", "f1").WaitAsync(TimeSpan.FromSeconds(5));
        releaseInitial.TrySetResult();
        await subscribe.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([RadarSensorRuntimeState.Stopped, RadarSensorRuntimeState.Running], received.Select(snapshot => snapshot.State.State));
        Assert.Equal(received[0].BindingGeneration, received[1].BindingGeneration);
        Assert.True(received[1].Version > received[0].Version);
    }

    [Fact]
    public async Task Coordinator_SensorRefreshHandlerCanSynchronouslyReenterTopologyWithoutDeadlock()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens =
            [
                ScreenConfiguration("front", "f1", 1920, 1080),
                ScreenConfiguration("left", "l1", 1920, 1080)
            ]
        };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        var expandedTopology = Hello(
            Screen("front", "Front", true, 1920, 1080, 0),
            Screen("left", "Left", false, 1920, 1080, 1));
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        var reentered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SubscribeSensorStates(snapshot =>
        {
            if (!string.Equals(snapshot.State.ScreenId, "left", StringComparison.OrdinalIgnoreCase)) return;
            coordinator.ApplyUnityTopologyAsync(expandedTopology).GetAwaiter().GetResult();
            reentered.TrySetResult();
        });

        await coordinator.ApplyUnityTopologyAsync(expandedTopology).WaitAsync(TimeSpan.FromSeconds(5));

        await reentered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Coordinator_SensorRefreshGenerationOverflowDoesNotCommitEarlierVersionChange()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens =
            [
                ScreenConfiguration("front", "f1", 1920, 1080),
                ScreenConfiguration("left", "l1", 1920, 1080)
            ]
        };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        coordinator.SetSensorStateSequenceForTest("front", "f1", long.MaxValue - 1, long.MaxValue);
        factory["front", "f1"].SetStateWithoutPublishing(RadarSensorRuntimeState.Running);
        var before = coordinator.GetSensorStateBookkeepingForTest();

        await Assert.ThrowsAsync<OverflowException>(() => coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("front", "Front", true, 1920, 1080, 0),
            Screen("left", "Left", false, 1920, 1080, 1))));

        var after = coordinator.GetSensorStateBookkeepingForTest();
        Assert.Equal(before.NextBindingGeneration, after.NextBindingGeneration);
        Assert.Equal(before.PublicationCount, after.PublicationCount);
        Assert.Equal(before.IsDraining, after.IsDraining);
        Assert.Equal(before.IsDrainDeferred, after.IsDrainDeferred);
        Assert.Equal(before.States, after.States);
    }

    [Fact]
    public async Task Coordinator_SensorStateVersionOverflowLeavesBookkeepingUnchanged()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        coordinator.SetSensorStateSequenceForTest("front", "f1", long.MaxValue, 1);
        var before = coordinator.GetSensorStateBookkeepingForTest();

        Assert.Throws<OverflowException>(() => factory["front", "f1"].PublishState(RadarSensorRuntimeState.Running));

        var after = coordinator.GetSensorStateBookkeepingForTest();
        Assert.Equal(before.NextBindingGeneration, after.NextBindingGeneration);
        Assert.Equal(before.PublicationCount, after.PublicationCount);
        Assert.Equal(before.IsDraining, after.IsDraining);
        Assert.Equal(before.IsDrainDeferred, after.IsDrainDeferred);
        Assert.Equal(before.States, after.States);
    }

    [Fact]
    public async Task Coordinator_ReplacementGenerationWinsAfterLateOldPipelineHandler()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        configuration.Screens[0].Sensors[0].SourceMode = RadarSensorSourceMode.Real;
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        var oldPipeline = factory["front", "f1"];
        var oldHandlers = oldPipeline.CaptureStateChangedSubscribers();
        Assert.NotNull(oldHandlers);
        var observed = new List<RadarSensorRuntimeStateSnapshot>();
        coordinator.SubscribeSensorStates(observed.Add);
        using var ui = new DedicatedSynchronizationContext();
        var viewModel = ui.Invoke(() => new MainViewModel(configuration, coordinator));
        var oldHandlerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateOldHandler = Task.Run(() => oldPipeline.PublishStateTo(state =>
        {
            oldHandlerBlocked.TrySetResult();
            releaseOldHandler.Task.GetAwaiter().GetResult();
            oldHandlers!(state);
        }, RadarSensorRuntimeState.Faulted));
        await oldHandlerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.StartAllSimulationAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = factory.GetInstances("front", "f1").Last();
        Assert.NotSame(oldPipeline, replacement);
        releaseOldHandler.TrySetResult();
        await lateOldHandler.WaitAsync(TimeSpan.FromSeconds(5));
        await ui.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(observed[^1].BindingGeneration > observed[0].BindingGeneration);
        Assert.Equal(RadarSensorRuntimeState.Running, observed[^1].State.State);
        Assert.Equal(RadarSensorRuntimeState.Running, viewModel.SelectedScreen!.Sensors.Single().RuntimeState);
        Assert.Equal(1, viewModel.ConnectedRadarCount);
        Assert.Equal("雷达：1/1 已连接", viewModel.RadarConnectionText);
        ui.Invoke(viewModel.Dispose);
    }

    [Fact]
    public async Task Coordinator_BlockedProviderSendCannotRestoreDisconnectedUnityStatus()
    {
        var sendEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new RadarBridgeCoordinator(
            new RadarAppConfiguration(),
            NullLogger<RadarBridgeCoordinator>.Instance,
            new FakePipelineFactory(),
            sendPointerBatchAsync: async (_, _) =>
            {
                sendEntered.TrySetResult();
                return await releaseSend.Task;
            },
            enableLegacyIpc: false);
        coordinator.ApplyUnityConnectionStatus(new UnityClientStatus(
            true, 42, "2021.3.45f1", [], null, 0, null));

        var send = coordinator.SendProviderBatchForTestAsync(9, DateTimeOffset.UnixEpoch);
        await sendEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.ApplyUnityConnectionStatus(UnityClientStatus.Disconnected);
        releaseSend.TrySetResult(true);
        await send.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(coordinator.UnityStatus.IsConnected);
        Assert.Equal(9, coordinator.UnityStatus.LastBatchSequence);
    }

    [Fact]
    public async Task Coordinator_StatusChangedPublishesInCommitOrderWhenFirstHandlerBlocks()
    {
        var firstPublicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new List<bool>();
        await using var coordinator = new RadarBridgeCoordinator(
            new RadarAppConfiguration(),
            NullLogger<RadarBridgeCoordinator>.Instance,
            new FakePipelineFactory(),
            sendPointerBatchAsync: (_, _) => Task.FromResult(true),
            enableLegacyIpc: false);
        coordinator.UnityStatusChanged += status =>
        {
            published.Add(status.IsConnected);
            if (!status.IsConnected || firstPublicationEntered.Task.IsCompleted) return;
            firstPublicationEntered.TrySetResult();
            releaseFirstPublication.Task.GetAwaiter().GetResult();
        };

        var connected = Task.Run(() => coordinator.ApplyUnityConnectionStatus(new UnityClientStatus(
            true, 42, "2021.3.45f1", [], null, 0, null)));
        await firstPublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disconnected = Task.Run(() => coordinator.ApplyUnityConnectionStatus(UnityClientStatus.Disconnected));
        await disconnected.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirstPublication.TrySetResult();
        await connected.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal([true, false], published);
        Assert.False(coordinator.UnityStatus.IsConnected);
    }

    [Fact]
    public void RuntimeContract_DoesNotExposePrimarySensorFacade()
    {
        var forbidden = new[] { "SnapshotUpdated", "ConnectionStateChanged", "ConnectionState" };

        Assert.DoesNotContain(typeof(IRadarBridgeRuntime).GetMembers(), member => forbidden.Contains(member.Name, StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(RadarBridgeCoordinator).GetMembers(), member => forbidden.Contains(member.Name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Coordinator_UnityHandshakeAutomaticallyStartsEveryEnabledSensorPipeline()
    {
        var configuration = ThreeScreenFourSensorConfiguration();
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await coordinator.StartInfrastructureAsync(cancellation.Token);

        await using var client = await ConnectAsync(configuration.Ipc.PipeName, cancellation.Token);
        var created = factory.Created.ToArray();
        Assert.Equal(4, created.Length);
        await Task.WhenAll(created.Select(pipeline => pipeline.StartEntered.Task))
            .WaitAsync(TimeSpan.FromSeconds(1), cancellation.Token);

        Assert.All(created, pipeline =>
        {
            Assert.Equal(1, pipeline.StartCallCount);
            Assert.Equal(RadarSensorRuntimeState.Running, pipeline.State);
        });
    }

    [Fact]
    public async Task Coordinator_ProviderModeStartsOutputSchedulerWithoutOpeningLegacyRadarPipe()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [ScreenConfiguration("front", "f1", 1920, 1080)]
        };
        configuration.Ipc.PipeName = "RadarControl.ProviderMode.Tests." + Guid.NewGuid().ToString("N");
        var factory = new FakePipelineFactory();
        var firstBatch = new TaskCompletionSource<PointerBatchPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new RadarBridgeCoordinator(
            configuration,
            NullLogger<RadarBridgeCoordinator>.Instance,
            factory,
            sendPointerBatchAsync: (batch, _) =>
            {
                firstBatch.TrySetResult(batch);
                return Task.FromResult(true);
            },
            enableLegacyIpc: false);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));

        await coordinator.StartInfrastructureAsync();

        var batch = await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("front", Assert.Single(batch.Screens).Screen.ScreenId);
        Assert.False(coordinator.UnityStatus.IsConnected);
        await using var client = new NamedPipeClientStream(
            ".",
            configuration.Ipc.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ConnectAsync(connectTimeout.Token));
    }

    [Fact]
    public void Coordinator_ProviderModeRequiresAnExplicitInteractionOutputSeam()
    {
        Assert.Throws<ArgumentException>(() => new RadarBridgeCoordinator(
            new RadarAppConfiguration(),
            NullLogger<RadarBridgeCoordinator>.Instance,
            new FakePipelineFactory(),
            enableLegacyIpc: false));
    }

    [Fact]
    public async Task Coordinator_ProviderOutputFailureReleasesTransitionAndSchedulerRetriesIt()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [ScreenConfiguration("front", "f1", 1920, 1080)]
        };
        configuration.Screens[0].Tracking.ConfirmFrames = 1;
        var factory = new FakePipelineFactory();
        var firstFailure = new TaskCompletionSource<PointerBatchPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryDelivered = new TaskCompletionSource<PointerBatchPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var transitionAttempts = 0;
        await using var coordinator = new RadarBridgeCoordinator(
            configuration,
            NullLogger<RadarBridgeCoordinator>.Instance,
            factory,
            sendPointerBatchAsync: (batch, _) =>
            {
                var isTransition = batch.Screens.Any(frame =>
                    frame.Screen.ScreenId == "front"
                    && frame.Pointers.Any(pointer => pointer.Phase == RadarPointerPhase.Up));
                if (!isTransition) return Task.FromResult(true);

                if (Interlocked.Increment(ref transitionAttempts) == 1)
                {
                    firstFailure.TrySetResult(batch);
                    throw new IOException("interaction sink failed");
                }

                retryDelivered.TrySetResult(batch);
                return Task.FromResult(true);
            },
            enableLegacyIpc: false);
        coordinator.LogReceived += logs.Enqueue;
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].Publish(Detection("f1", 400, 300));
        Assert.Equal(
            RadarPointerPhase.Down,
            Assert.Single(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens.Single().Pointers).Phase);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));

        await coordinator.StartInfrastructureAsync();

        var failed = await firstFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var retried = await retryDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(failed.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers).Phase);
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(retried.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers).Phase);
        Assert.Equal(2, Volatile.Read(ref transitionAttempts));
        Assert.Contains(logs, message => message.Contains("interaction output failed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("interaction sink failed", coordinator.UnityStatus.LastError, StringComparison.OrdinalIgnoreCase);

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Coordinator_PublishesAllEnabledScreensAndMergesFrontOverlap()
    {
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2)));

        factory["front", "f1"].Publish(Detection("f1", 1000, 700));
        factory["front", "f2"].Publish(Detection("f2", 1040, 700));

        var batch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(["left", "front", "right"], batch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Empty(batch.Screens.Single(frame => frame.Screen.ScreenId == "left").Pointers);
        Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Equal(4096, batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Screen.WidthPixels);
    }

    [Fact]
    public async Task Coordinator_IsolatesFaultedSensorAndContinuesSiblingScreenFusion()
    {
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2)));
        factory["front", "f1"].Fault();
        factory["front", "f2"].Publish(Detection("f2", 1100, 700));
        factory["right", "r1"].Publish(Detection("r1", 500, 300));

        var batch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Single(batch.Screens.Single(frame => frame.Screen.ScreenId == "right").Pointers);
        Assert.Equal(RadarSensorRuntimeState.Faulted, factory["front", "f1"].State);
    }

    [Fact]
    public async Task Coordinator_HotRemovedScreenPublishesUpThenEmptyFrameBeforeReset()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [ScreenConfiguration("front", "f1", 1920, 1080)]
        };
        configuration.Screens[0].Tracking.ConfirmFrames = 1;
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].Publish(Detection("f1", 400, 300));
        Assert.Single(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens.Single().Pointers);

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        var upBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2));
        var emptyBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3));
        var settledBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(4));

        var upFrame = upBatch.Screens.Single(frame => frame.Screen.ScreenId == "front");
        Assert.Equal(["front", "left"], upBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Single(upFrame.Pointers);
        Assert.Equal(RadarPointerPhase.Up, upFrame.Pointers[0].Phase);
        Assert.Equal(["front", "left"], emptyBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Empty(emptyBatch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Equal(["left"], settledBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.True(factory["front", "f1"].StopCallCount > 0);
    }

    [Fact]
    public async Task Coordinator_RetriesUndeliveredTransitionBeforeActivatingReplacement()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        configuration.Screens[0].Tracking.ConfirmFrames = 1;
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].Publish(Detection("f1", 400, 300));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        var failedUp = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2), delivered: false);
        var retriedUp = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3));
        var empty = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(4));
        var settled = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(5));

        Assert.Equal(RadarPointerPhase.Up, Assert.Single(failedUp.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers).Phase);
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(retriedUp.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers).Phase);
        Assert.Empty(empty.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.Equal(["left"], settled.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Equal(1, factory["front", "f1"].StopCallCount);
    }

    [Fact]
    public async Task Coordinator_WaitsForLeasedPipelineOperationBeforeRetirementDisposesIt()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].BlockStart();
        var connect = coordinator.ConnectSensorAsync("front", "f1");
        await factory["front", "f1"].StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3), waitForRetirement: false);
        await Task.Delay(20);

        Assert.False(factory["front", "f1"].DisposedDuringOperation);
        factory["front", "f1"].ReleaseStart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await WaitUntilAsync(() => factory["front", "f1"].Disposed, new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
        Assert.False(factory["front", "f1"].DisposedDuringOperation);
    }

    [Fact]
    public async Task Coordinator_StopTimeoutDefersPipelineDisposeUntilStopCompletes()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        var pipeline = factory["front", "f1"];
        pipeline.BlockStop();

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2));
        await pipeline.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(pipeline.Disposed);
        pipeline.ReleaseStop();
        await WaitUntilAsync(() => pipeline.Disposed, new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
        Assert.Equal(1, pipeline.DisposeCallCount);
        Assert.False(pipeline.DisposedDuringOperation);
    }

    [Fact]
    public async Task Coordinator_FactoryFailureLeavesPreviousTopologyAndConfigurationUnchanged()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory { ThrowOnCreate = true };
        await using var coordinator = CreateCoordinator(configuration, factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0))));

        Assert.Equal(["front"], configuration.Screens.Select(screen => screen.ScreenId));
        Assert.Empty(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens);
    }

    [Fact]
    public async Task Coordinator_SaveFailureLeavesTopologyAndConfigurationUnchanged()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        var saves = 0;
        await using var coordinator = CreateCoordinator(configuration, factory, (_, _) => { saves++; return Task.FromException(new IOException("save failed")); });

        await Assert.ThrowsAsync<IOException>(() => coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0))));

        Assert.Equal(1, saves);
        Assert.Equal(["front"], configuration.Screens.Select(screen => screen.ScreenId));
        Assert.True(factory.AllDisposed);
        Assert.Empty(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens);
    }

    [Fact]
    public async Task Coordinator_HelloPersistsNewZeroSensorScreenAndAcknowledgesStableTopology()
    {
        var path = Path.Combine(Path.GetTempPath(), "RadarControl.ZeroSensors." + Guid.NewGuid().ToString("N") + ".json");
        var configuration = new RadarAppConfiguration
        {
            Screens =
            [
                new RadarScreenConfiguration
                {
                    ScreenId = "front",
                    UnityDisplayName = "Configured Front",
                    ResolutionMode = RadarResolutionMode.Override,
                    WidthPixels = 3000,
                    HeightPixels = 1200,
                    Sensors = [Sensor("f1")]
                }
            ]
        };
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        await RadarConfigurationStore.SaveAsync(path, configuration);

        try
        {
            await using var coordinator = new RadarBridgeCoordinator(
                configuration,
                NullLogger<RadarBridgeCoordinator>.Instance,
                new FakePipelineFactory(),
                configurationPath: path);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await coordinator.StartInfrastructureAsync(cancellation.Token);
            await using var client = new NamedPipeClientStream(".", configuration.Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cancellation.Token);
            await IpcStream.WriteAsync(client, IpcEnvelope.Create(IpcMessageType.Hello, 1, Hello(
                Screen("left", "Left", false, 1920, 1440, 0),
                Screen("front", "Unity Front", true, 4096, 1536, 1))), cancellation.Token);

            var response = await IpcStream.ReadAsync(client, cancellation.Token);

            Assert.Equal(IpcMessageType.HelloAck, response.MessageType);
            var acknowledgement = response.DeserializePayload<HelloAckPayload>();
            Assert.Equal(["left", "front"], acknowledgement.Screens.Select(screen => screen.ScreenId));
            var persisted = await RadarConfigurationStore.LoadAsync(path, cancellation.Token);
            Assert.True(persisted.CanPersist);
            Assert.Equal(["left", "front"], persisted.Screens.OrderBy(screen => screen.UnityOrder).Select(screen => screen.ScreenId));
            var front = persisted.Screens.Single(screen => screen.ScreenId == "front");
            Assert.True(front.IsPrimary);
            Assert.Equal(1, front.UnityOrder);
            Assert.Equal(3000, front.EffectiveWidthPixels);
            Assert.Equal(1200, front.EffectiveHeightPixels);
            Assert.Equal("f1", Assert.Single(front.Sensors).SensorId);
            var left = persisted.Screens.Single(screen => screen.ScreenId == "left");
            Assert.False(left.IsPrimary);
            Assert.Equal(0, left.UnityOrder);
            Assert.Empty(left.Sensors);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ViewModelCreatedBeforeHelloRefreshesOnUiThreadPreservesSelectionAndAppliesCurrentObjects()
    {
        var path = Path.Combine(Path.GetTempPath(), "RadarControl.ViewModelRefresh." + Guid.NewGuid().ToString("N") + ".json");
        var sensor = Sensor("f1");
        sensor.Enabled = false;
        var configuration = new RadarAppConfiguration
        {
            Screens = [new RadarScreenConfiguration { ScreenId = "front", UnityDisplayName = "Front", Sensors = [sensor] }]
        };
        await RadarConfigurationStore.SaveAsync(path, configuration);

        try
        {
            await using var coordinator = new RadarBridgeCoordinator(
                configuration,
                NullLogger<RadarBridgeCoordinator>.Instance,
                new FakePipelineFactory(),
                configurationPath: path);
            using var ui = new DedicatedSynchronizationContext();
            var viewModel = ui.Invoke(() => new MainViewModel(configuration, coordinator));
            try
            {
                ui.Invoke(() =>
                {
                    viewModel.SelectedScreen = viewModel.Screens.Single(screen => screen.ScreenId == "front");
                    viewModel.SelectedSensor = viewModel.SelectedScreen.Sensors.Single(sensorItem => sensorItem.SensorId == "f1");
                });
                var collectionChangedThreadId = 0;
                ui.Invoke(() => viewModel.Screens.CollectionChanged += (_, _) => collectionChangedThreadId = Environment.CurrentManagedThreadId);

                await coordinator.ApplyUnityTopologyAsync(Hello(
                    Screen("left", "Left", false, 1920, 1440, 0),
                    Screen("front", "Front", true, 4096, 1536, 1)));
                await ui.DrainAsync();

                ui.Invoke(() =>
                {
                    Assert.Equal(2, viewModel.Screens.Count);
                    Assert.Equal("front", viewModel.SelectedScreen?.ScreenId);
                    Assert.Equal("f1", viewModel.SelectedSensor?.SensorId);
                });
                Assert.Equal(ui.ThreadId, collectionChangedThreadId);

                ui.Invoke(() =>
                {
                    var left = viewModel.Screens.Single(screen => screen.ScreenId == "left");
                    viewModel.SelectedScreen = left;
                    left.ResolutionMode = RadarResolutionMode.Override;
                    left.WidthPixels = 1800;
                    left.HeightPixels = 1000;
                    viewModel.SaveConfigurationCommand.Execute(null);
                });

                using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await WaitUntilAsync(
                    () => ui.Invoke(() => viewModel.SaveConfigurationCommand.CanExecute(null)),
                    saveTimeout.Token);
                await ui.DrainAsync();

                var persisted = await RadarConfigurationStore.LoadAsync(path);
                var persistedLeft = persisted.Screens.Single(screen => screen.ScreenId == "left");
                Assert.Equal(RadarResolutionMode.Override, persistedLeft.ResolutionMode);
                Assert.Equal(1800, persistedLeft.WidthPixels);
                Assert.Equal(1000, persistedLeft.HeightPixels);
                var currentLeft = configuration.Screens.Single(screen => screen.ScreenId == "left");
                Assert.Equal(RadarResolutionMode.Override, currentLeft.ResolutionMode);
                Assert.Equal(1800, currentLeft.WidthPixels);
                Assert.Equal(1000, currentLeft.HeightPixels);
            }
            finally
            {
                ui.Invoke(viewModel.Dispose);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Coordinator_ApplyConfigurationSaveFailureLeavesActiveRuntimeUnchanged()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        var saves = 0;
        var failSaves = false;
        await using var coordinator = CreateCoordinator(configuration, factory, (_, _) => { saves++; return failSaves ? Task.FromException(new IOException("save failed")) : Task.CompletedTask; });
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        saves = 0;
        failSaves = true;
        var active = factory["front", "f1"];
        var before = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        await Assert.ThrowsAsync<IOException>(() => coordinator.ApplyConfigurationAsync());

        Assert.Equal(1, saves);
        Assert.False(active.Disposed);
        Assert.Same(configuration, configuration);
        Assert.Single(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2)).Screens);
        Assert.Equal(before.Screens.Single().Screen.ScreenId, coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3)).Screens.Single().Screen.ScreenId);
        Assert.True(factory.Instances.Where(pipeline => !ReferenceEquals(pipeline, active)).All(pipeline => pipeline.Disposed));
    }

    [Fact]
    public async Task Coordinator_RejectsTopologyAndApplyConfigurationForRejectedLoadWithoutPersisting()
    {
        var path = Path.Combine(Path.GetTempPath(), "RadarControl.Rejected." + Guid.NewGuid().ToString("N") + ".json");
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":99,\"screens\":[]}");
        await File.WriteAllBytesAsync(path, sourceBytes);
        var configuration = await RadarConfigurationStore.LoadAsync(path);
        var factory = new FakePipelineFactory();
        var saves = 0;
        await using var coordinator = CreateCoordinator(configuration, factory, (_, _) => { saves++; return Task.CompletedTask; });

        var topology = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0))));
        var apply = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyConfigurationAsync());

        Assert.Contains("rejected", topology.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rejected", apply.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, saves);
        Assert.False(configuration.CanPersist);
        Assert.Empty(factory.Instances);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(path));
        File.Delete(path);
    }

    [Fact]
    public async Task Coordinator_SecondPipelineFactoryFailureDisposesFirstStagedPipeline()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [new RadarScreenConfiguration { ScreenId = "front", UnityDisplayName = "Front", Sensors = [Sensor("f1"), Sensor("f2")] }]
        };
        var factory = new FakePipelineFactory { ThrowOnCreateNumber = 2 };
        await using var coordinator = CreateCoordinator(configuration, factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0))));

        Assert.True(factory["front", "f1"].Disposed);
        Assert.Empty(coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1)).Screens);
    }

    [Fact]
    public async Task Coordinator_StagesExistingScreenNewSensorBeforePersist()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var order = new List<string>();
        var factory = new FakePipelineFactory { CreateObserved = sensor => order.Add("factory:" + sensor) };
        await using var coordinator = CreateCoordinator(configuration, factory, (_, _) => { order.Add("persist"); return Task.CompletedTask; });
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        order.Clear();
        configuration.Screens[0].Sensors.Add(Sensor("f2"));

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));

        Assert.Equal(["factory:f1", "factory:f2", "persist"], order);
    }

    [Fact]
    public async Task Coordinator_DisposeBoundsHungOperationAndQuarantinesPipelineUntilRelease()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].BlockStart(ignoreCancellation: true);
        var operation = coordinator.ConnectSensorAsync("front", "f1");
        await factory["front", "f1"].StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(factory["front", "f1"].Disposed);
        factory["front", "f1"].ReleaseStart();
        await operation;
        await WaitUntilAsync(() => factory["front", "f1"].Disposed, new CancellationTokenSource(TimeSpan.FromSeconds(1)).Token);
    }

    [Fact]
    public async Task Coordinator_BlockedTransitionSendSerializesConcurrentTopologyAndPreservesUpThenEmpty()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        configuration.Screens[0].Tracking.ConfirmFrames = 1;
        var factory = new FakePipelineFactory();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new System.Collections.Concurrent.ConcurrentQueue<PointerBatchPayload>();
        var upAttempts = 0;
        await using var coordinator = CreateCoordinator(configuration, factory, send: async (batch, _) =>
        {
            sent.Enqueue(batch);
            if (batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Any(pointer => pointer.Phase == RadarPointerPhase.Up)))
            {
                if (Interlocked.Increment(ref upAttempts) == 1)
                {
                    sendStarted.TrySetResult();
                    await releaseSend.Task;
                    return false;
                }
            }
            return true;
        });
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].Publish(Detection("f1", 400, 300));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        await coordinator.StartInfrastructureAsync();
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var concurrent = coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        await concurrent.WaitAsync(TimeSpan.FromSeconds(1));
        releaseSend.TrySetResult();
        await WaitUntilAsync(() => Volatile.Read(ref upAttempts) >= 2 && sent.Any(batch => batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Count == 0)), new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);

        var batches = sent.ToArray();
        var firstUp = Array.FindIndex(batches, batch => batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Any(pointer => pointer.Phase == RadarPointerPhase.Up)));
        var retriedUp = Array.FindIndex(batches, firstUp + 1, batch => batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Any(pointer => pointer.Phase == RadarPointerPhase.Up)));
        var empty = Array.FindIndex(batches, retriedUp + 1, batch => batch.Screens.Any(frame => frame.Screen.ScreenId == "front" && frame.Pointers.Count == 0));
        Assert.Equal(2, upAttempts);
        Assert.True(firstUp >= 0 && retriedUp > firstUp && empty > retriedUp);
    }

    [Fact]
    public async Task Coordinator_StartStopAndDisposeAreConcurrentSafe()
    {
        var factory = new FakePipelineFactory();
        var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), factory);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => coordinator.StartInfrastructureAsync()));
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => coordinator.DisconnectAllAsync()));
            await Task.WhenAll(coordinator.DisposeAsync().AsTask(), coordinator.DisposeAsync().AsTask());
        }
        finally
        {
            await coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Coordinator_ScreenRemovalTransitionsWithACompleteThreeScreenBatch()
    {
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(ThreeScreenFourSensorConfiguration(), factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2)));
        factory["front", "f1"].Publish(Detection("f1", 1000, 700));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", true, 1920, 1440, 0),
            Screen("right", "Right", false, 1920, 1440, 1)));
        var upBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2));
        var emptyBatch = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.Equal(["left", "front", "right"], upBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Equal(["left", "front", "right"], emptyBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(upBatch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers).Phase);
        Assert.Empty(emptyBatch.Screens.Single(frame => frame.Screen.ScreenId == "front").Pointers);
        Assert.True(emptyBatch.Screens.Select(frame => frame.Sequence).Distinct().Single() > upBatch.Screens.Select(frame => frame.Sequence).Distinct().Single());
    }

    [Fact]
    public async Task Coordinator_ConcurrentRetirementsKeepEveryNegotiatedScreenInEachTransitionBatch()
    {
        var configuration = ThreeScreenFourSensorConfiguration();
        configuration.Screens.ForEach(screen => screen.Tracking.ConfirmFrames = 1);
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2)));
        factory["left", "l1"].Publish(Detection("l1", 300, 400));
        factory["right", "r1"].Publish(Detection("r1", 500, 400));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 4096, 1536, 0)));
        var batches = Enumerable.Range(2, 4).Select(second => coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(second))).ToArray();

        Assert.All(batches, batch => Assert.Equal(["front", "left", "right"], batch.Screens.Select(frame => frame.Screen.ScreenId)));
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(batches[0].Screens.Single(frame => frame.Screen.ScreenId == "left").Pointers).Phase);
        Assert.Empty(batches[1].Screens.Single(frame => frame.Screen.ScreenId == "left").Pointers);
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(batches[2].Screens.Single(frame => frame.Screen.ScreenId == "right").Pointers).Phase);
        Assert.Empty(batches[3].Screens.Single(frame => frame.Screen.ScreenId == "right").Pointers);
    }

    [Fact]
    public async Task Coordinator_CoalescesRetiringScreenReplacementsToTheLatestTopology()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [new RadarScreenConfiguration
            {
                ScreenId = "front",
                UnityDisplayName = "Front",
                ResolutionMode = RadarResolutionMode.FollowUnityDefault,
                Sensors = [Sensor("f1")]
            }]
        };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));

        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 2560, 1440, 0)));
        var stagedB = factory.Instances.Last();
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 3840, 2160, 0)));
        var stagedC = factory.Instances.Last();

        var up = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));
        var empty = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2));
        var settled = coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(3));

        Assert.Equal(1920, up.Screens.Single().Screen.WidthPixels);
        Assert.Equal(1920, empty.Screens.Single().Screen.WidthPixels);
        Assert.Equal(3840, settled.Screens.Single().Screen.WidthPixels);
        Assert.True(stagedB.Disposed);
        Assert.False(stagedC.Disposed);
    }

    [Fact]
    public async Task Coordinator_ConnectAllAcquiresAndInvokesPipelinesInStableScreenIdOrder()
    {
        var configuration = ThreeScreenFourSensorConfiguration();
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2)));

        await coordinator.ConnectAllAsync();

        Assert.Equal(["front/f1", "front/f2", "left/l1", "right/r1"], factory.StartOrder);
    }

    [Fact]
    public async Task Coordinator_StartAllSimulationConvertsPersistsAndStartsEveryAssociatedSensor()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens =
            [
                ScreenConfiguration("left", "l1", 1920, 1440),
                new RadarScreenConfiguration
                {
                    ScreenId = "front",
                    UnityDisplayName = "Front",
                    Sensors = [Sensor("f1"), Sensor("f2")]
                }
            ]
        };
        configuration.Screens[0].Sensors[0].SourceMode = RadarSensorSourceMode.Real;
        configuration.Screens[1].Sensors[0].SourceMode = RadarSensorSourceMode.Real;
        configuration.Screens[1].Sensors[1].SourceMode = RadarSensorSourceMode.Replay;
        RadarAppConfiguration? persisted = null;
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory, (candidate, _) =>
        {
            persisted = System.Text.Json.JsonSerializer.Deserialize<RadarAppConfiguration>(
                System.Text.Json.JsonSerializer.Serialize(candidate));
            return Task.CompletedTask;
        });
        await coordinator.ApplyUnityTopologyAsync(Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1)));
        var originalPipelines = factory.Created.ToArray();
        persisted = null;

        await coordinator.StartAllSimulationAsync();

        Assert.All(configuration.Screens.SelectMany(screen => screen.Sensors),
            sensor => Assert.Equal(RadarSensorSourceMode.Simulation, sensor.SourceMode));
        Assert.NotNull(persisted);
        Assert.All(persisted.Screens.SelectMany(screen => screen.Sensors),
            sensor => Assert.Equal(RadarSensorSourceMode.Simulation, sensor.SourceMode));
        var replacements = factory.Created.Except(originalPipelines).ToArray();
        Assert.Equal(3, replacements.Length);
        Assert.All(replacements, pipeline =>
        {
            Assert.Equal(RadarSensorSourceMode.Simulation, pipeline.SourceMode);
            Assert.Equal(1, pipeline.StartCallCount);
            Assert.Equal(RadarSensorRuntimeState.Running, pipeline.State);
        });
    }

    [Fact]
    public async Task Coordinator_StopAllSimulationStopsPendingReplacementPipelinesWithoutChangingSourceMode()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [ScreenConfiguration("front", "f1", 1920, 1080)]
        };
        configuration.Screens[0].Sensors[0].SourceMode = RadarSensorSourceMode.Real;
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        var original = factory["front", "f1"];
        await coordinator.StartAllSimulationAsync();
        var replacement = factory["front", "f1"];

        var stopMethod = typeof(RadarBridgeCoordinator).GetMethod("StopAllSimulationAsync", Type.EmptyTypes);
        Assert.NotNull(stopMethod);
        await Assert.IsAssignableFrom<Task>(stopMethod.Invoke(coordinator, null));

        Assert.NotSame(original, replacement);
        Assert.Equal(RadarSensorSourceMode.Simulation, configuration.Screens[0].Sensors[0].SourceMode);
        Assert.Equal(1, replacement.StopCallCount);
        Assert.Equal(RadarSensorRuntimeState.Stopped, replacement.State);
    }

    [Fact]
    public async Task Coordinator_ConnectScreenKeepsLaterSensorInTheSameLeaseBatchDuringRetirement()
    {
        var configuration = new RadarAppConfiguration
        {
            Screens = [new RadarScreenConfiguration
            {
                ScreenId = "front",
                UnityDisplayName = "Front",
                Sensors = [Sensor("f1"), Sensor("f2")]
            }]
        };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        factory["front", "f1"].BlockStart(ignoreCancellation: true);

        var connect = coordinator.ConnectScreenAsync("front");
        await factory["front", "f1"].StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(1));
        coordinator.TickForTest(DateTimeOffset.UnixEpoch.AddSeconds(2), waitForRetirement: false);

        factory["front", "f1"].ReleaseStart();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.Equal(1, factory["front", "f2"].StartCallCount);
    }

    [Fact]
    public async Task Coordinator_ConnectScreenLeaseFailureInvokesNoPipeline()
    {
        var configuration = new RadarAppConfiguration { Screens = [ScreenConfiguration("front", "f1", 1920, 1080)] };
        var factory = new FakePipelineFactory();
        await using var coordinator = CreateCoordinator(configuration, factory);
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("front", "Front", true, 1920, 1080, 0)));
        await coordinator.ApplyUnityTopologyAsync(Hello(Screen("left", "Left", true, 1920, 1080, 0)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ConnectScreenAsync("front"));
        Assert.Equal(0, factory["front", "f1"].StartCallCount);
    }

    [Fact]
    public async Task Coordinator_ReconnectsIpcAndPublishesCompleteBatchesAfterEachHello()
    {
        var factory = new FakePipelineFactory();
        var configuration = ThreeScreenFourSensorConfiguration();
        await using var coordinator = CreateCoordinator(configuration, factory);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await coordinator.StartInfrastructureAsync(cancellation.Token);

        await using (var first = await ConnectAsync(configuration.Ipc.PipeName, cancellation.Token))
        {
            var firstBatch = await ReadBatchAsync(first, cancellation.Token);
            Assert.Equal(["left", "front", "right"], firstBatch.Screens.Select(frame => frame.Screen.ScreenId));
        }

        await WaitUntilAsync(() => !coordinator.UnityStatus.IsConnected, cancellation.Token);
        await using var second = await ConnectAsync(configuration.Ipc.PipeName, cancellation.Token);
        var secondBatch = await ReadBatchAsync(second, cancellation.Token);

        Assert.Equal(["left", "front", "right"], secondBatch.Screens.Select(frame => frame.Screen.ScreenId));
        Assert.True(secondBatch.Screens[0].Sequence > 0);
        Assert.True(coordinator.UnityStatus.IsConnected);
    }

    private static RadarBridgeCoordinator CreateCoordinator(RadarAppConfiguration configuration, FakePipelineFactory factory, Func<RadarAppConfiguration, CancellationToken, Task>? persist = null, Func<PointerBatchPayload, CancellationToken, Task<bool>>? send = null)
    {
        configuration.Ipc.PipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        return new RadarBridgeCoordinator(configuration, NullLogger<RadarBridgeCoordinator>.Instance, factory, persistConfigurationAsync: persist, sendPointerBatchAsync: send);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellationToken);
        await IpcStream.WriteAsync(client, IpcEnvelope.Create(IpcMessageType.Hello, 1, Hello(
            Screen("left", "Left", false, 1920, 1440, 0),
            Screen("front", "Front", true, 4096, 1536, 1),
            Screen("right", "Right", false, 1920, 1440, 2))), cancellationToken);
        var acknowledgement = await IpcStream.ReadAsync(client, cancellationToken);
        Assert.Equal(IpcMessageType.HelloAck, acknowledgement.MessageType);
        var ack = acknowledgement.DeserializePayload<HelloAckPayload>();
        Assert.Equal("1.2.10", BridgeVersion.Value);
        Assert.Equal(BridgeVersion.Value, ack.BridgeVersion);
        Assert.Equal(["left", "front", "right"], ack.Screens.Select(screen => screen.ScreenId));
        return client;
    }

    private static async Task<PointerBatchPayload> ReadBatchAsync(Stream client, CancellationToken cancellationToken)
    {
        while (true)
        {
            var envelope = await IpcStream.ReadAsync(client, cancellationToken);
            if (envelope.MessageType == IpcMessageType.PointerBatch) return envelope.DeserializePayload<PointerBatchPayload>();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate()) await Task.Delay(10, cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, CancellationToken cancellationToken)
    {
        while (!await predicate()) await Task.Delay(10, cancellationToken);
    }

    private static RadarAppConfiguration ThreeScreenFourSensorConfiguration() => new()
    {
        Screens =
        [
            ScreenConfiguration("left", "l1", 1920, 1440),
            new RadarScreenConfiguration
            {
                ScreenId = "front", UnityDisplayName = "Front", ResolutionMode = RadarResolutionMode.Override,
                WidthPixels = 4096, HeightPixels = 1536, Sensors = [Sensor("f1"), Sensor("f2")],
                Tracking = new RadarScreenTrackingConfiguration { ConfirmFrames = 1 }
            },
            ScreenConfiguration("right", "r1", 1920, 1440)
        ]
    };

    private static RadarScreenConfiguration ScreenConfiguration(string screenId, string sensorId, int width, int height) => new()
    {
        ScreenId = screenId,
        UnityDisplayName = screenId,
        ResolutionMode = RadarResolutionMode.Override,
        WidthPixels = width,
        HeightPixels = height,
        Sensors = [Sensor(sensorId)],
        Tracking = new RadarScreenTrackingConfiguration { ConfirmFrames = 1 }
    };

    private static RadarSensorConfiguration Sensor(string sensorId) => new()
    {
        SensorId = sensorId,
        Enabled = true,
        SourceMode = RadarSensorSourceMode.Simulation
    };

    private static RadarScreenDefinitionPayload Screen(string id, string name, bool primary, int width, int height, int order) =>
        new(id, name, width, height, primary, order);

    private static HelloPayload Hello(params RadarScreenDefinitionPayload[] screens) => new(Environment.ProcessId, "2021.3", screens);

    private static SensorDetectionFrame Detection(string sensorId, float x, float y) =>
        new(sensorId, DateTimeOffset.UnixEpoch.AddMilliseconds(950), [new SensorDetection(1, x, y, 1f)]);

    private sealed class FakePipelineFactory : IRadarSensorPipelineFactory
    {
        private readonly Dictionary<(string ScreenId, string SensorId), FakePipeline> _pipelines = new();
        private readonly List<FakePipeline> _instances = [];
        public bool ThrowOnCreate { get; set; }
        public int ThrowOnCreateNumber { get; set; }
        public Action<string>? CreateObserved { get; set; }
        private int _createCalls;
        public bool AllDisposed => _pipelines.Values.All(value => value.Disposed);
        public IReadOnlyCollection<FakePipeline> Created => _pipelines.Values;
        public IReadOnlyCollection<FakePipeline> Instances => _instances;
        public List<string> StartOrder { get; } = [];

        public FakePipeline this[string screenId, string sensorId] => _pipelines[(screenId, sensorId)];
        public IReadOnlyList<FakePipeline> GetInstances(string screenId, string sensorId) => _instances
            .Where(value => string.Equals(value.ScreenId, screenId, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(value.SensorId, sensorId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        public IRadarSensorPipeline Create(RadarScreenConfiguration screen, RadarSensorConfiguration sensor)
        {
            if (ThrowOnCreate || ++_createCalls == ThrowOnCreateNumber) throw new InvalidOperationException("factory failure");
            CreateObserved?.Invoke(sensor.SensorId);
            var pipeline = new FakePipeline(screen.ScreenId, sensor.SensorId, sensor.SourceMode, value => StartOrder.Add(value));
            _pipelines[(screen.ScreenId, sensor.SensorId)] = pipeline;
            _instances.Add(pipeline);
            return pipeline;
        }
    }

    private sealed class DedicatedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = [];
        private readonly Thread _thread;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DedicatedSynchronizationContext()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "RadarControl test UI" };
            _thread.Start();
            _started.Task.GetAwaiter().GetResult();
        }

        public int ThreadId { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => _work.Add((d, state));

        public T Invoke<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { completion.SetResult(action()); }
                catch (Exception exception) { completion.SetException(exception); }
            }, null);
            return completion.Task.GetAwaiter().GetResult();
        }

        public void Invoke(Action action) => Invoke(() => { action(); return true; });

        public Task DrainAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ => completion.SetResult(), null);
            return completion.Task;
        }

        public void Dispose()
        {
            _work.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(2));
            _work.Dispose();
        }

        private void Run()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(this);
            _started.SetResult();
            foreach (var item in _work.GetConsumingEnumerable()) item.Callback(item.State);
        }
    }

    private sealed class FakePipeline(string screenId, string sensorId, RadarSensorSourceMode sourceMode, Action<string>? startObserved = null) : IRadarSensorPipeline
    {
        public string ScreenId { get; } = screenId;
        public string SensorId { get; } = sensorId;
        public RadarSensorSourceMode SourceMode { get; } = sourceMode;
        public RadarSensorRuntimeState State { get; private set; } = RadarSensorRuntimeState.Stopped;
        public long DroppedInputFrameCount => 0;
        public int StopCallCount { get; private set; }
        public int StartCallCount { get; private set; }
        public int DisposeCallCount { get; private set; }
        public bool Disposed { get; private set; }
        public bool DisposedDuringOperation { get; private set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RecordingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RecordingCallCount { get; private set; }
        private TaskCompletionSource? _startRelease;
        private TaskCompletionSource? _stopRelease;
        private TaskCompletionSource? _recordingRelease;
        private bool _ignoreStartCancellation;
        private bool _ignoreRecordingCancellation;
        public event Action<RadarSensorRuntimeSnapshot>? SnapshotUpdated;
        public event Action<SensorDetectionFrame>? DetectionFrameUpdated;
        public event Action<RadarSensorRuntimeState>? StateChanged;
        public event Action<string>? LogReceived;
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCallCount++;
            startObserved?.Invoke($"{ScreenId}/{SensorId}");
            StartEntered.TrySetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (_startRelease is not null)
            {
                if (_ignoreStartCancellation) await _startRelease.Task;
                else await _startRelease.Task.WaitAsync(cancellationToken);
            }
            DisposedDuringOperation |= Disposed;
            State = RadarSensorRuntimeState.Running;
            StateChanged?.Invoke(State);
        }
        public async Task StopAsync()
        {
            StopCallCount++;
            StopEntered.TrySetResult();
            if (_stopRelease is not null) await _stopRelease.Task;
            DisposedDuringOperation |= Disposed;
            State = RadarSensorRuntimeState.Stopped;
            StateChanged?.Invoke(State);
        }
        public async Task StartRecordingAsync(string path, CancellationToken cancellationToken = default)
        {
            RecordingCallCount++;
            RecordingEntered.TrySetResult();
            if (_recordingRelease is not null)
            {
                if (_ignoreRecordingCancellation) await _recordingRelease.Task;
                else await _recordingRelease.Task.WaitAsync(cancellationToken);
            }
            DisposedDuringOperation |= Disposed;
        }
        public Task StopRecordingAsync() => Task.CompletedTask;
        public Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PauseReplay() { }
        public void ResumeReplay() { }
        public void StepReplay() { }
        public Task StopReplayAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { DisposeCallCount++; Disposed = true; return ValueTask.CompletedTask; }
        public void BlockStart(bool ignoreCancellation = false) { _ignoreStartCancellation = ignoreCancellation; _startRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ReleaseStart() => _startRelease?.TrySetResult();
        public void BlockStop() => _stopRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseStop() => _stopRelease?.TrySetResult();
        public void BlockRecording(bool ignoreCancellation = false) { _ignoreRecordingCancellation = ignoreCancellation; _recordingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ReleaseRecording() => _recordingRelease?.TrySetResult();
        public void Publish(SensorDetectionFrame frame) => DetectionFrameUpdated?.Invoke(frame);
        public void Fault() { State = RadarSensorRuntimeState.Faulted; StateChanged?.Invoke(State); }
        public void SetStateWithoutPublishing(RadarSensorRuntimeState state) => State = state;
        public void PublishState(RadarSensorRuntimeState state) { State = state; StateChanged?.Invoke(state); }
        public Action<RadarSensorRuntimeState>? CaptureStateChangedSubscribers() => StateChanged;
        public void PublishStateTo(Action<RadarSensorRuntimeState>? handlers, RadarSensorRuntimeState state)
        {
            State = state;
            handlers?.Invoke(state);
        }
    }
}
