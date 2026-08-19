using System.IO.Pipes;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Ipc;

namespace Blaze.Interaction.Ipc.Tests;

public sealed class InteractionPipeServerTests
{
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

    private static InteractionEnvelope FrameEnvelope(long sequence) =>
        InteractionEnvelope.Create(InteractionMessageType.InteractionFrame, sequence, Frame(sequence));

    private static string NewPipeName() => "BlazeInteraction.Tests." + Guid.NewGuid().ToString("N");

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(10, cancellationToken);
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
}
