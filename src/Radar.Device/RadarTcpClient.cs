using System.Net;
using System.Net.Sockets;

namespace Yuexin.Radar.Device;

public sealed class RadarTcpClient : IAsyncDisposable
{
    private Socket? _socket;

    public bool IsConnected => _socket?.Connected == true;
    public IPEndPoint? LocalEndPoint => _socket?.LocalEndPoint as IPEndPoint;
    public IPEndPoint? RemoteEndPoint => _socket?.RemoteEndPoint as IPEndPoint;

    public async Task ConnectAsync(
        RadarConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IPAddress.TryParse(options.RadarIp, out var radarAddress))
        {
            throw new ArgumentException("RadarIp must be a valid IP address.", nameof(options));
        }

        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be between 1 and 65535.");
        }

        if (options.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "ConnectTimeout must be positive.");
        }

        await DisposeSocketAsync().ConfigureAwait(false);

        var socket = new Socket(radarAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };

        if (!string.IsNullOrWhiteSpace(options.LocalIp))
        {
            if (!IPAddress.TryParse(options.LocalIp, out var localAddress) ||
                localAddress.AddressFamily != radarAddress.AddressFamily)
            {
                socket.Dispose();
                throw new ArgumentException("LocalIp must be a valid address with the same address family as RadarIp.", nameof(options));
            }

            socket.Bind(new IPEndPoint(localAddress, 0));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(radarAddress, options.Port), timeout.Token).ConfigureAwait(false);
            _socket = socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var socket = _socket ?? throw new InvalidOperationException("The radar TCP client is not connected.");
        return socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
    }

    public ValueTask DisconnectAsync()
    {
        return DisposeSocketAsync();
    }

    public ValueTask DisposeAsync()
    {
        return DisposeSocketAsync();
    }

    private ValueTask DisposeSocketAsync()
    {
        var socket = Interlocked.Exchange(ref _socket, null);
        socket?.Dispose();
        return ValueTask.CompletedTask;
    }
}
