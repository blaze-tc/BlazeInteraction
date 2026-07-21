using System.IO.Pipes;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;

namespace Yuexin.Radar.Ipc.Tests;

public sealed class RadarPipeServerTests
{
    [Fact]
    public async Task Server_DisposeWhileWaiting_StopsRunAndRejectsRestart()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName });
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
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName });
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
            HeartbeatTimeout = TimeSpan.FromSeconds(2)
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
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName });
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
            HelloAckFactory = () => new HelloAckPayload(
                "1.0.0",
                IpcProtocolVersion.Current,
                true,
                "multi-screen",
                [new RadarScreenInfo("main", "Main", 1920, 1080, true, 0)])
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
    public async Task Server_ReturnsErrorForIncompatibleProtocol()
    {
        var pipeName = "RadarControl.Tests." + Guid.NewGuid().ToString("N");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var server = new RadarPipeServer(new RadarPipeServerOptions { PipeName = pipeName });
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

    private static HelloPayload Hello() => new(
        42,
        "2021.3",
        [new RadarScreenDefinitionPayload("main", "Main", 1920, 1080, true, 0)]);
}
