using System.Text.Json;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Threading.Tasks.Sources;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Ipc;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.Interaction.Runtime;

namespace Blaze.Interaction.Bridge.Wpf.Tests;

public sealed class BridgeHostTests
{
    private static readonly InteractionSurface Front = new()
    {
        SurfaceId = "FRONT",
        Name = "Front",
        LogicalWidth = 1920,
        LogicalHeight = 1080,
        IsPrimary = true,
        Order = 0
    };
    private static readonly InteractionSurface Back = Front with
    {
        SurfaceId = "BACK",
        Name = "Back"
    };

    [Fact]
    public void HostStatus_FreezesSurfaceSnapshotsAndPublishesOnlyRealChanges()
    {
        var status = new BridgeInteractionHostStatus();
        var published = new List<InteractionHostStatus>();
        status.Changed += published.Add;
        var surfaces = new[] { Front };

        status.ApplyConnected(Hello() with { Surfaces = surfaces });
        surfaces[0] = Back;
        status.ApplyConnected(Hello());

        Assert.Equal("FRONT", Assert.Single(status.Current.Surfaces).SurfaceId);
        Assert.Single(published);

        status.ApplyDisconnected();
        Assert.Equal(2, published.Count);
        Assert.False(status.Current.IsConnected);
    }

    [Fact]
    public async Task HostStatus_TerminalDisconnectCannotBeReversedByCapturedConnectedPublication()
    {
        var connectedPublicationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseConnectedPublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var status = new BridgeInteractionHostStatus(
            beforePublish: value =>
            {
                if (!value.IsConnected) return;
                connectedPublicationEntered.TrySetResult();
                releaseConnectedPublication.Task.GetAwaiter().GetResult();
            },
            beforeTerminate: () => terminalRequested.TrySetResult());
        var published = new List<bool>();
        status.Changed += value => published.Add(value.IsConnected);

        var connected = Task.Run(() => status.ApplyConnected(Hello()));
        await connectedPublicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var terminal = Task.Run(status.Terminate);
        await terminalRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseConnectedPublication.TrySetResult();
        await Task.WhenAll(connected, terminal).WaitAsync(TimeSpan.FromSeconds(5));

        status.ApplyConnected(Hello());
        Assert.Equal([true, false], published);
        Assert.False(status.Current.IsConnected);
    }

    [Fact]
    public void HostStatus_ReentrantTerminalPublicationPreservesOrderForEverySubscriber()
    {
        var status = new BridgeInteractionHostStatus();
        var first = new List<bool>();
        var second = new List<bool>();
        status.Changed += value =>
        {
            first.Add(value.IsConnected);
            if (value.IsConnected) status.Terminate();
        };
        status.Changed += value => second.Add(value.IsConnected);

        status.ApplyConnected(Hello());
        status.ApplyConnected(Hello());

        Assert.Equal([true, false], first);
        Assert.Equal([true, false], second);
        Assert.False(status.Current.IsConnected);
    }

    [Fact]
    public void HostStatus_FailsFastBeforeVersionWouldOverflow()
    {
        var status = new BridgeInteractionHostStatus(initialVersion: long.MaxValue);

        Assert.Throws<OverflowException>(() => status.ApplyConnected(Hello()));
        Assert.False(status.Current.IsConnected);
    }

    [Fact]
    public async Task HostStatus_HookFailureDoesNotStrandQueuedPublicationOrFutureDrain()
    {
        var firstHookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedHookCount = 0;
        var status = new BridgeInteractionHostStatus(beforePublish: value =>
        {
            if (!value.IsConnected || Interlocked.Increment(ref connectedHookCount) != 1) return;
            firstHookEntered.TrySetResult();
            releaseFirstHook.Task.GetAwaiter().GetResult();
            throw new InvalidOperationException("observer failed");
        });
        var published = new List<bool>();
        status.Changed += value => published.Add(value.IsConnected);

        var first = Task.Run(() => status.ApplyConnected(Hello()));
        await firstHookEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Task.Run(status.ApplyDisconnected);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirstHook.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        status.ApplyConnected(Hello());

        Assert.Equal([false, true], published);
        Assert.True(status.Current.IsConnected);
    }

