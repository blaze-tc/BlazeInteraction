using System.Net;
using System.Net.Sockets;
using Yuexin.Radar.Device;

namespace Yuexin.Radar.Device.Tests;

public sealed class RadarTcpClientTests
{
    [Fact]
    public async Task ConnectAndRead_UsesConfiguredLocalAddressAndReceivesBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptSocketAsync();

        await using var client = new RadarTcpClient();
        await client.ConnectAsync(new RadarConnectionOptions
        {
            RadarIp = IPAddress.Loopback.ToString(),
            Port = port,
            LocalIp = IPAddress.Loopback.ToString(),
            ConnectTimeout = TimeSpan.FromSeconds(2)
        });
        using var server = await acceptTask;
        await server.SendAsync(new byte[] { 1, 2, 3 }, SocketFlags.None);

        var buffer = new byte[8];
        var count = await client.ReadAsync(buffer);

        Assert.Equal(3, count);
        Assert.Equal([1, 2, 3], buffer[..count]);
        Assert.Equal(IPAddress.Loopback, client.LocalEndPoint?.Address);
    }

    [Fact]
    public async Task Connect_RejectsInvalidConfiguredAddress()
    {
        await using var client = new RadarTcpClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.ConnectAsync(new RadarConnectionOptions
        {
            RadarIp = "invalid",
            Port = 8487
        }));
    }
}
