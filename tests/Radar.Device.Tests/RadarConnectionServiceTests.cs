using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;

namespace Yuexin.Radar.Device.Tests;

public sealed class RadarConnectionServiceTests
{
    private static readonly byte[] ManualSample = [0x30, 0x14, 0x13, 0xAF];

    [Theory]
    [InlineData("not-an-ip", "127.0.0.1", 8487)]
    [InlineData("127.0.0.1", "not-an-ip", 8487)]
    [InlineData("127.0.0.1", "127.0.0.1", 0)]
    [InlineData("127.0.0.1", "127.0.0.1", 65536)]
    public void Constructor_RejectsInvalidEndpointsImmediately(string radarIp, string localIp, int port)
    {
        var options = Options(8487, autoReconnect: false);
        options.RadarIp = radarIp;
        options.LocalIp = localIp;
        options.Port = port;

        Assert.ThrowsAny<ArgumentException>(() => new RadarConnectionService(options));
    }

    [Fact]
    public async Task RunAsync_ReceivesProtocolPointsWithoutBlockingCaller()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pointReceived = new TaskCompletionSource<RadarPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new RadarConnectionService(Options(port, autoReconnect: false));
        service.PointReceived += point => pointReceived.TrySetResult(point);

        var runTask = service.RunAsync(cancellation.Token);
        using var server = await listener.AcceptSocketAsync(cancellation.Token);
        await server.SendAsync(ManualSample, SocketFlags.None, cancellation.Token);

        var point = await pointReceived.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await runTask;

        Assert.Equal(40, point.DistanceCentimeters);
        Assert.Equal(RadarConnectionState.Disconnected, service.State);
        Assert.Equal(4, service.Metrics.ReceivedByteCount);
        Assert.Equal(1, service.Metrics.ParsedPointCount);
    }

    [Fact]
    public async Task RunAsync_ReconnectsAndClearsPartialPacketState()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var states = new ConcurrentQueue<RadarConnectionState>();
        var pointReceived = new TaskCompletionSource<RadarPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new RadarConnectionService(Options(port, autoReconnect: true));
        service.StateChanged += state => states.Enqueue(state);
        service.PointReceived += point => pointReceived.TrySetResult(point);

        var runTask = service.RunAsync(cancellation.Token);
        using (var first = await listener.AcceptSocketAsync(cancellation.Token))
        {
            await first.SendAsync(ManualSample.AsMemory(0, 2), SocketFlags.None, cancellation.Token);
            first.Shutdown(SocketShutdown.Both);
        }

        using (var second = await listener.AcceptSocketAsync(cancellation.Token))
        {
            await second.SendAsync(ManualSample, SocketFlags.None, cancellation.Token);
            var point = await pointReceived.Task.WaitAsync(cancellation.Token);
            Assert.Equal(40, point.DistanceCentimeters);
        }

        cancellation.Cancel();
        await runTask;

        Assert.Contains(RadarConnectionState.Reconnecting, states);
        Assert.True(service.Metrics.SuccessfulConnectionCount >= 2);
        Assert.Equal(0, service.Metrics.CrcErrorCount);
    }

    [Fact]
    public async Task RunAsync_RaisesDataWarningBeforeDisconnectTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var warning = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(port, autoReconnect: false);
        options.DataWarningTimeout = TimeSpan.FromMilliseconds(40);
        options.DataDisconnectTimeout = TimeSpan.FromMilliseconds(500);
        await using var service = new RadarConnectionService(options);
        service.DataWarning += elapsed => warning.TrySetResult(elapsed);

        var runTask = service.RunAsync(cancellation.Token);
        using var server = await listener.AcceptSocketAsync(cancellation.Token);

        var elapsed = await warning.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await runTask;

        Assert.True(elapsed >= options.DataWarningTimeout);
    }

    [Fact]
    public async Task RunAsync_FaultingPointSubscriber_DoesNotStopOtherSubscribersOrTransport()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var pointReceived = new TaskCompletionSource<RadarPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new RadarConnectionService(Options(port, autoReconnect: false));
        service.PointReceived += _ => throw new InvalidOperationException("observer failed");
        service.PointReceived += point => pointReceived.TrySetResult(point);

        var runTask = service.RunAsync(cancellation.Token);
        using var server = await listener.AcceptSocketAsync(cancellation.Token);
        await server.SendAsync(ManualSample, SocketFlags.None, cancellation.Token);

        var completed = await Task.WhenAny(pointReceived.Task, Task.Delay(500, cancellation.Token));
        cancellation.Cancel();
        try
        {
            await runTask;
        }
        catch (InvalidOperationException)
        {
            // The assertion below captures the pre-fix behavior without hiding the regression.
        }

        Assert.Same(pointReceived.Task, completed);
    }

    private static RadarConnectionOptions Options(int port, bool autoReconnect)
    {
        return new RadarConnectionOptions
        {
            RadarIp = IPAddress.Loopback.ToString(),
            Port = port,
            LocalIp = IPAddress.Loopback.ToString(),
            AutoReconnect = autoReconnect,
            ConnectTimeout = TimeSpan.FromSeconds(1),
            DataDisconnectTimeout = TimeSpan.FromSeconds(2),
            ReconnectDelays = [TimeSpan.FromMilliseconds(20)]
        };
    }
}
