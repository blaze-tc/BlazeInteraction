using System.IO.Pipes;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;

namespace Yuexin.Radar.Ipc.Tests;

public sealed class RadarPipeServerTests
{
    [Fact]
    public async Task Server_RejectsClaimedPidMismatchThenAcceptsLegitimateClient()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var authenticationCalls = 0;
        await using var server = new RadarPipeServer(new RadarPipeServerOptions
        {
            PipeName = pipeName,
            AuthenticateHelloAsync = (_, _, _) =>
            {
                Interlocked.Increment(ref authenticationCalls);
                return Accept();
            }
        });
        var runTask = server.RunAsync(cancellation.Token);

        await using (var attacker = Client(pipeName))
        {
            await attacker.ConnectAsync(cancellation.Token);
            await IpcStream.WriteAsync(attacker, IpcEnvelope.Create(
                IpcMessageType.Hello,
                1,
                Hello(Environment.ProcessId + 1)), cancellation.Token);
            var rejection = await IpcStream.ReadAsync(attacker, cancellation.Token);
            Assert.Equal(IpcMessageType.Error, rejection.MessageType);
            Assert.Equal("client_pid_mismatch", rejection.DeserializePayload<ErrorPayload>().Code);
        }

        await using var legitimate = Client(pipeName);
        await legitimate.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(legitimate, IpcEnvelope.Create(
            IpcMessageType.Hello,
            2,
            Hello(Environment.ProcessId)), cancellation.Token);
        Assert.Equal(IpcMessageType.HelloAck, (await IpcStream.ReadAsync(legitimate, cancellation.Token)).MessageType);
        Assert.Equal(1, authenticationCalls);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_RejectsRealClientThatDoesNotMatchExpectedParentPid()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var authenticationCalls = 0;
        await using var server = new RadarPipeServer(new RadarPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedClientProcessId = Environment.ProcessId + 1,
            AuthenticateHelloAsync = (_, _, _) =>
            {
                Interlocked.Increment(ref authenticationCalls);
                return Accept();
            }
        });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = Client(pipeName);
        await client.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(client, IpcEnvelope.Create(
            IpcMessageType.Hello,
            1,
            Hello(Environment.ProcessId)), cancellation.Token);
        var rejection = await IpcStream.ReadAsync(client, cancellation.Token);

        Assert.Equal(IpcMessageType.Error, rejection.MessageType);
        Assert.Equal("unexpected_client_process", rejection.DeserializePayload<ErrorPayload>().Code);
        Assert.Equal(0, authenticationCalls);
        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_PassesVerifiedCurrentUserProcessAndSessionContextToAuthentication()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        PipeClientAuthenticationContext? observed = null;
        await using var server = new RadarPipeServer(new RadarPipeServerOptions
        {
            PipeName = pipeName,
            ExpectedClientProcessId = Environment.ProcessId,
            AuthenticateHelloAsync = (_, context, _) =>
            {
                observed = context;
                return Accept();
            }
        });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = Client(pipeName);
        await client.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(client, IpcEnvelope.Create(
            IpcMessageType.Hello,
            1,
            Hello(Environment.ProcessId)), cancellation.Token);
        Assert.Equal(IpcMessageType.HelloAck, (await IpcStream.ReadAsync(client, cancellation.Token)).MessageType);

        Assert.NotNull(observed);
        Assert.Equal(Environment.ProcessId, observed.ClientProcessId);
        Assert.Equal(Environment.ProcessId, observed.ExpectedClientProcessId);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().SessionId, observed.ClientSessionId);
        Assert.True(observed.CurrentUserOnlyEnforced);
        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_DisposeWhileWaiting_StopsRunAndRejectsRestart()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName, AuthenticateHelloAsync = AcceptHelloAsync });
        var runTask = server.RunAsync(CancellationToken.None);

        await Task.Delay(50);
        await server.DisposeAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => server.RunAsync(CancellationToken.None));
        await server.DisposeAsync();
    }

    [Fact]
    public async Task Server_CancellationBeforeConnection_DoesNotReportPhantomDisconnect()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource();
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName, AuthenticateHelloAsync = AcceptHelloAsync });
        var disconnectCount = 0;
        server.ClientDisconnected += () => Interlocked.Increment(ref disconnectCount);

        var runTask = server.RunAsync(cancellation.Token);
        cancellation.Cancel();
        await runTask;

        Assert.Equal(0, disconnectCount);
    }

    [Fact]
    public async Task Server_MalformedClient_DoesNotPreventNextClientFromConnecting()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions
        {
            PipeName = pipeName,
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            AuthenticateHelloAsync = AcceptHelloAsync
        });
        var runTask = server.RunAsync(cancellation.Token);

        await using (var malformedClient = new NamedPipeClientStream(
                         ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await malformedClient.ConnectAsync(cancellation.Token);
            await malformedClient.WriteAsync(new byte[] { 1, 0, 0, 0, (byte)'{' }, cancellation.Token);
            await malformedClient.FlushAsync(cancellation.Token);
        }

        await using var healthyClient = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await healthyClient.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(
            healthyClient,
            IpcEnvelope.Create(IpcMessageType.Hello, 2, Hello()),
            cancellation.Token);

        var ack = await IpcStream.ReadAsync(healthyClient, cancellation.Token);
        Assert.Equal(IpcMessageType.HelloAck, ack.MessageType);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_HealthProbeDisconnectBeforeHello_IsSilent()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName, AuthenticateHelloAsync = AcceptHelloAsync });
        var clientErrors = 0;
        server.ClientError += _ => Interlocked.Increment(ref clientErrors);
        var runTask = server.RunAsync(cancellation.Token);

        await using (var probe = new NamedPipeClientStream(
                         ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await probe.ConnectAsync(cancellation.Token);
        }

        await using var healthyClient = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await healthyClient.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(
            healthyClient,
            IpcEnvelope.Create(IpcMessageType.Hello, 3, Hello()),
            cancellation.Token);
        await IpcStream.ReadAsync(healthyClient, cancellation.Token);

        Assert.Equal(0, clientErrors);
        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_HandshakesAndRespondsToPing()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions
        {
            PipeName = pipeName,
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            AuthenticateHelloAsync = (_, _, _) => ValueTask.FromResult(HelloAuthenticationResult.Accept(new HelloAckPayload(
                "1.0.0",
                IpcProtocolVersion.Current,
                true,
                "multi-screen",
                [new RadarScreenInfo("main", "Main", 1920, 1080, true, 0)])))
        });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(
            client,
            IpcEnvelope.Create(IpcMessageType.Hello, 1, Hello()),
            cancellation.Token);

        var ack = await IpcStream.ReadAsync(client, cancellation.Token);
        Assert.Equal(IpcMessageType.HelloAck, ack.MessageType);
        var helloAck = ack.DeserializePayload<HelloAckPayload>();
        Assert.Equal(IpcProtocolVersion.Current, helloAck.ProtocolVersion);
        Assert.Equal("multi-screen", helloAck.Capability);
        Assert.Equal("main", Assert.Single(helloAck.Screens).ScreenId);

        await IpcStream.WriteAsync(
            client,
            IpcEnvelope.Create(IpcMessageType.Ping, 2, new PingPayload(123)),
            cancellation.Token);
        var pong = await IpcStream.ReadAsync(client, cancellation.Token);

        Assert.Equal(IpcMessageType.Pong, pong.MessageType);
        Assert.Equal(123, pong.DeserializePayload<PongPayload>().ClientTimestampUnixMilliseconds);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_DefaultHandshake_UsesBridgeVersionOnePointTwoZero()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions
        {
            PipeName = pipeName,
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            AuthenticateHelloAsync = (_, _, _) => ValueTask.FromResult(HelloAuthenticationResult.Accept(new HelloAckPayload(
                "1.2.0",
                IpcProtocolVersion.Current,
                false,
                "multi-screen",
                [])))
        });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(
            client,
            IpcEnvelope.Create(IpcMessageType.Hello, 1, Hello()),
            cancellation.Token);

        var acknowledgement = await IpcStream.ReadAsync(client, cancellation.Token);
        var helloAck = acknowledgement.DeserializePayload<HelloAckPayload>();

        Assert.Equal(IpcMessageType.HelloAck, acknowledgement.MessageType);
        Assert.Equal("1.2.0", helloAck.BridgeVersion);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_ReturnsErrorForIncompatibleProtocol()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName, AuthenticateHelloAsync = AcceptHelloAsync });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(
            client,
            IpcEnvelope.Create(IpcMessageType.Hello, 1, new { }, protocolVersion: 99),
            cancellation.Token);

        var error = await IpcStream.ReadAsync(client, cancellation.Token);

        Assert.Equal(IpcMessageType.Error, error.MessageType);
        Assert.Contains("99", error.DeserializePayload<ErrorPayload>().Message);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_RejectsDuplicateScreenIdsBeforeAuthentication()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var authenticationCalls = 0;
        await using var server = new RadarPipeServer(new RadarPipeServerOptions
        {
            PipeName = pipeName,
            AuthenticateHelloAsync = (_, _, _) =>
            {
                Interlocked.Increment(ref authenticationCalls);
                return ValueTask.FromResult(HelloAuthenticationResult.Accept(new HelloAckPayload(
                    "1.2.0", IpcProtocolVersion.Current, false, "multi-screen", [])));
            }
        });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(client, IpcEnvelope.Create(IpcMessageType.Hello, 1, new HelloPayload(
            42,
            "2021.3",
            [
                new RadarScreenDefinitionPayload("front", "Front", 4096, 1536, true, 0),
                new RadarScreenDefinitionPayload("front", "Front Duplicate", 1920, 1080, false, 1)
            ])), cancellation.Token);

        var response = await IpcStream.ReadAsync(client, cancellation.Token);

        Assert.Equal(IpcMessageType.Error, response.MessageType);
        Assert.Equal("invalid_screen_topology", response.DeserializePayload<ErrorPayload>().Code);
        Assert.Equal(0, authenticationCalls);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_DoesNotPublishToCandidateBeforeHelloAuthentication()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName, AuthenticateHelloAsync = AcceptHelloAsync });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);

        var send = server.SendAsync(PointerBatchEnvelope(), cancellation.Token).AsTask();
        using var shortRead = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IpcStream.ReadAsync(client, shortRead.Token).AsTask());
        Assert.False(await send);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_RejectsInvalidHelloBeforeAnyPointerBatchCanBePublished()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName, AuthenticateHelloAsync = AcceptHelloAsync });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(client, IpcEnvelope.Create(IpcMessageType.Hello, 1, new HelloPayload(42, "2021.3",
            [new RadarScreenDefinitionPayload("front", "Front", 1920, 1080, true, 0), new RadarScreenDefinitionPayload("front", "Duplicate", 1920, 1080, false, 1)])), cancellation.Token);

        var send = server.SendAsync(PointerBatchEnvelope(), cancellation.Token).AsTask();
        var response = await IpcStream.ReadAsync(client, cancellation.Token);
        Assert.Equal(IpcMessageType.Error, response.MessageType);
        Assert.NotEqual(IpcMessageType.PointerBatch, response.MessageType);
        Assert.False(await send);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_PublishesOnlyAfterHelloAckIsWritten()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName, AuthenticateHelloAsync = AcceptHelloAsync });
        var runTask = server.RunAsync(cancellation.Token);

        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cancellation.Token);
        await IpcStream.WriteAsync(client, IpcEnvelope.Create(IpcMessageType.Hello, 1, Hello()), cancellation.Token);

        var acknowledgement = await IpcStream.ReadAsync(client, cancellation.Token);
        Assert.Equal(IpcMessageType.HelloAck, acknowledgement.MessageType);
        var send = server.SendAsync(PointerBatchEnvelope(), cancellation.Token).AsTask();
        Assert.Equal(IpcMessageType.PointerBatch, (await IpcStream.ReadAsync(client, cancellation.Token)).MessageType);
        Assert.True(await send);

        cancellation.Cancel();
        await runTask;
    }

    [Fact]
    public async Task Server_DoesNotExposeAuthenticatedClientUntilHelloAckIsWritten()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var authenticationCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new RadarPipeServer(new RadarPipeServerOptions
        {
            PipeName = pipeName,
            AuthenticateHelloAsync = (_, _, _) =>
            {
                authenticationCompleted.TrySetResult();
                return Accept();
            }
        });
        var runTask = server.RunAsync(cancellation.Token);
        var writeLock = (SemaphoreSlim)typeof(RadarPipeServer)
            .GetField("_writeLock", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .GetValue(server)!;

        await writeLock.WaitAsync(cancellation.Token);
        bool exposedBeforeAcknowledgement;
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync(cancellation.Token);
            await IpcStream.WriteAsync(
                client,
                IpcEnvelope.Create(IpcMessageType.Hello, 1, Hello()),
                cancellation.Token);
            await authenticationCompleted.Task.WaitAsync(cancellation.Token);
            await Task.Delay(50, cancellation.Token);
            exposedBeforeAcknowledgement = server.IsClientConnected;
        }
        finally
        {
            writeLock.Release();
        }

        var acknowledgement = await IpcStream.ReadAsync(client, cancellation.Token);
        Assert.Equal(IpcMessageType.HelloAck, acknowledgement.MessageType);
        Assert.False(exposedBeforeAcknowledgement);
        Assert.True(server.IsClientConnected);

        cancellation.Cancel();
        await runTask;
    }

    private static NamedPipeClientStream Client(string pipeName) =>
        new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

    private static HelloPayload Hello(int? processId = null) => new(
        processId ?? Environment.ProcessId,
        "2021.3",
        [new RadarScreenDefinitionPayload("main", "Main", 1920, 1080, true, 0)]);

    private static ValueTask<HelloAuthenticationResult> Accept() =>
        ValueTask.FromResult(HelloAuthenticationResult.Accept(new HelloAckPayload(
            "1.2.0", IpcProtocolVersion.Current, false, "multi-screen", [])));

    private static ValueTask<HelloAuthenticationResult> AcceptHelloAsync(
        HelloPayload _,
        PipeClientAuthenticationContext __,
        CancellationToken ___) =>
        ValueTask.FromResult(HelloAuthenticationResult.Accept(new HelloAckPayload(
            "1.2.0", IpcProtocolVersion.Current, false, "multi-screen", [])));

    private static IpcEnvelope PointerBatchEnvelope() => IpcEnvelope.Create(IpcMessageType.PointerBatch, 99,
        new PointerBatchPayload([new RadarScreenPointerFrame(new RadarScreenInfo("main", "Main", 1920, 1080, true, 0), 99, 0, [])]));
}