    [Fact]
    public async Task Dispose_TerminateOverflowStillDisposesOwnedResources()
    {
        var status = new BridgeInteractionHostStatus(initialVersion: long.MaxValue - 1);
        status.ApplyConnected(Hello());
        var manager = new ProviderManager();
        var resource = new TrackingAsyncDisposable();
        await using var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = $"Blaze.InteractionBridge.DisposeOverflow.{Guid.NewGuid():N}"
        });
        var host = new BridgeHost(
            manager,
            new RecordingMessageSink(),
            _ => Task.CompletedTask,
            new RecordingParentProcessMonitor(),
            null,
            null,
            new Dictionary<string, ProviderDescriptor>(),
            [resource, server],
            services: new BridgeServiceProvider([status]));

        await Assert.ThrowsAsync<OverflowException>(async () => await host.DisposeAsync());

        Assert.True(status.Current.IsConnected);
        Assert.True(resource.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.RunAsync());
    }

    [Fact]
    public async Task Host_MapsOnlyAcknowledgedPipeLifecycleIntoProviderStatus()
    {
        var provider = new RecordingProvider("radar-main");
        var manager = new ProviderManager();
        manager.Register(Descriptor(), provider.ProviderInstanceId, () => provider);
        var status = new BridgeInteractionHostStatus();
        var pipeName = $"Blaze.InteractionBridge.HostStatus.{Guid.NewGuid():N}";
        BridgeHost? host = null;
        var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (hello, cancellationToken) => host!.HandleHelloAsync(hello, cancellationToken)
        });
        host = new BridgeHost(
            manager,
            new ServerMessageSink(server),
            server.RunAsync,
            new RecordingParentProcessMonitor(),
            null,
            provider.ProviderInstanceId,
            new Dictionary<string, ProviderDescriptor> { [provider.ProviderInstanceId] = Descriptor() },
            [server],
            services: new BridgeServiceProvider([status]));
        await using var ownedHost = host;
        using var stop = new CancellationTokenSource();
        var running = host.RunAsync(stop.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using (var client = new NamedPipeClientStream(
                         ".",
                         pipeName,
                         PipeDirection.InOut,
                         PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(timeout.Token);
            await InteractionIpcStream.WriteAsync(
                client,
                InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()),
                timeout.Token);
            Assert.Equal(InteractionMessageType.HelloAck,
                (await InteractionIpcStream.ReadAsync(client, timeout.Token)).MessageType);
            await WaitUntilAsync(() => status.Current.IsConnected, timeout.Token);
            Assert.Equal(Environment.ProcessId, status.Current.ProcessId);
        }

        await WaitUntilAsync(() => !status.Current.IsConnected, timeout.Token);
        stop.Cancel();
        await running.WaitAsync(timeout.Token);
    }

    [Fact]
    public void ProviderDiscovery_IsolatesInvalidManifestAndLoadsValidSibling()
    {
        using var providers = new ProviderTestDirectory();
        providers.AddBuiltValidProvider("Good");
        var bad = providers.AddEmptyProvider("Bad");
        File.WriteAllText(Path.Combine(bad, "provider.json"), "{ broken");

        using var discovery = BridgeProviderDiscovery.Discover(providers.Root);

        Assert.Equal(2, discovery.CatalogEntries.Count);
        Assert.Single(discovery.LoadResults, result => result.IsSuccess);
        Assert.Single(discovery.LoadResults, result => !result.IsSuccess);
        Assert.Equal("blaze.test.valid", discovery.LoadedProviders.Single().Manifest.Id);
    }

    [Fact]
    public void ProviderDiscovery_RejectsUnsupportedProviderManifest()
    {
        using var providers = new ProviderTestDirectory();
        var directory = providers.AddEmptyProvider("FutureApi");
        ProviderTestDirectory.WriteManifest(directory, providerApiVersion: 99);

        using var discovery = BridgeProviderDiscovery.Discover(providers.Root);

        var entry = Assert.Single(discovery.CatalogEntries);
        Assert.Equal(ProviderCatalogIssue.UnsupportedApiVersion, entry.Issue);
        Assert.Empty(discovery.LoadedProviders);
    }

    [Fact]
    public void Create_PipeConstructionFailureDisposesPrefetchedProviderAndReleasesLoadContext()
    {
        using var providers = new ProviderTestDirectory();
        providers.AddBuiltValidProvider("Good");

        var references = FailCreateAndCaptureReleasedResources(providers.Root);
        CollectUntilDead(references.Provider, references.LoadContext);

        Assert.False(references.Provider.IsAlive);
        Assert.False(references.LoadContext.IsAlive);
    }

    [Fact]
    public void Dispose_NormalHostLifetimeReleasesProviderAndLoadContext()
    {
        using var providers = new ProviderTestDirectory();
        providers.AddBuiltValidProvider("Good");

        var references = CreateDisposeAndCaptureReleasedResources(providers.Root);
        CollectUntilDead(references.Provider, references.LoadContext);

        Assert.False(references.Provider.IsAlive);
        Assert.False(references.LoadContext.IsAlive);
    }

    [Fact]
    public void PrefetchedFactory_DisposesCreatedProviderWhenInstanceIdentityThrows()
    {
        var provider = new ThrowingIdentityProvider();

        Assert.Throws<InvalidOperationException>(() => new PrefetchedProviderFactory(
            new RecordingPlugin(Descriptor(), provider),
            new ProviderCreateContext(Path.GetTempPath(), EmptyServices.Instance)));

        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task RegistrationFailureImmediatelyDisposesFactoryAndReportsDiagnostic()
    {
        var manager = new ProviderManager();
        var descriptors = new Dictionary<string, ProviderDescriptor>(StringComparer.Ordinal);
        var diagnostics = new List<BridgeStartupDiagnostic>();
        var firstProvider = new RecordingProvider("shared-instance");
        var secondProvider = new RecordingProvider("shared-instance");
        var first = BridgeProviderRegistrar.TryRegister(
            manager,
            new RecordingPlugin(Descriptor("blaze.first"), firstProvider),
            new ProviderCreateContext(Path.GetTempPath(), EmptyServices.Instance),
            descriptors,
            diagnostics);
        var second = BridgeProviderRegistrar.TryRegister(
            manager,
            new RecordingPlugin(Descriptor("blaze.second"), secondProvider),
            new ProviderCreateContext(Path.GetTempPath(), EmptyServices.Instance),
            descriptors,
            diagnostics);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(1, secondProvider.DisposeCount);
        Assert.Contains(diagnostics, item => item.Stage == "RegisterProvider");

        await first.DisposeAsync();
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task StartupDiagnosticsExposeProviderLoadFailures()
    {
        using var providers = new ProviderTestDirectory();
        var bad = providers.AddEmptyProvider("Bad");
        File.WriteAllText(Path.Combine(bad, "provider.json"), "{ broken");
        await using var host = BridgeHost.Create(new BridgeHostOptions
        {
            ProvidersRoot = providers.Root,
            DataRoot = Path.Combine(providers.Root, "data"),
            PipeName = $"Blaze.InteractionBridge.Diagnostics.{Guid.NewGuid():N}"
        });

        var diagnostic = Assert.Single(host.StartupDiagnostics);
        Assert.Equal("LoadProvider", diagnostic.Stage);
        Assert.Contains("invalid", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateProviderFailureIsIsolatedAndReported()
    {
        var manager = new ProviderManager();
        var diagnostics = new List<BridgeStartupDiagnostic>();
        var factory = BridgeProviderRegistrar.TryRegister(
            manager,
            new ThrowingPlugin(),
            new ProviderCreateContext(Path.GetTempPath(), EmptyServices.Instance),
            new Dictionary<string, ProviderDescriptor>(),
            diagnostics);

        Assert.Null(factory);
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("CreateProvider", diagnostic.Stage);
        Assert.Contains("activation", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        await manager.DisposeAsync();
    }

    [Fact]
    public void AppCleanupFailure_IsWrittenToTraceDiagnostics()
    {
        using var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            BridgeAppDiagnostics.ReportCleanupFailure(new IOException("cleanup-failed"));
            Trace.Flush();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        Assert.Contains("cleanup-failed", listener.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelloTopology_ReachesProviderInitializationBeforeProviderStartAndAck()
    {
        var provider = new RecordingProvider("radar-main");
        await using var fixture = BridgeFixture.Create(provider);

        var acknowledgement = await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);

        Assert.Equal(["initialize:FRONT", "start"], provider.Operations);
        Assert.Equal("blaze.test", acknowledgement.ActiveProvider!.Id);
        Assert.Equal("radar-main", acknowledgement.ActiveProvider.InstanceId);
    }

    [Fact]
    public async Task ProviderFrame_IsForwardedToInteractionPipe()
    {
        var provider = new RecordingProvider("radar-main");
        await using var fixture = BridgeFixture.Create(provider);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);

        provider.Emit(Frame("radar-main", 1, InteractionPhase.Move));
        var published = await fixture.Sink.NextAsync();

        Assert.False(published.Reliable);
        Assert.Equal(InteractionMessageType.InteractionFrame, published.Message.MessageType);
        Assert.Equal(1, published.Message.DeserializePayload<InteractionFrame>().Sequence);
    }

    [Theory]
    [InlineData(InteractionPhase.Down)]
    [InlineData(InteractionPhase.Up)]
    [InlineData(InteractionPhase.Cancel)]
    public async Task ProviderLifecycleEdges_AreForwardedReliablyInOrder(InteractionPhase phase)
    {
        var provider = new RecordingProvider("radar-main");
        await using var fixture = BridgeFixture.Create(provider);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);

        provider.Emit(Frame("radar-main", 1, phase));
        var published = await fixture.Sink.NextAsync();

        Assert.True(published.Reliable);
        Assert.Equal(phase, Assert.Single(
            published.Message.DeserializePayload<InteractionFrame>().Points).Phase);
    }

    [Fact]
    public async Task ProviderSwitch_SendsPointCancelBeforeProviderChanged()
    {
        var radar = new RecordingProvider("radar-main");
        var alternate = new RecordingProvider("alternate-main");
        await using var fixture = BridgeFixture.Create(radar, alternate);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        radar.Emit(Frame("radar-main", 1, InteractionPhase.Down));
        _ = await fixture.Sink.NextAsync();

        await fixture.Host.SwitchProviderAsync("alternate-main", CancellationToken.None);
        var cancellation = await fixture.Sink.NextAsync();
        var changed = await fixture.Sink.NextAsync();

        Assert.True(cancellation.Reliable);
        Assert.Equal(InteractionMessageType.InteractionFrame, cancellation.Message.MessageType);
        Assert.All(
            cancellation.Message.DeserializePayload<InteractionFrame>().Points,
            point => Assert.Equal(InteractionPhase.Cancel, point.Phase));
        Assert.False(changed.Reliable);
        Assert.Equal(InteractionMessageType.ProviderChanged, changed.Message.MessageType);
    }

    [Fact]
    public async Task ProviderSwitch_DoesNotInvokeProviderChangedUntilReliableCancelCompletes()
    {
        var radar = new RecordingProvider("radar-main");
        var alternate = new RecordingProvider("alternate-main");
        await using var fixture = BridgeFixture.Create(radar, alternate);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        radar.Emit(Frame("radar-main", 1, InteractionPhase.Down));
        _ = await fixture.Sink.NextAsync();
        fixture.Sink.BlockReliableSend();

        var switching = fixture.Host.SwitchProviderAsync("alternate-main", CancellationToken.None);
        await fixture.Sink.ReliableSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, fixture.Sink.ProviderChangedCalls);
        Assert.False(switching.IsCompleted);

        fixture.Sink.ReleaseReliableSend();
        await switching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Sink.ProviderChangedCalls);
        Assert.True((await fixture.Sink.NextAsync()).Reliable);
        Assert.Equal(
            InteractionMessageType.ProviderChanged,
            (await fixture.Sink.NextAsync()).Message.MessageType);
    }

    [Fact]
    public async Task Dispose_WaitsForPendingReliableCancelBeforeCompleting()
    {
        var radar = new RecordingProvider("radar-main");
        var alternate = new RecordingProvider("alternate-main");
        await using var fixture = BridgeFixture.Create(radar, alternate);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        radar.Emit(Frame("radar-main", 1, InteractionPhase.Down));
        _ = await fixture.Sink.NextAsync();
        fixture.Sink.BlockReliableSend();
        var switching = fixture.Host.SwitchProviderAsync("alternate-main", CancellationToken.None);
        await fixture.Sink.ReliableSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposal = fixture.Host.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        fixture.Sink.ReleaseReliableSend();
        await Task.WhenAll(switching, disposal).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Dispose_PublishesActivePointCancelReliablyBeforeCompleting()
    {
        var provider = new RecordingProvider("radar-main");
        await using var fixture = BridgeFixture.Create(provider);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        provider.Emit(Frame("radar-main", 1, InteractionPhase.Down));
        _ = await fixture.Sink.NextAsync();

        var disposal = fixture.Host.DisposeAsync().AsTask();
        var first = await Task.WhenAny(fixture.Sink.ReliableSendStarted.Task, disposal);

        Assert.Same(fixture.Sink.ReliableSendStarted.Task, first);
        var cancellation = await fixture.Sink.NextAsync();
        Assert.True(cancellation.Reliable);
        Assert.All(
            cancellation.Message.DeserializePayload<InteractionFrame>().Points,
            point => Assert.Equal(InteractionPhase.Cancel, point.Phase));
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisconnectedSink_DisposeSkipsUndeliverableCancel()
    {
        var provider = new RecordingProvider("radar-main");
        await using var fixture = BridgeFixture.Create(provider);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        provider.Emit(Frame("radar-main", 1, InteractionPhase.Down));
        _ = await fixture.Sink.NextAsync();
        var reliableCallsBeforeDisconnect = fixture.Sink.ReliableSendCalls;
        fixture.Sink.IsConnected = false;

        await fixture.Host.DisposeAsync();

        Assert.Equal(reliableCallsBeforeDisconnect, fixture.Sink.ReliableSendCalls);
    }

    [Fact]
    public async Task Dispose_CancelsStalledReliableCancelWithinBoundAndReportsFailure()
    {
        var provider = new RecordingProvider("radar-main");
        await using var fixture = BridgeFixture.Create(provider);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        provider.Emit(Frame("radar-main", 1, InteractionPhase.Down));
        _ = await fixture.Sink.NextAsync();
        fixture.Sink.BlockReliableSend();
        var elapsed = Stopwatch.StartNew();

        var disposal = fixture.Host.DisposeAsync().AsTask();
        await fixture.Sink.ReliableSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => disposal);
        elapsed.Stop();

        Assert.InRange(elapsed.Elapsed, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task NormalFrameFlood_DoesNotQueueBehindBlockedReliableControlChain()
    {
        var radar = new RecordingProvider("radar-main");
        var alternate = new RecordingProvider("alternate-main");
        await using var fixture = BridgeFixture.Create(radar, alternate);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        fixture.Sink.BlockNormalPublish();

        for (var sequence = 1; sequence <= 100; sequence++)
        {
            radar.Emit(Frame("radar-main", sequence, InteractionPhase.Move));
        }

        Assert.Equal(100, fixture.Sink.NormalPublishCalls);
        fixture.Sink.BlockReliableSend();
        radar.Emit(Frame("radar-main", 101, InteractionPhase.Down));
        var switching = fixture.Host.SwitchProviderAsync("alternate-main", CancellationToken.None);
        await fixture.Sink.ReliableSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, fixture.Sink.NormalPublishCalls);

        fixture.Sink.ReleaseNormalPublish();
        fixture.Sink.ReleaseReliableSend();
        await switching.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(
            [InteractionPhase.Down, InteractionPhase.Cancel],
            fixture.Sink.ReliablePhases);
    }

    [Fact]
    public async Task NormalFrameFlood_DoesNotRegisterPerFrameCompletionContinuations()
    {
        var provider = new RecordingProvider("radar-main");
        var manager = new ProviderManager();
        manager.Register(Descriptor(), provider.ProviderInstanceId, () => provider);
        var sink = new PendingNormalMessageSink();
        await using var host = new BridgeHost(
            manager,
            sink,
            _ => Task.CompletedTask,
            new RecordingParentProcessMonitor(),
            null,
            provider.ProviderInstanceId,
            new Dictionary<string, ProviderDescriptor>
            {
                [provider.ProviderInstanceId] = Descriptor()
            },
            []);
        await host.HandleHelloAsync(Hello(), CancellationToken.None);

        for (var sequence = 1; sequence <= 100; sequence++)
        {
            provider.Emit(Frame("radar-main", sequence, InteractionPhase.Move));
        }

        Assert.Equal(100, sink.NormalPublishCalls);
        Assert.Equal(0, sink.CompletionRegistrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReliableCancel_DoesNotInvokeProviderChangedOrReportSwitchFlushSuccess(
        bool throwFromReliableSend)
    {
        var radar = new RecordingProvider("radar-main");
        var alternate = new RecordingProvider("alternate-main");
        await using var fixture = BridgeFixture.Create(radar, alternate);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        radar.Emit(Frame("radar-main", 1, InteractionPhase.Down));
        _ = await fixture.Sink.NextAsync();
        fixture.Sink.ReliableSendResult = false;
        fixture.Sink.ThrowFromReliableSend = throwFromReliableSend;

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Host.SwitchProviderAsync("alternate-main", CancellationToken.None));

        Assert.Equal(0, fixture.Sink.ProviderChangedCalls);
    }

    [Fact]
    public async Task DisconnectedSink_SwitchSkipsUndeliverableCancelAndProviderChanged()
    {
        var radar = new RecordingProvider("radar-main");
        var alternate = new RecordingProvider("alternate-main");
        await using var fixture = BridgeFixture.Create(radar, alternate);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);
        radar.Emit(Frame("radar-main", 1, InteractionPhase.Down));
        _ = await fixture.Sink.NextAsync();
        var reliableCallsBeforeDisconnect = fixture.Sink.ReliableSendCalls;
        fixture.Sink.IsConnected = false;

        await fixture.Host.SwitchProviderAsync("alternate-main", CancellationToken.None);

        Assert.Equal(reliableCallsBeforeDisconnect, fixture.Sink.ReliableSendCalls);
        Assert.Equal(0, fixture.Sink.ProviderChangedCalls);
    }

    [Fact]
    public async Task ChangedHelloTopology_RestartsActiveProviderBeforeAcknowledgingNewTopology()
    {
        var provider = new RecordingProvider("radar-main");
        await using var fixture = BridgeFixture.Create(provider);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);

        var acknowledgement = await fixture.Host.HandleHelloAsync(
            Hello() with { Surfaces = [Back] },
            CancellationToken.None);

        Assert.Equal(
            ["initialize:FRONT", "start", "stop", "dispose", "initialize:BACK", "start"],
            provider.Operations);
        Assert.Equal("radar-main", acknowledgement.ActiveProvider!.InstanceId);
    }

    [Fact]
    public async Task IdenticalHelloTopology_DoesNotRestartActiveProvider()
    {
        var provider = new RecordingProvider("radar-main");
        await using var fixture = BridgeFixture.Create(provider);
        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);

        await fixture.Host.HandleHelloAsync(Hello(), CancellationToken.None);

        Assert.Equal(["initialize:FRONT", "start"], provider.Operations);
    }

    [Fact]
    public async Task ParentPidMode_StopsBridgeWhenParentExits()
    {
        var parent = new RecordingParentProcessMonitor();
        await using var fixture = BridgeFixture.Create(
            new RecordingProvider("radar-main"),
            parentProcessId: 31415,
            parentMonitor: parent);

        var running = fixture.Host.RunAsync(CancellationToken.None);
        Assert.Equal(31415, await parent.WaitedPid.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        parent.Exit();

        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Server.WasCancelled);
    }

    [Fact]
    public async Task ManualMode_RemainsRunningAfterUnityClientDisconnects()
    {
        var provider = new RecordingProvider("radar-main");
        var manager = new ProviderManager();
        manager.Register(Descriptor(), provider.ProviderInstanceId, () => provider);
        var pipeName = $"Blaze.InteractionBridge.Manual.{Guid.NewGuid():N}";
        BridgeHost? host = null;
        var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (hello, cancellationToken) =>
                host!.HandleHelloAsync(hello, cancellationToken)
        });
        host = new BridgeHost(
            manager,
            new ServerMessageSink(server),
            server.RunAsync,
            new RecordingParentProcessMonitor(),
            null,
            provider.ProviderInstanceId,
            new Dictionary<string, ProviderDescriptor>
            {
                [provider.ProviderInstanceId] = Descriptor()
            },
            [server]);
        await using var ownedHost = host;
        using var stop = new CancellationTokenSource();
        var running = host.RunAsync(stop.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var client = new NamedPipeClientStream(
                         ".",
                         pipeName,
                         PipeDirection.InOut,
                         PipeOptions.Asynchronous))
        {
            await client.ConnectAsync(timeout.Token);
            await InteractionIpcStream.WriteAsync(
                client,
                InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()),
                timeout.Token);
            Assert.Equal(
                InteractionMessageType.HelloAck,
                (await InteractionIpcStream.ReadAsync(client, timeout.Token)).MessageType);
        }

        await WaitUntilAsync(() => !server.IsClientConnected, timeout.Token);

        Assert.False(running.IsCompleted);
        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconnectedClientWithChangedTopology_RestartsProviderBeforeSecondAcknowledgement()
    {
        var provider = new RecordingProvider("radar-main");
        var manager = new ProviderManager();
        manager.Register(Descriptor(), provider.ProviderInstanceId, () => provider);
        var pipeName = $"Blaze.InteractionBridge.Reconnect.{Guid.NewGuid():N}";
        BridgeHost? host = null;
        var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (hello, cancellationToken) =>
                host!.HandleHelloAsync(hello, cancellationToken)
        });
        host = new BridgeHost(
            manager,
            new ServerMessageSink(server),
            server.RunAsync,
            new RecordingParentProcessMonitor(),
            null,
            provider.ProviderInstanceId,
            new Dictionary<string, ProviderDescriptor>
            {
                [provider.ProviderInstanceId] = Descriptor()
            },
            [server]);
        await using var ownedHost = host;
        using var stop = new CancellationTokenSource();
        var running = host.RunAsync(stop.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await ConnectAndExchangeHelloAsync(pipeName, Hello(), timeout.Token);
        await WaitUntilAsync(() => !server.IsClientConnected, timeout.Token);
        var acknowledgement = await ConnectAndExchangeHelloAsync(
            pipeName,
            Hello() with { Surfaces = [Back] },
            timeout.Token);

        Assert.Equal("radar-main", acknowledgement.ActiveProvider!.InstanceId);
        Assert.Equal(
            ["initialize:FRONT", "start", "stop", "dispose", "initialize:BACK", "start"],
            provider.Operations);

        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconnectedLargeChangedTopology_AcksWithoutStaleProviderChanged()
    {
        var provider = new RecordingProvider("radar-main");
        var manager = new ProviderManager();
        manager.Register(Descriptor(), provider.ProviderInstanceId, () => provider);
        var pipeName = $"Blaze.InteractionBridge.LargeTopology.{Guid.NewGuid():N}";
        BridgeHost? host = null;
        var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (hello, cancellationToken) =>
                host!.HandleHelloAsync(hello, cancellationToken)
        });
        host = new BridgeHost(
            manager,
            new ServerMessageSink(server),
            server.RunAsync,
            new RecordingParentProcessMonitor(),
            null,
            provider.ProviderInstanceId,
            new Dictionary<string, ProviderDescriptor>
            {
                [provider.ProviderInstanceId] = Descriptor()
            },
            [server]);
        await using var ownedHost = host;
        using var stop = new CancellationTokenSource();
        var running = host.RunAsync(stop.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var oldTopology = Enumerable.Range(0, 65)
            .Select(index => Front with
            {
                SurfaceId = $"OLD-{index}",
                Name = $"Old {index}",
                IsPrimary = index == 0,
                Order = index
            })
            .ToArray();

        await ConnectAndExchangeHelloAsync(
            pipeName,
            Hello() with { Surfaces = oldTopology },
            timeout.Token);
        await WaitUntilAsync(() => !server.IsClientConnected, timeout.Token);
        for (var index = 0; index < oldTopology.Length; index++)
        {
            provider.Emit(Frame(
                "radar-main",
                oldTopology[index].SurfaceId,
                1,
                InteractionPhase.Down));
        }

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await InteractionIpcStream.WriteAsync(
            client,
            InteractionEnvelope.Create(
                InteractionMessageType.Hello,
                2,
                Hello() with { Surfaces = [Back] }),
            timeout.Token);

        var acknowledgement = await InteractionIpcStream.ReadAsync(client, timeout.Token);
        Assert.Equal(InteractionMessageType.HelloAck, acknowledgement.MessageType);
        Assert.Equal(
            "radar-main",
            acknowledgement.DeserializePayload<HelloAckPayload>().ActiveProvider!.InstanceId);
        provider.Emit(Frame("radar-main", "BACK", 1, InteractionPhase.Down));
        while (true)
        {
            var message = await InteractionIpcStream.ReadAsync(client, timeout.Token);
            if (message.MessageType == InteractionMessageType.ProviderChanged)
            {
                var changed = message.DeserializePayload<ProviderChangedPayload>();
                Assert.False(
                    changed.PreviousProvider is { InstanceId: "radar-main" } &&
                    changed.ActiveProvider is null,
                    "The new client received the old session's inactive ProviderChanged after an active-provider Ack.");
            }

            if (message.MessageType == InteractionMessageType.InteractionFrame)
            {
                var frame = message.DeserializePayload<InteractionFrame>();
                if (frame.SurfaceId == "BACK" && frame.Sequence == 1)
                {
                    break;
                }
            }
        }

        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReconnectedChangedTopology_DiscardsOldFramePublishedDuringHelloRestart()
    {
        var provider = new RecordingProvider("radar-main");
        var manager = new ProviderManager();
        manager.Register(Descriptor(), provider.ProviderInstanceId, () => provider);
        var pipeName = $"Blaze.InteractionBridge.StagedFrame.{Guid.NewGuid():N}";
        BridgeHost? host = null;
        var acknowledgementCalls = 0;
        var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (hello, cancellationToken) =>
            {
                if (Interlocked.Increment(ref acknowledgementCalls) == 2)
                {
                    provider.Emit(Frame("radar-main", "FRONT", 90, InteractionPhase.Move));
                }

                return host!.HandleHelloAsync(hello, cancellationToken);
            }
        });
        host = new BridgeHost(
            manager,
            new ServerMessageSink(server),
            server.RunAsync,
            new RecordingParentProcessMonitor(),
            null,
            provider.ProviderInstanceId,
            new Dictionary<string, ProviderDescriptor>
            {
                [provider.ProviderInstanceId] = Descriptor()
            },
            [server]);
        await using var ownedHost = host;
        using var stop = new CancellationTokenSource();
        var running = host.RunAsync(stop.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await ConnectAndExchangeHelloAsync(pipeName, Hello(), timeout.Token);
        await WaitUntilAsync(() => !server.IsClientConnected, timeout.Token);

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await InteractionIpcStream.WriteAsync(
            client,
            InteractionEnvelope.Create(
                InteractionMessageType.Hello,
                2,
                Hello() with { Surfaces = [Back] }),
            timeout.Token);
        Assert.Equal(
            InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(client, timeout.Token)).MessageType);

        for (var sequence = 1; sequence <= 9; sequence++)
        {
            Assert.True(await server.SendAsync(
                InteractionEnvelope.Create(
                    InteractionMessageType.Status,
                    sequence,
                    new StatusPayload("marker", "Ready", "Ready", null, sequence)),
                timeout.Token));
        }

        var markersRead = 0;
        while (markersRead < 9)
        {
            var message = await InteractionIpcStream.ReadAsync(client, timeout.Token);
            if (message.MessageType == InteractionMessageType.Status)
            {
                markersRead++;
            }
            else if (message.MessageType == InteractionMessageType.InteractionFrame)
            {
                var frame = message.DeserializePayload<InteractionFrame>();
                Assert.False(
                    frame.SurfaceId == "FRONT" && frame.Sequence == 90,
                    "A frame from the old topology escaped after the new HelloAck.");
            }
        }

        provider.Emit(Frame("radar-main", "BACK", 91, InteractionPhase.Down));
        while (true)
        {
            var message = await InteractionIpcStream.ReadAsync(client, timeout.Token);
            if (message.MessageType != InteractionMessageType.InteractionFrame)
            {
                continue;
            }

            var frame = message.DeserializePayload<InteractionFrame>();
            if (frame.SurfaceId == "BACK" && frame.Sequence == 91)
            {
                break;
            }
        }

        stop.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(new[] { "--data-root", "C:\\Blaze\\Project" }, null, false)]
    [InlineData(new[] { "--data-root", "C:\\Blaze\\Project", "--parent-pid", "42", "--minimized" }, 42, true)]
    public void CommandLine_ParsesManualAndUnityLaunchModes(
        string[] arguments,
        int? expectedParentPid,
        bool expectedMinimized)
    {
        var options = BridgeCommandLine.Parse(arguments);

        Assert.Equal(expectedParentPid, options.ParentProcessId);
        Assert.Equal(expectedMinimized, options.Minimized);
    }

    [Fact]
    public void CommandLine_AcceptsAnIsolatedInteractionPipeForSmokeDiagnostics()
    {
        var options = BridgeCommandLine.Parse(
            ["--data-root", Path.GetTempPath(), "--pipe-name", "Blaze.InteractionBridge.Test.42"]);

        Assert.Equal("Blaze.InteractionBridge.Test.42", options.PipeName);
    }

    [Fact]
    public void CommandLine_ParsesProjectDataRootAndProfileOverride()
    {
        var options = BridgeCommandLine.Parse(
        [
            "--data-root", @"E:\Unity\Game\Library\BlazeInteraction",
            "--profile", @"E:\Unity\Game\Radar\custom.json"
        ]);

        Assert.Equal(Path.GetFullPath(@"E:\Unity\Game\Library\BlazeInteraction"), options.DataRoot);
        Assert.Equal(Path.GetFullPath(@"E:\Unity\Game\Radar\custom.json"), options.ProfilePath);
    }

    [Fact]
    public void CommandLine_RejectsMissingProjectDataRoot()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            BridgeCommandLine.Parse(["--pipe-name", "Blaze.InteractionBridge.Test.42"]));

        Assert.Contains("--data-root", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderStorageContext_UsesProjectScopedProviderDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "Blaze", Guid.NewGuid().ToString("N"));
        var storage = new BridgeProviderStorageContext(root, null);

        Assert.Equal(Path.GetFullPath(root), storage.DataRoot);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(root), "Providers", "blaze.radar.f10f20"),
            storage.GetProviderDataDirectory("blaze.radar.f10f20"));
        Assert.Throws<ArgumentException>(() => storage.GetProviderDataDirectory("../outside"));
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("radar.")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("nul.txt")]
    public void ProviderStorageContext_RejectsProviderPathAliases(string providerId)
    {
        var storage = new BridgeProviderStorageContext(Path.GetTempPath(), null);

        Assert.Throws<ArgumentException>(() => storage.GetProviderDataDirectory(providerId));
    }

    [Fact]
    public void ProviderServiceProvider_ReturnsTheExactRegisteredStorageInstance()
    {
        var storage = new BridgeProviderStorageContext(Path.GetTempPath(), null);
        var services = new BridgeServiceProvider([storage]);

        Assert.Same(storage, services.GetService(typeof(IProviderStorageContext)));
        Assert.Null(services.GetService(typeof(IDisposable)));
    }

    private static HelloPayload Hello() => new(
        Environment.ProcessId,
        "2021.3.45f1",
        "1.0.0",
        [Front]);

    private static async Task<HelloAckPayload> ConnectAndExchangeHelloAsync(
        string pipeName,
        HelloPayload hello,
        CancellationToken cancellationToken)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellationToken);
        await InteractionIpcStream.WriteAsync(
            client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1, hello),
            cancellationToken);
        var acknowledgement = await InteractionIpcStream.ReadAsync(client, cancellationToken);
        Assert.Equal(InteractionMessageType.HelloAck, acknowledgement.MessageType);
        return acknowledgement.DeserializePayload<HelloAckPayload>();
    }

    private static InteractionFrame Frame(string instanceId, long sequence, InteractionPhase phase) =>
        Frame(instanceId, "FRONT", sequence, phase);

    private static InteractionFrame Frame(
        string instanceId,
        string surfaceId,
        long sequence,
        InteractionPhase phase) =>
        new()
        {
            ProviderId = "blaze.test",
            ProviderInstanceId = instanceId,
            SurfaceId = surfaceId,
            Sequence = sequence,
            TimestampUnixMs = 1000 + sequence,
            Points =
            [
                new InteractionPoint
                {
                    Id = 7,
                    SurfaceId = surfaceId,
                    ProviderId = "blaze.test",
                    ProviderInstanceId = instanceId,
                    SourceId = "sensor-1",
                    Phase = phase,
                    NormalizedPosition = new Vector2Data(0.25f, 0.5f),
                    PixelPosition = new Vector2Data(480, 540),
                    Confidence = 1,
                    TimestampUnixMs = 1000 + sequence
                }
            ]
        };

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class BridgeFixture : IAsyncDisposable
    {
        private BridgeFixture(
            BridgeHost host,
            RecordingMessageSink sink,
            RecordingServer server)
        {
            Host = host;
            Sink = sink;
            Server = server;
        }

        internal BridgeHost Host { get; }
        internal RecordingMessageSink Sink { get; }
        internal RecordingServer Server { get; }

        internal static BridgeFixture Create(
            RecordingProvider first,
            RecordingProvider? second = null,
            int? parentProcessId = null,
            IParentProcessMonitor? parentMonitor = null)
        {
            var manager = new ProviderManager();
            manager.Register(Descriptor(), first.ProviderInstanceId, () => first);
            var descriptors = new Dictionary<string, ProviderDescriptor>(StringComparer.Ordinal)
            {
                [first.ProviderInstanceId] = Descriptor()
            };
            if (second is not null)
            {
                manager.Register(Descriptor(), second.ProviderInstanceId, () => second);
                descriptors.Add(second.ProviderInstanceId, Descriptor());
            }

            var sink = new RecordingMessageSink();
            var server = new RecordingServer();
            var host = new BridgeHost(
                manager,
                sink,
                server.RunAsync,
                parentMonitor ?? new RecordingParentProcessMonitor(),
                parentProcessId,
                first.ProviderInstanceId,
                descriptors,
                []);
            return new BridgeFixture(host, sink, server);
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private sealed class RecordingProvider(string instanceId) : IInteractionProvider
    {
        public string ProviderInstanceId { get; } = instanceId;
        public ProviderRuntimeStatus Status { get; private set; }
        public List<string> Operations { get; } = [];
        public int DisposeCount { get; private set; }
        public event EventHandler<InteractionFrameEventArgs>? FrameReceived;
        public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(ProviderInitializationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add($"initialize:{string.Join(',', context.Surfaces.Select(surface => surface.SurfaceId))}");
            Status = ProviderRuntimeStatus.Ready;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add("start");
            Status = ProviderRuntimeStatus.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add("stop");
            Status = ProviderRuntimeStatus.Stopped;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            Operations.Add("dispose");
            Status = ProviderRuntimeStatus.Stopped;
            return ValueTask.CompletedTask;
        }

        internal void Emit(InteractionFrame frame) =>
            FrameReceived?.Invoke(this, new InteractionFrameEventArgs(frame));
    }

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingMessageSink : IBridgeMessageSink
    {
        private readonly Queue<RecordedSend> _messages = new();
        private readonly SemaphoreSlim _available = new(0);
        private TaskCompletionSource? _reliableRelease;
        private TaskCompletionSource? _normalRelease;

        internal TaskCompletionSource ReliableSendStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ProviderChangedCalls { get; private set; }
        internal int NormalPublishCalls { get; private set; }
        internal int ReliableSendCalls { get; private set; }
        internal List<InteractionPhase> ReliablePhases { get; } = [];
        internal bool ReliableSendResult { get; set; } = true;
        internal bool ThrowFromReliableSend { get; set; }
        public bool IsConnected { get; set; } = true;
        public bool IsAcknowledged => IsConnected;

        public async ValueTask<bool> PublishFrameAsync(
            InteractionFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalPublishCalls++;
            var release = Volatile.Read(ref _normalRelease);
            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            lock (_messages)
            {
                _messages.Enqueue(new RecordedSend(
                    InteractionEnvelope.Create(
                        InteractionMessageType.InteractionFrame,
                        frame.Sequence,
                        frame),
                    Reliable: false));
            }

            _available.Release();
            return true;
        }

        public bool DiscardStagedLatestFrame(string providerId, string providerInstanceId) => false;

        public async ValueTask<bool> SendReliableFrameAsync(
            InteractionFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReliableSendCalls++;
            lock (ReliablePhases)
            {
                ReliablePhases.Add(frame.Points[0].Phase);
            }
            ReliableSendStarted.TrySetResult();
            var release = Volatile.Read(ref _reliableRelease);
            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            if (ThrowFromReliableSend)
            {
                throw new IOException("Reliable send failed.");
            }

            if (!ReliableSendResult)
            {
                return false;
            }

            lock (_messages)
            {
                _messages.Enqueue(new RecordedSend(
                    InteractionEnvelope.Create(
                        InteractionMessageType.InteractionFrame,
                        frame.Sequence,
                        frame),
                    Reliable: true));
            }

            _available.Release();
            return true;
        }

        public ValueTask<bool> SendAsync(InteractionEnvelope message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.MessageType == InteractionMessageType.ProviderChanged)
            {
                ProviderChangedCalls++;
            }

            lock (_messages)
            {
                _messages.Enqueue(new RecordedSend(message, Reliable: false));
            }

            _available.Release();
            return ValueTask.FromResult(true);
        }

        internal void BlockReliableSend() =>
            _reliableRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseReliableSend() => _reliableRelease?.TrySetResult();

        internal void BlockNormalPublish() =>
            _normalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseNormalPublish() => _normalRelease?.TrySetResult();

        internal async Task<RecordedSend> NextAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _available.WaitAsync(timeout.Token);
            lock (_messages)
            {
                return _messages.Dequeue();
            }
        }
    }

    private sealed class PendingNormalMessageSink : IBridgeMessageSink
    {
        internal int NormalPublishCalls { get; private set; }
        internal int CompletionRegistrations;
        public bool IsConnected => true;
        public bool IsAcknowledged => true;

        public ValueTask<bool> PublishFrameAsync(
            InteractionFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalPublishCalls++;
            return new ValueTask<bool>(
                new PendingValueTaskSource(
                    () => Interlocked.Increment(ref CompletionRegistrations)),
                0);
        }

        public bool DiscardStagedLatestFrame(string providerId, string providerInstanceId) => false;

        public ValueTask<bool> SendReliableFrameAsync(
            InteractionFrame frame,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> SendAsync(
            InteractionEnvelope message,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    private sealed class PendingValueTaskSource(Action onCompleted) : IValueTaskSource<bool>
    {
        public bool GetResult(short token) => throw new InvalidOperationException("The test source is pending.");
        public ValueTaskSourceStatus GetStatus(short token) => ValueTaskSourceStatus.Pending;

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) => onCompleted();
    }

    private sealed class ServerMessageSink(InteractionPipeServer server) : IBridgeMessageSink
    {
        public bool IsConnected => server.IsClientConnected;

        public bool IsAcknowledged => server.IsClientAcknowledged;

        public ValueTask<bool> PublishFrameAsync(InteractionFrame frame, CancellationToken cancellationToken) =>
            server.PublishFrameAsync(frame, cancellationToken);

        public bool DiscardStagedLatestFrame(string providerId, string providerInstanceId) =>
            server.DiscardStagedLatestFrame(providerId, providerInstanceId);

        public ValueTask<bool> SendAsync(InteractionEnvelope message, CancellationToken cancellationToken) =>
            server.SendAsync(message, cancellationToken);

        public ValueTask<bool> SendReliableFrameAsync(
            InteractionFrame frame,
            CancellationToken cancellationToken) =>
            server.SendReliableFrameAsync(frame, cancellationToken);
    }

    private sealed record RecordedSend(InteractionEnvelope Message, bool Reliable);

    private sealed class RecordingServer
    {
        internal bool WasCancelled { get; private set; }

        internal async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
            }
        }
    }

    private sealed class RecordingParentProcessMonitor : IParentProcessMonitor
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> WaitedPid { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
        {
            WaitedPid.TrySetResult(processId);
            await _exit.Task.WaitAsync(cancellationToken);
        }

        internal void Exit() => _exit.TrySetResult();
    }

    private static ProviderDescriptor Descriptor() => new(
        "blaze.test",
        "Test provider",
        new Version(1, 0, 0),
        "Test",
        ["interaction-point"]);

    private static ProviderDescriptor Descriptor(string id) => new(
        id,
        "Test provider",
        new Version(1, 0, 0),
        "Test",
        ["interaction-point"]);

    private sealed class RecordingPlugin(
        ProviderDescriptor descriptor,
        IInteractionProvider provider) : IInteractionProviderPlugin
    {
        public ProviderDescriptor Descriptor { get; } = descriptor;
        public IProviderSettingsViewFactory? SettingsViewFactory => null;
        public IInteractionProvider CreateProvider(ProviderCreateContext context) => provider;
    }

    private sealed class ThrowingPlugin : IInteractionProviderPlugin
    {
        public ProviderDescriptor Descriptor => BridgeHostTests.Descriptor("blaze.throwing");
        public IProviderSettingsViewFactory? SettingsViewFactory => null;
        public IInteractionProvider CreateProvider(ProviderCreateContext context) =>
            throw new InvalidOperationException("activation failed");
    }

    private sealed class ThrowingIdentityProvider : IInteractionProvider
    {
        public int DisposeCount { get; private set; }
        public string ProviderInstanceId => throw new InvalidOperationException("identity failed");
        public ProviderRuntimeStatus Status => ProviderRuntimeStatus.Created;
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
        public Task InitializeAsync(ProviderInitializationContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly System.Text.StringBuilder _output = new();
        internal string Output => _output.ToString();
        public override void Write(string? message) => _output.Append(message);
        public override void WriteLine(string? message) => _output.AppendLine(message);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        internal static EmptyServices Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Provider, WeakReference LoadContext)
        FailCreateAndCaptureReleasedResources(string providersRoot)
    {
        PrefetchedProviderFactory? observedFactory = null;
        WeakReference? observedContext = null;
        Assert.Throws<ArgumentException>(() => BridgeHost.Create(new BridgeHostOptions
        {
            ProvidersRoot = providersRoot,
            DataRoot = Path.Combine(providersRoot, "data"),
            PipeName = "",
            ProviderFactoryObserved = (factory, loadContext) =>
            {
                observedFactory = factory;
                observedContext = loadContext;
            }
        }));
        Assert.NotNull(observedFactory);
        Assert.NotNull(observedContext);
        Assert.True(observedFactory.IsDisposed);
        return (observedFactory.ProviderReference, observedContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Provider, WeakReference LoadContext)
        CreateDisposeAndCaptureReleasedResources(string providersRoot)
    {
        PrefetchedProviderFactory? observedFactory = null;
        WeakReference? observedContext = null;
        var host = BridgeHost.Create(new BridgeHostOptions
        {
            ProvidersRoot = providersRoot,
            DataRoot = Path.Combine(providersRoot, "data"),
            PipeName = $"Blaze.InteractionBridge.Dispose.{Guid.NewGuid():N}",
            ProviderFactoryObserved = (factory, loadContext) =>
            {
                observedFactory = factory;
                observedContext = loadContext;
            }
        });
        host.HandleHelloAsync(Hello(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Assert.NotNull(observedFactory);
        Assert.NotNull(observedContext);
        Assert.True(observedFactory.IsDisposed);
        return (observedFactory.ProviderReference, observedContext);
    }

    private static void CollectUntilDead(params WeakReference[] references)
    {
        for (var attempt = 0; attempt < 20 && references.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(20);
        }
    }

    private sealed class ProviderTestDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "BlazeInteractionBridgeTests",
            Guid.NewGuid().ToString("N"));

        internal ProviderTestDirectory() => Directory.CreateDirectory(_root);
        internal string Root => _root;

        internal string AddEmptyProvider(string name)
        {
            var directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        internal string AddBuiltValidProvider(string name)
        {
            var target = AddEmptyProvider(name);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var source = Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "TestProviders",
                "ValidProvider",
                "bin",
                configuration,
                "net8.0");
            foreach (var file in Directory.EnumerateFiles(source))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }

            WriteManifest(target);
            return target;
        }

        internal static void WriteManifest(string directory, int providerApiVersion = 1)
        {
            File.WriteAllText(
                Path.Combine(directory, "provider.json"),
                JsonSerializer.Serialize(new
                {
                    id = "blaze.test.valid",
                    displayName = "Valid test provider",
                    version = "1.2.3",
                    providerApiVersion,
                    entryAssembly = "ValidProvider.dll",
                    entryType = "Blaze.Interaction.TestProviders.ValidPlugin",
                    category = "Test",
                    capabilities = new[] { "interaction-point", "diagnostics" }
                }));
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BlazeInteraction.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }
}
