using System.IO.Pipes;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Ipc;

namespace Blaze.Interaction.Ipc.Tests;

public sealed class InteractionPipeServerTests
{
    [Fact]
    public async Task AcknowledgedSession_RaisesConnectedThenDisconnectedExactlyOnce()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var states = new ConcurrentQueue<string>();
        fixture.Server.ClientConnected += (_, _) => states.Enqueue("connected");
        fixture.Server.ClientDisconnected += (_, _) => states.Enqueue("disconnected");

        await using (var client = await fixture.ConnectAndSendHelloAsync())
        {
            Assert.Equal(
                InteractionMessageType.HelloAck,
                (await InteractionIpcStream.ReadAsync(client, fixture.Token)).MessageType);
            await WaitUntilAsync(() => states.Contains("connected"), fixture.Token);
        }

        await WaitUntilAsync(() => states.Contains("disconnected"), fixture.Token);
        Assert.Equal(["connected", "disconnected"], states);
    }

    [Fact]
    public async Task RejectedHello_DoesNotRaiseConnectionLifecycleEvents()
    {
        await using var fixture = await ServerFixture.StartAsync((_, _) =>
            ValueTask.FromException<HelloAckPayload>(new InvalidOperationException("rejected")));
        var states = new ConcurrentQueue<string>();
        fixture.Server.ClientConnected += (_, _) => states.Enqueue("connected");
        fixture.Server.ClientDisconnected += (_, _) => states.Enqueue("disconnected");
        await using var client = await fixture.ConnectAndSendHelloAsync();

        var error = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal("hello_rejected", error.DeserializePayload<ErrorPayload>().Code);
        await Task.Delay(50, fixture.Token);
        Assert.Empty(states);
    }

    [Fact]
    public async Task FailedHelloAckWrite_DoesNotRaiseConnectionLifecycleEvents()
    {
        var pipeName = NewPipeName();
        await using var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (_, _) => ValueTask.FromResult(Ack()),
            WriteHelloAckAsync = (_, _, _) => ValueTask.FromException(new IOException("ack write failed"))
        });
        var states = new ConcurrentQueue<string>();
        server.ClientConnected += (_, _) => states.Enqueue("connected");
        server.ClientDisconnected += (_, _) => states.Enqueue("disconnected");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stop = new CancellationTokenSource();
        var run = server.RunAsync(stop.Token);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()), timeout.Token);

        await WaitUntilAsync(() => !server.IsClientConnected, timeout.Token);
        Assert.Empty(states);

        stop.Cancel();
        await run.WaitAsync(timeout.Token);
    }

    [Fact]
    public void StaleAcknowledgedSession_DoesNotPublishDisconnectForReplacement()
    {
        Assert.False(InteractionPipeServer.ShouldPublishDisconnected(
            acknowledged: true,
            clearedCurrentSession: false));
        Assert.True(InteractionPipeServer.ShouldPublishDisconnected(
            acknowledged: true,
            clearedCurrentSession: true));
    }

    [Fact]
    public async Task Server_WritesHelloAckBeforeConnectedPublication()
    {
        await using var fixture = await ServerFixture.StartAsync();
        fixture.Server.ClientConnected += (_, _) =>
            _ = fixture.Server.PublishFrameAsync(Frame(8));
        await using var client = await fixture.ConnectAndSendHelloAsync();

        var first = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        var second = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal(InteractionMessageType.HelloAck, first.MessageType);
        var ack = first.DeserializePayload<HelloAckPayload>();
        Assert.Equal("1.0.0", ack.BridgeVersion);
        Assert.Equal("blaze.radar.f10f20", ack.ActiveProvider!.Id);
        Assert.Equal(["multi-surface", "provider-extensions"], ack.Capabilities);
        Assert.Equal(InteractionMessageType.InteractionFrame, second.MessageType);
    }

    [Fact]
    public async Task Server_StagesFramePublishedByHelloAckFactoryUntilAfterHelloAck()
    {
        ServerFixture? fixture = null;
        var published = false;
        fixture = await ServerFixture.StartAsync(async (_, cancellationToken) =>
        {
            published = await fixture!.Server.PublishFrameAsync(
                ProviderFrame(9, "blaze.provider.alpha", "alpha-main", InteractionPhase.Down),
                cancellationToken);
            return Ack();
        });
        await using var ownedFixture = fixture;
        await using var client = await fixture.ConnectAndSendHelloAsync();

        var first = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.True(published);
        Assert.Equal(InteractionMessageType.HelloAck, first.MessageType);
        var second = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        Assert.Equal(InteractionMessageType.InteractionFrame, second.MessageType);
        Assert.All(
            second.DeserializePayload<InteractionFrame>().Points,
            static point => Assert.Equal(InteractionPhase.Down, point.Phase));
    }

    [Fact]
    public async Task Server_RejectsControlPublishedByHelloAckFactoryBeforeAcknowledgement()
    {
        ServerFixture? fixture = null;
        var controlQueued = true;
        fixture = await ServerFixture.StartAsync(async (_, cancellationToken) =>
        {
            controlQueued = await fixture!.Server.SendAsync(
                ProviderChangedEnvelope(10),
                cancellationToken);
            return Ack();
        });
        await using var ownedFixture = fixture;
        await using var client = await fixture.ConnectAndSendHelloAsync();

        var acknowledgement = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        using var noControl = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        Assert.False(controlQueued);
        Assert.Equal(InteractionMessageType.HelloAck, acknowledgement.MessageType);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InteractionIpcStream.ReadAsync(client, noControl.Token));
    }

    [Fact]
    public async Task Server_DoesNotPublishBeforeHello()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        Assert.False(await fixture.Server.PublishFrameAsync(Frame(1), fixture.Token));
        using var shortRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InteractionIpcStream.ReadAsync(client, shortRead.Token));
    }

    [Fact]
    public async Task Server_DoesNotSendReliableFrameBeforeHello()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        Assert.False(await fixture.Server.SendReliableFrameAsync(CancelFrame(2), fixture.Token));
        using var shortRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InteractionIpcStream.ReadAsync(client, shortRead.Token));
    }

    [Fact]
    public async Task Server_RejectsWrongProtocolBeforeAuthentication()
    {
        var authenticationCalls = 0;
        await using var fixture = await ServerFixture.StartAsync((_, _) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            return ValueTask.FromResult(Ack());
        });
        await using var client = await fixture.ConnectAsync();
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello(), protocolVersion: 2), fixture.Token);

        var error = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal("unsupported_protocol", error.DeserializePayload<ErrorPayload>().Code);
        Assert.Equal(0, authenticationCalls);
    }

    [Fact]
    public Task Server_RejectsClaimedPidThatDoesNotMatchActualPipeClient()
    {
        return AssertInvalidHelloIsIsolatedAsync(
            Hello() with { UnityPid = Environment.ProcessId + 1 },
            "client_identity_mismatch");
    }

    [Fact]
    public async Task Server_RejectsActualClientWhenExpectedPidDiffers()
    {
        var pipeName = NewPipeName();
        var authenticationCalls = 0;
        await using var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedClientProcessId = Environment.ProcessId + 1,
            CreateHelloAckAsync = (_, _) =>
            {
                Interlocked.Increment(ref authenticationCalls);
                return ValueTask.FromResult(Ack());
            }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stopServer = new CancellationTokenSource();
        var run = server.RunAsync(stopServer.Token);
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()), timeout.Token);

        var error = await InteractionIpcStream.ReadAsync(client, timeout.Token);

        Assert.Equal("unexpected_client_process", error.DeserializePayload<ErrorPayload>().Code);
        Assert.Equal(0, authenticationCalls);
        stopServer.Cancel();
        await run.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Server_AcceptsActualClientWhenExpectedPidMatches()
    {
        var pipeName = NewPipeName();
        await using var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedClientProcessId = Environment.ProcessId,
            CreateHelloAckAsync = (_, _) => ValueTask.FromResult(Ack())
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stopServer = new CancellationTokenSource();
        var run = server.RunAsync(stopServer.Token);
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()), timeout.Token);

        Assert.Equal(InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(client, timeout.Token)).MessageType);
        stopServer.Cancel();
        await run.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Server_RejectsNonHelloFirstMessage()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Ping, 4, new PingPayload(10)), fixture.Token);

        var error = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal("hello_required", error.DeserializePayload<ErrorPayload>().Code);
    }

    [Fact]
    public async Task Server_IsolatesHelloAckWriteFailureAndAcceptsNextClient()
    {
        var pipeName = NewPipeName();
        var firstAckStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAck = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgementCalls = 0;
        await using var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref acknowledgementCalls) == 1)
                {
                    firstAckStarted.TrySetResult();
                    await releaseFirstAck.Task.WaitAsync(cancellationToken);
                    return new HelloAckPayload(
                        "1.0.0",
                        null,
                        [new string('x', 2 * 1024 * 1024)]);
                }

                return Ack();
            }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stopServer = new CancellationTokenSource();
        var run = server.RunAsync(stopServer.Token);
        try
        {
            await using (var first = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous))
            {
                await first.ConnectAsync(timeout.Token);
                await InteractionIpcStream.WriteAsync(
                    first,
                    InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()),
                    timeout.Token);
                await firstAckStarted.Task.WaitAsync(timeout.Token);
            }

            releaseFirstAck.TrySetResult();
            await using var second = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await second.ConnectAsync(timeout.Token);
            await InteractionIpcStream.WriteAsync(
                second,
                InteractionEnvelope.Create(InteractionMessageType.Hello, 2, Hello()),
                timeout.Token);

            Assert.Equal(
                InteractionMessageType.HelloAck,
                (await InteractionIpcStream.ReadAsync(second, timeout.Token)).MessageType);
            Assert.False(run.IsCompleted);
            Assert.Equal(2, acknowledgementCalls);
        }
        finally
        {
            releaseFirstAck.TrySetResult();
            stopServer.Cancel();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (IOException)
            {
                // Expected only during the RED run before handshake write failures are isolated.
            }
        }
    }

    [Fact]
    public async Task Server_DisposeDuringCompletingHelloAckWriteDoesNotAcknowledgeInactiveSession()
    {
        var pipeName = NewPipeName();
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (_, _) => ValueTask.FromResult(Ack()),
            WriteHelloAckAsync = async (_, _, _) =>
            {
                writeStarted.TrySetResult();
                await releaseWrite.Task;
            }
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = server.RunAsync();
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(timeout.Token);
        await InteractionIpcStream.WriteAsync(
            client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()),
            timeout.Token);
        await writeStarted.Task.WaitAsync(timeout.Token);

        var disposal = server.DisposeAsync().AsTask();
        await WaitUntilAsync(() => !server.IsClientConnected, timeout.Token);
        releaseWrite.TrySetResult();

        await disposal.WaitAsync(timeout.Token);
        await run.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Server_RejectsDuplicateSurfaceIdsBeforeAuthentication()
    {
        var authenticationCalls = 0;
        await using var fixture = await ServerFixture.StartAsync((_, _) =>
        {
            Interlocked.Increment(ref authenticationCalls);
            return ValueTask.FromResult(Ack());
        });
        await using var client = await fixture.ConnectAsync();
        var hello = Hello() with { Surfaces = [Surface("FRONT"), Surface("FRONT")] };
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1, hello), fixture.Token);

        var error = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal("invalid_surface_topology", error.DeserializePayload<ErrorPayload>().Code);
        Assert.Equal(0, authenticationCalls);
    }

    [Fact]
    public Task Server_IsolatesNullSurfaceElementAndAcceptsNextClient()
    {
        return AssertInvalidHelloIsIsolatedAsync(
            Hello() with { Surfaces = [null!] },
            "invalid_surface_topology");
    }

    [Fact]
    public Task Server_RejectsDuplicateSurfaceOrderAndAcceptsNextClient()
    {
        return AssertInvalidHelloIsIsolatedAsync(
            Hello() with
            {
                Surfaces =
                [
                    Surface("FRONT"),
                    Surface("RIGHT") with { IsPrimary = false }
                ]
            },
            "invalid_surface_topology");
    }

    [Theory]
    [InlineData(0, "2021.3", "1.0.0")]
    [InlineData(1, "", "1.0.0")]
    [InlineData(1, "2021.3", "")]
    public async Task Server_RejectsInvalidHelloIdentity(int unityPid, string unityVersion, string sdkVersion)
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1,
                Hello() with { UnityPid = unityPid, UnityVersion = unityVersion, SdkVersion = sdkVersion }), fixture.Token);

        var error = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal("invalid_hello", error.DeserializePayload<ErrorPayload>().Code);
    }

    [Fact]
    public async Task Server_AnswersPingWithPongUsingSameSequenceAndTimestamp()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using var client = await fixture.ConnectAndSendHelloAsync();
        _ = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Ping, 77, new PingPayload(123456)), fixture.Token);

        var pong = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal(InteractionMessageType.Pong, pong.MessageType);
        Assert.Equal(77, pong.Sequence);
        Assert.Equal(123456, pong.DeserializePayload<PongPayload>().TimestampUnixMs);
    }

    [Fact]
    public async Task Server_SendsControlMessagesWithoutSilentlyDroppingThem()
    {
        await using var fixture = await ServerFixture.StartAsync(controlQueueCapacity: 1);
        await using var client = await fixture.ConnectAndSendHelloAsync();
        _ = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        var status = InteractionEnvelope.Create(InteractionMessageType.Status, 10,
            new StatusPayload("running", "Running", "Ready.", null, 20));

        Assert.True(await fixture.Server.SendAsync(status, fixture.Token));
        var received = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal(InteractionMessageType.Status, received.MessageType);
        Assert.Equal(10, received.Sequence);
    }

    [Fact]
    public async Task Server_ReliableCancelFramePrecedesProviderChangedOnWire()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var cancelFrame = CancelFrame(40);
        var providerChanged = InteractionEnvelope.Create(
            InteractionMessageType.ProviderChanged,
            41,
            new ProviderChangedPayload(
                new ProviderReferencePayload("blaze.provider.alpha", "alpha-main"),
                new ProviderReferencePayload("blaze.provider.beta", "beta-main"),
                1041));
        var messagesQueued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.ClientConnected += async (_, _) =>
        {
            try
            {
                _ = await fixture.Server.SendReliableFrameAsync(cancelFrame, fixture.Token);
                _ = await fixture.Server.SendAsync(providerChanged, fixture.Token);
                messagesQueued.TrySetResult();
            }
            catch (Exception exception)
            {
                messagesQueued.TrySetException(exception);
            }
        };
        await using var client = await fixture.ConnectAndSendHelloAsync();
        _ = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        await messagesQueued.Task.WaitAsync(fixture.Token);

        var first = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        var second = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.Equal(InteractionMessageType.InteractionFrame, first.MessageType);
        Assert.All(
            first.DeserializePayload<InteractionFrame>().Points,
            static point => Assert.Equal(InteractionPhase.Cancel, point.Phase));
        Assert.Equal(InteractionMessageType.ProviderChanged, second.MessageType);
    }

    [Fact]
    public async Task Server_ReliableCancelDropsPendingLatestFrameFromCancelledProvider()
    {
        await using var fixture = await ServerFixture.StartAsync();
        var cancelFrame = CancelFrame(40);
        var providerChanged = ProviderChangedEnvelope(41);
        var messagesQueued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.ClientConnected += async (_, _) =>
        {
            try
            {
                _ = await fixture.Server.PublishFrameAsync(
                    ProviderFrame(39, "blaze.provider.alpha", "alpha-main", InteractionPhase.Move),
                    fixture.Token);
                _ = await fixture.Server.SendReliableFrameAsync(cancelFrame, fixture.Token);
                _ = await fixture.Server.SendAsync(providerChanged, fixture.Token);
                messagesQueued.TrySetResult();
            }
            catch (Exception exception)
            {
                messagesQueued.TrySetException(exception);
            }
        };
        await using var client = await fixture.ConnectAndSendHelloAsync();
        _ = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        await messagesQueued.Task.WaitAsync(fixture.Token);

        var cancel = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        var changed = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        using var shortRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        Assert.Equal(InteractionMessageType.InteractionFrame, cancel.MessageType);
        Assert.All(
            cancel.DeserializePayload<InteractionFrame>().Points,
            static point => Assert.Equal(InteractionPhase.Cancel, point.Phase));
        Assert.Equal(InteractionMessageType.ProviderChanged, changed.MessageType);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InteractionIpcStream.ReadAsync(client, shortRead.Token));
    }

    [Fact]
    public async Task Server_ReliableCancelPreservesPendingLatestFrameFromNewProvider()
    {
        await using var fixture = await ServerFixture.StartAsync(controlQueueCapacity: 16);
        var cancelFrame = CancelFrame(40);
        var newProviderFrame = ProviderFrame(
            42,
            "blaze.provider.beta",
            "beta-main",
            InteractionPhase.Move);
        var messagesQueued = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Server.ClientConnected += async (_, _) =>
        {
            try
            {
                for (var sequence = 0; sequence < 8; sequence++)
                {
                    _ = await fixture.Server.SendAsync(ControlEnvelope(sequence), fixture.Token);
                }

                _ = await fixture.Server.PublishFrameAsync(newProviderFrame, fixture.Token);
                _ = await fixture.Server.SendReliableFrameAsync(cancelFrame, fixture.Token);
                _ = await fixture.Server.SendAsync(ProviderChangedEnvelope(41), fixture.Token);
                messagesQueued.TrySetResult();
            }
            catch (Exception exception)
            {
                messagesQueued.TrySetException(exception);
            }
        };
        await using var client = await fixture.ConnectAndSendHelloAsync();
        _ = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        await messagesQueued.Task.WaitAsync(fixture.Token);

        for (var sequence = 0; sequence < 8; sequence++)
        {
            var status = await InteractionIpcStream.ReadAsync(client, fixture.Token);
            Assert.Equal(InteractionMessageType.Status, status.MessageType);
            Assert.Equal(sequence, status.Sequence);
        }

        var cancel = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        var changed = await InteractionIpcStream.ReadAsync(client, fixture.Token);
        var latest = await InteractionIpcStream.ReadAsync(client, fixture.Token);

        Assert.All(
            cancel.DeserializePayload<InteractionFrame>().Points,
            static point => Assert.Equal(InteractionPhase.Cancel, point.Phase));
        Assert.Equal(InteractionMessageType.ProviderChanged, changed.MessageType);
        Assert.Equal("blaze.provider.beta", latest.DeserializePayload<InteractionFrame>().ProviderId);
    }

    [Fact]
    public async Task Server_ReconnectRequiresNewHelloAndDropsOldSessionFrames()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using (var first = await fixture.ConnectAndSendHelloAsync())
        {
            _ = await InteractionIpcStream.ReadAsync(first, fixture.Token);
        }

        await WaitUntilAsync(() => !fixture.Server.IsClientConnected, fixture.Token);
        Assert.False(await fixture.Server.PublishFrameAsync(Frame(90), fixture.Token));
        await using var second = await fixture.ConnectAsync();
        using (var shortRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await InteractionIpcStream.ReadAsync(second, shortRead.Token));
        }

        await InteractionIpcStream.WriteAsync(second,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 2, Hello()), fixture.Token);
        var ack = await InteractionIpcStream.ReadAsync(second, fixture.Token);

        Assert.Equal(InteractionMessageType.HelloAck, ack.MessageType);
    }

    [Fact]
    public async Task Server_ReliableFrameAfterDisconnectIsNotReplayedToNextSession()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using (var first = await fixture.ConnectAndSendHelloAsync())
        {
            _ = await InteractionIpcStream.ReadAsync(first, fixture.Token);
        }

        await WaitUntilAsync(() => !fixture.Server.IsClientConnected, fixture.Token);
        Assert.False(await fixture.Server.SendReliableFrameAsync(CancelFrame(90), fixture.Token));
        await using var second = await fixture.ConnectAndSendHelloAsync();
        Assert.Equal(
            InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(second, fixture.Token)).MessageType);
        using var shortRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InteractionIpcStream.ReadAsync(second, shortRead.Token));
    }

    [Fact]
    public async Task Server_ShutdownMessageEndsSessionAndAllowsReconnect()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using (var first = await fixture.ConnectAndSendHelloAsync())
        {
            _ = await InteractionIpcStream.ReadAsync(first, fixture.Token);
            await InteractionIpcStream.WriteAsync(first,
                InteractionEnvelope.Create(InteractionMessageType.Shutdown, 2,
                    new ShutdownPayload("Unity exiting.")), fixture.Token);
            await WaitUntilAsync(() => !fixture.Server.IsClientConnected, fixture.Token);
        }

        await using var second = await fixture.ConnectAndSendHelloAsync();
        Assert.Equal(InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(second, fixture.Token)).MessageType);
    }

    [Fact]
    public async Task Server_SerializesSecondClientUntilFirstDisconnects()
    {
        await using var fixture = await ServerFixture.StartAsync();
        await using var first = await fixture.ConnectAndSendHelloAsync();
        _ = await InteractionIpcStream.ReadAsync(first, fixture.Token);
        await using var second = new NamedPipeClientStream(".", fixture.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);

        var secondConnect = second.ConnectAsync(fixture.Token);
        await Task.Delay(150, fixture.Token);
        Assert.False(secondConnect.IsCompleted);

        first.Dispose();
        await secondConnect;
        await InteractionIpcStream.WriteAsync(second,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 2, Hello()), fixture.Token);
        Assert.Equal(InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(second, fixture.Token)).MessageType);
    }

    [Fact]
    public async Task Server_RunCancellationAndDisposeAreIdempotent()
    {
        var pipeName = NewPipeName();
        var server = new InteractionPipeServer(new InteractionPipeServerOptions { PipeName = pipeName });
        using var cancellation = new CancellationTokenSource();
        var run = server.RunAsync(cancellation.Token);
        cancellation.Cancel();

        await run;
        await server.DisposeAsync();
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Server_HandshakeTimeoutReleasesClientThatNeverSendsHello()
    {
        await using var timeoutServer = await TimeoutServerFixture.StartAsync(
            handshakeTimeout: TimeSpan.FromMilliseconds(100));
        await using var silent = await timeoutServer.ConnectAsync();

        await Task.Delay(150, timeoutServer.Token);
        await using var next = await timeoutServer.ConnectAndSendHelloAsync();

        Assert.Equal(InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(next, timeoutServer.Token)).MessageType);
        Assert.False(timeoutServer.RunTask.IsCompleted);
    }

    [Fact]
    public async Task Server_HeartbeatTimeoutDisconnectsInactiveClientAndAllowsReconnect()
    {
        await using var timeoutServer = await TimeoutServerFixture.StartAsync(
            heartbeatTimeout: TimeSpan.FromMilliseconds(100));
        await using (var inactive = await timeoutServer.ConnectAndSendHelloAsync())
        {
            _ = await InteractionIpcStream.ReadAsync(inactive, timeoutServer.Token);
            using var publishCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeoutServer.Token);
            var publishLoop = Task.Run(async () =>
            {
                var sequence = 1L;
                while (!publishCancellation.Token.IsCancellationRequested)
                {
                    await timeoutServer.Server.PublishFrameAsync(
                        Frame(sequence++),
                        publishCancellation.Token);
                    await Task.Delay(10, publishCancellation.Token);
                }
            }, publishCancellation.Token);
            await WaitUntilAsync(() => !timeoutServer.Server.IsClientConnected, timeoutServer.Token);
            publishCancellation.Cancel();
            try
            {
                await publishLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await using var next = await timeoutServer.ConnectAndSendHelloAsync();
        Assert.Equal(InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(next, timeoutServer.Token)).MessageType);
        Assert.False(timeoutServer.RunTask.IsCompleted);
    }

    [Fact]
    public async Task Server_ValidPingRefreshesHeartbeatAndReturnsPong()
    {
        await using var timeoutServer = await TimeoutServerFixture.StartAsync(
            heartbeatTimeout: TimeSpan.FromMilliseconds(180));
        await using var client = await timeoutServer.ConnectAndSendHelloAsync();
        _ = await InteractionIpcStream.ReadAsync(client, timeoutServer.Token);
        await Task.Delay(100, timeoutServer.Token);
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Ping, 7, new PingPayload(77)),
            timeoutServer.Token);

        var pong = await InteractionIpcStream.ReadAsync(client, timeoutServer.Token);
        await Task.Delay(100, timeoutServer.Token);

        Assert.Equal(InteractionMessageType.Pong, pong.MessageType);
        Assert.Equal(77, pong.DeserializePayload<PongPayload>().TimestampUnixMs);
        Assert.True(timeoutServer.Server.IsClientConnected);
    }

    [Fact]
    public async Task Server_SendTimeoutReleasesBlockedControlSendAndAllowsReconnect()
    {
        await using var timeoutServer = await TimeoutServerFixture.StartAsync(
            sendTimeout: TimeSpan.FromMilliseconds(100),
            controlQueueCapacity: 1,
            stallAuthenticatedWrites: true);
        await using var stalled = await timeoutServer.ConnectAndSendHelloAsync();
        _ = await InteractionIpcStream.ReadAsync(stalled, timeoutServer.Token);
        var hugeStatus = InteractionEnvelope.Create(
            InteractionMessageType.Status,
            1,
            new StatusPayload(
                "large",
                "Running",
                new string('x', 2 * 1024 * 1024),
                null,
                1));
        var sends = Enumerable.Range(0, 8)
            .Select(_ => timeoutServer.Server.SendAsync(hugeStatus, timeoutServer.Token).AsTask())
            .ToArray();
        await Task.Delay(25, timeoutServer.Token);

        Assert.Contains(sends, static send => !send.IsCompleted);
        var results = await Task.WhenAll(sends).WaitAsync(timeoutServer.Token);
        await WaitUntilAsync(() => !timeoutServer.Server.IsClientConnected, timeoutServer.Token);

        Assert.Contains(false, results);
        await using var next = await timeoutServer.ConnectAndSendHelloAsync();
        Assert.Equal(InteractionMessageType.HelloAck,
            (await InteractionIpcStream.ReadAsync(next, timeoutServer.Token)).MessageType);
        Assert.False(timeoutServer.RunTask.IsCompleted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Server_RejectsNonPositiveOrInfiniteTimeouts(int milliseconds)
    {
        var timeout = TimeSpan.FromMilliseconds(milliseconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => new InteractionPipeServer(
            new InteractionPipeServerOptions { HandshakeTimeout = timeout }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InteractionPipeServer(
            new InteractionPipeServerOptions { HeartbeatTimeout = timeout }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InteractionPipeServer(
            new InteractionPipeServerOptions { SendTimeout = timeout }));
    }

    [Fact]
    public async Task Server_DisposeWhileClientIsConnectedStopsRunCleanly()
    {
        var pipeName = NewPipeName();
        var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (_, _) => ValueTask.FromResult(Ack())
        });
        var run = server.RunAsync();
        await using var client = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(timeout.Token);
        await InteractionIpcStream.WriteAsync(client,
            InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()), timeout.Token);
        _ = await InteractionIpcStream.ReadAsync(client, timeout.Token);

        await server.DisposeAsync();

        await run.WaitAsync(timeout.Token);
        await server.DisposeAsync();
    }

    [Fact]
    public Task Server_IsolatesOversizedFrameAndAcceptsNextClient()
    {
        return AssertClientProtocolViolationIsIsolatedAsync(async (client, cancellationToken) =>
        {
            var prefix = new byte[InteractionIpcProtocol.LengthPrefixSize];
            BinaryPrimitives.WriteInt32LittleEndian(
                prefix,
                InteractionIpcProtocol.DefaultMaximumPayloadLength + 1);
            await client.WriteAsync(prefix, cancellationToken);
            await client.FlushAsync(cancellationToken);
        });
    }

    [Fact]
    public Task Server_IsolatesMalformedJsonFrameAndAcceptsNextClient()
    {
        return AssertClientProtocolViolationIsIsolatedAsync((client, cancellationToken) =>
            WriteRawFrameAsync(client, "{"u8.ToArray(), cancellationToken));
    }

    [Fact]
    public Task Server_IsolatesInvalidPayloadAndAcceptsNextClient()
    {
        return AssertClientProtocolViolationIsIsolatedAsync(async (client, cancellationToken) =>
        {
            var invalidPing = InteractionEnvelope.Create(
                InteractionMessageType.Ping,
                9,
                new { timestampUnixMs = "not-a-number" });
            await InteractionIpcStream.WriteAsync(client, invalidPing, cancellationToken);
        });
    }

    [Fact]
    public async Task OutboundQueue_CoalescesFramesToLatestValueAndPrioritizesControl()
    {
        await using var queue = new InteractionOutboundQueue(controlCapacity: 2);
        Assert.True(queue.PublishLatestFrame(FrameEnvelope(1)));
        Assert.True(queue.PublishLatestFrame(FrameEnvelope(2)));
        await queue.EnqueueControlAsync(InteractionEnvelope.Create(
            InteractionMessageType.Status, 3,
            new StatusPayload("ready", "Ready", "Ready.", null, 3)));

        var control = await queue.DequeueAsync();
        var frame = await queue.DequeueAsync();

        Assert.Equal(InteractionMessageType.Status, control.MessageType);
        Assert.Equal(2, frame.DeserializePayload<InteractionFrame>().Sequence);
    }

    [Fact]
    public async Task OutboundQueue_AppliesBackpressureAndCancellationToControlMessages()
    {
        await using var queue = new InteractionOutboundQueue(controlCapacity: 1);
        await queue.EnqueueControlAsync(InteractionEnvelope.Create(
            InteractionMessageType.Status, 1,
            new StatusPayload("one", "Ready", "One.", null, 1)));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await queue.EnqueueControlAsync(InteractionEnvelope.Create(
                InteractionMessageType.Error, 2,
                new ErrorPayload("two", "Two.")), cancellation.Token));
    }

    [Fact]
    public async Task OutboundQueue_DeliversPendingFrameWithinBoundedControlBurst()
    {
        await using var queue = new InteractionOutboundQueue(controlCapacity: 1);
        Assert.True(queue.PublishLatestFrame(FrameEnvelope(99)));
        await queue.EnqueueControlAsync(ControlEnvelope(0));
        InteractionEnvelope? observedFrame = null;

        for (var attempt = 0; attempt < 9; attempt++)
        {
            var message = await queue.DequeueAsync();
            if (message.MessageType == InteractionMessageType.InteractionFrame)
            {
                observedFrame = message;
                break;
            }

            Assert.Equal(attempt, message.Sequence);
            await queue.EnqueueControlAsync(ControlEnvelope(attempt + 1));
        }

        Assert.NotNull(observedFrame);
        Assert.Equal(99, observedFrame.DeserializePayload<InteractionFrame>().Sequence);
    }

    [Fact]
    public Task Session_PropagatesInvalidDataWriterFault()
    {
        return AssertWriterFaultPropagatesAsync(
            new InvalidDataException("Injected writer program fault."));
    }

    [Fact]
    public Task Session_PropagatesJsonWriterFault()
    {
        return AssertWriterFaultPropagatesAsync(
            new JsonException("Injected writer serialization fault."));
    }

    [Fact]
    public async Task Session_CancellationNormalizesDisposedTransportWriter()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new CancellationDisposesWriteStream();
        await using var session = new InteractionPipeSession(
            stream,
            controlQueueCapacity: 1,
            InteractionIpcProtocol.DefaultMaximumPayloadLength,
            cancellation.Token);
        await session.Outbound.EnqueueControlAsync(
            InteractionEnvelope.Create(
                InteractionMessageType.Status,
                1,
                new StatusPayload("ready", "Ready", "Ready.", null, 1)));
        var run = session.RunAsync();
        await stream.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        Assert.Equal(
            InteractionPipeSessionOutcome.Cancelled,
            await run.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static HelloAckPayload Ack() => new(
        "1.0.0",
        new ProviderReferencePayload("blaze.radar.f10f20", "radar-main"),
        ["multi-surface", "provider-extensions"]);

    private static HelloPayload Hello() => new(
        Environment.ProcessId,
        "2021.3.45f1",
        "1.0.0",
        [Surface("FRONT")]);

    private static InteractionSurface Surface(string id) => new()
    {
        SurfaceId = id,
        Name = id,
        LogicalWidth = 1920,
        LogicalHeight = 1080,
        IsPrimary = true,
        Order = 0
    };

    private static InteractionFrame Frame(long sequence) => new()
    {
        ProviderId = "blaze.radar.f10f20",
        ProviderInstanceId = "radar-main",
        SurfaceId = "FRONT",
        Sequence = sequence,
        TimestampUnixMs = 1000 + sequence,
        Points = []
    };

    private static InteractionFrame CancelFrame(long sequence) =>
        ProviderFrame(
            sequence,
            "blaze.provider.alpha",
            "alpha-main",
            InteractionPhase.Cancel);

    private static InteractionFrame ProviderFrame(
        long sequence,
        string providerId,
        string providerInstanceId,
        InteractionPhase phase) => new()
        {
            ProviderId = providerId,
            ProviderInstanceId = providerInstanceId,
            SurfaceId = "FRONT",
            Sequence = sequence,
            TimestampUnixMs = 1000 + sequence,
            Points =
            [
                new InteractionPoint
                {
                    Id = 7,
                    SurfaceId = "FRONT",
                    ProviderId = providerId,
                    ProviderInstanceId = providerInstanceId,
                    SourceId = "source-7",
                    Phase = phase,
                    NormalizedPosition = new Vector2Data(0.25f, 0.75f),
                    PixelPosition = new Vector2Data(480f, 810f),
                    Confidence = 0.9f,
                    TimestampUnixMs = 1000 + sequence
                }
            ]
        };

    private static InteractionEnvelope ProviderChangedEnvelope(long sequence) =>
        InteractionEnvelope.Create(
            InteractionMessageType.ProviderChanged,
            sequence,
            new ProviderChangedPayload(
                new ProviderReferencePayload("blaze.provider.alpha", "alpha-main"),
                new ProviderReferencePayload("blaze.provider.beta", "beta-main"),
                1000 + sequence));

    private static InteractionEnvelope FrameEnvelope(long sequence) =>
        InteractionEnvelope.Create(InteractionMessageType.InteractionFrame, sequence, Frame(sequence));

    private static InteractionEnvelope ControlEnvelope(long sequence) =>
        InteractionEnvelope.Create(
            InteractionMessageType.Status,
            sequence,
            new StatusPayload("ready", "Ready", "Ready.", null, sequence));

    private static string NewPipeName() => "BlazeInteraction.Tests." + Guid.NewGuid().ToString("N");

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task AssertClientProtocolViolationIsIsolatedAsync(
        Func<NamedPipeClientStream, CancellationToken, Task> sendViolation)
    {
        var pipeName = NewPipeName();
        await using var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (_, _) => ValueTask.FromResult(Ack())
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stopServer = new CancellationTokenSource();
        var run = server.RunAsync(stopServer.Token);
        try
        {
            await using (var first = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await first.ConnectAsync(timeout.Token);
                await InteractionIpcStream.WriteAsync(first,
                    InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()), timeout.Token);
                Assert.Equal(InteractionMessageType.HelloAck,
                    (await InteractionIpcStream.ReadAsync(first, timeout.Token)).MessageType);
                await sendViolation(first, timeout.Token);
                await WaitUntilAsync(() => !server.IsClientConnected, timeout.Token);
            }

            await Task.WhenAny(run, Task.Delay(100, timeout.Token));
            Assert.False(run.IsFaulted, run.Exception?.ToString());
            Assert.False(run.IsCompleted);

            await using var second = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await second.ConnectAsync(timeout.Token);
            await InteractionIpcStream.WriteAsync(second,
                InteractionEnvelope.Create(InteractionMessageType.Hello, 2, Hello()), timeout.Token);
            Assert.Equal(InteractionMessageType.HelloAck,
                (await InteractionIpcStream.ReadAsync(second, timeout.Token)).MessageType);
            Assert.False(run.IsCompleted);

            stopServer.Cancel();
            await run.WaitAsync(timeout.Token);
        }
        finally
        {
            stopServer.Cancel();
            try
            {
                await run.WaitAsync(timeout.Token);
            }
            catch (InvalidDataException)
            {
                // Expected only during the RED run against the pre-isolation server.
            }
        }
    }

    private static async Task AssertInvalidHelloIsIsolatedAsync(
        HelloPayload invalidHello,
        string expectedErrorCode)
    {
        var pipeName = NewPipeName();
        await using var server = new InteractionPipeServer(new InteractionPipeServerOptions
        {
            PipeName = pipeName,
            CreateHelloAckAsync = (_, _) => ValueTask.FromResult(Ack())
        });
        var connectedCount = 0;
        server.ClientConnected += (_, _) => Interlocked.Increment(ref connectedCount);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stopServer = new CancellationTokenSource();
        var run = server.RunAsync(stopServer.Token);
        try
        {
            await using (var first = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await first.ConnectAsync(timeout.Token);
                await InteractionIpcStream.WriteAsync(first,
                    InteractionEnvelope.Create(InteractionMessageType.Hello, 1, invalidHello), timeout.Token);
                var response = await InteractionIpcStream.ReadAsync(first, timeout.Token);
                Assert.Equal(InteractionMessageType.Error, response.MessageType);
                Assert.Equal(expectedErrorCode, response.DeserializePayload<ErrorPayload>().Code);
                Assert.Equal(0, connectedCount);
            }

            Assert.False(run.IsFaulted, run.Exception?.ToString());
            await using var second = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await second.ConnectAsync(timeout.Token);
            await InteractionIpcStream.WriteAsync(second,
                InteractionEnvelope.Create(InteractionMessageType.Hello, 2, Hello()), timeout.Token);
            Assert.Equal(InteractionMessageType.HelloAck,
                (await InteractionIpcStream.ReadAsync(second, timeout.Token)).MessageType);
        }
        finally
        {
            stopServer.Cancel();
            await run.WaitAsync(timeout.Token);
        }
    }

    private static async Task WriteRawFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[InteractionIpcProtocol.LengthPrefixSize];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task AssertWriterFaultPropagatesAsync<TException>(TException expected)
        where TException : Exception
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = new ThrowingWriteDuplexStream(expected);
        await using var session = new InteractionPipeSession(
            stream,
            controlQueueCapacity: 1,
            InteractionIpcProtocol.DefaultMaximumPayloadLength,
            timeout.Token);
        await session.Outbound.EnqueueControlAsync(
            InteractionEnvelope.Create(
                InteractionMessageType.Status,
                1,
                new StatusPayload("ready", "Ready", "Ready.", null, 1)),
            timeout.Token);

        var actual = await Assert.ThrowsAsync<TException>(() => session.RunAsync());

        Assert.Same(expected, actual);
    }

    private sealed class ThrowingWriteDuplexStream(Exception writeException) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw writeException;
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.FromException(writeException);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class StallingWriteDuplexStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class CancellationDisposesWriteStream : Stream
    {
        private readonly TaskCompletionSource _writeStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WriteStarted => _writeStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _writeStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw new ObjectDisposedException(nameof(CancellationDisposesWriteStream));
            }
        }
    }

    private sealed class ServerFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new(TimeSpan.FromSeconds(10));
        private readonly Task _runTask;

        private ServerFixture(InteractionPipeServer server, string pipeName)
        {
            Server = server;
            PipeName = pipeName;
            _runTask = server.RunAsync(_cancellation.Token);
        }

        public InteractionPipeServer Server { get; }

        public string PipeName { get; }

        public CancellationToken Token => _cancellation.Token;

        public static Task<ServerFixture> StartAsync(
            Func<HelloPayload, CancellationToken, ValueTask<HelloAckPayload>>? createAck = null,
            int controlQueueCapacity = 8)
        {
            var pipeName = NewPipeName();
            var options = new InteractionPipeServerOptions
            {
                PipeName = pipeName,
                ControlQueueCapacity = controlQueueCapacity,
                CreateHelloAckAsync = createAck ?? ((_, _) => ValueTask.FromResult(Ack()))
            };
            return Task.FromResult(new ServerFixture(new InteractionPipeServer(options), pipeName));
        }

        public async Task<NamedPipeClientStream> ConnectAsync()
        {
            var client = new NamedPipeClientStream(".", PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(Token);
            return client;
        }

        public async Task<NamedPipeClientStream> ConnectAndSendHelloAsync()
        {
            var client = await ConnectAsync();
            await InteractionIpcStream.WriteAsync(client,
                InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()), Token);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            await _runTask;
            await Server.DisposeAsync();
            _cancellation.Dispose();
        }
    }

    private sealed class TimeoutServerFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(5));
        private readonly CancellationTokenSource _stopServer = new();

        private TimeoutServerFixture(InteractionPipeServer server, string pipeName)
        {
            Server = server;
            PipeName = pipeName;
            RunTask = server.RunAsync(_stopServer.Token);
        }

        public InteractionPipeServer Server { get; }

        public string PipeName { get; }

        public Task RunTask { get; }

        public CancellationToken Token => _timeout.Token;

        public static Task<TimeoutServerFixture> StartAsync(
            TimeSpan? handshakeTimeout = null,
            TimeSpan? heartbeatTimeout = null,
            TimeSpan? sendTimeout = null,
            int controlQueueCapacity = 8,
            bool stallAuthenticatedWrites = false)
        {
            var pipeName = NewPipeName();
            var server = new InteractionPipeServer(new InteractionPipeServerOptions
            {
                PipeName = pipeName,
                HandshakeTimeout = handshakeTimeout ?? TimeSpan.FromSeconds(2),
                HeartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(2),
                SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(2),
                ControlQueueCapacity = controlQueueCapacity,
                CreateAuthenticatedSessionStream = stallAuthenticatedWrites
                    ? static pipe => new StallingWriteDuplexStream(pipe)
                    : static pipe => pipe,
                CreateHelloAckAsync = (_, _) => ValueTask.FromResult(Ack())
            });
            return Task.FromResult(new TimeoutServerFixture(server, pipeName));
        }

        public async Task<NamedPipeClientStream> ConnectAsync()
        {
            var client = new NamedPipeClientStream(".", PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(Token);
            return client;
        }

        public async Task<NamedPipeClientStream> ConnectAndSendHelloAsync()
        {
            var client = await ConnectAsync();
            await InteractionIpcStream.WriteAsync(client,
                InteractionEnvelope.Create(InteractionMessageType.Hello, 1, Hello()), Token);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            _stopServer.Cancel();
            try
            {
                await RunTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException exception)
            {
                throw new InvalidOperationException(
                    $"IPC server did not stop after cancellation. Connected={Server.IsClientConnected}, RunStatus={RunTask.Status}.",
                    exception);
            }
            await Server.DisposeAsync();
            _stopServer.Dispose();
            _timeout.Dispose();
        }
    }
}
