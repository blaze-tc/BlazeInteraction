using System.IO.Pipes;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Ipc;

public sealed class RadarPipeServerOptions
{
    public string PipeName { get; set; } = "Yuexin.RadarBridge";
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public Func<HelloAckPayload> HelloAckFactory { get; set; } =
        () => new HelloAckPayload("1.2.0", IpcProtocolVersion.Current, false, "multi-screen", []);
}

public sealed class RadarPipeServer : IAsyncDisposable
{
    private readonly RadarPipeServerOptions _options;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NamedPipeServerStream? _activePipe;
    private long _sequence;
    private int _isRunning;
    private int _disposed;
    private int _resourcesDisposed;

    public RadarPipeServer(RadarPipeServerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(_options.PipeName))
        {
            throw new ArgumentException("PipeName is required.", nameof(options));
        }

        if (_options.HeartbeatTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("HeartbeatTimeout must be positive.", nameof(options));
        }
    }

    public bool IsClientConnected => _activePipe?.IsConnected == true;

    public event Action<HelloPayload>? ClientConnected;
    public event Action? ClientDisconnected;
    public event Action<IpcEnvelope>? MessageReceived;
    public event Action<Exception>? ClientError;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) != 0)
        {
            throw new InvalidOperationException("The IPC pipe server is already running.");
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token);

            while (!linked.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(
                    _options.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                _activePipe = pipe;
                var authenticated = false;
                try
                {
                    await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                    await HandleClientAsync(
                        pipe,
                        linked.Token,
                        () => authenticated = true).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (
                    exception is IOException or TimeoutException or InvalidDataException or ObjectDisposedException)
                {
                    if (authenticated || exception is not EndOfStreamException)
                    {
                        NotifyClientError(exception);
                    }
                }
                finally
                {
                    if (ReferenceEquals(_activePipe, pipe))
                    {
                        _activePipe = null;
                    }

                    if (authenticated)
                    {
                        InvokeSafely(ClientDisconnected);
                    }
                }
            }
        }
        finally
        {
            _activePipe = null;
            Interlocked.Exchange(ref _isRunning, 0);
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeResources();
            }
        }
    }

    public async ValueTask<bool> SendAsync(
        IpcEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var pipe = _activePipe;
        if (pipe?.IsConnected != true)
        {
            return false;
        }

        try
        {
            await WriteLockedAsync(pipe, envelope, cancellationToken).ConfigureAwait(false);
            return ReferenceEquals(_activePipe, pipe) && pipe.IsConnected;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            NotifyClientError(exception);
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _disposeCancellation.Cancel();
        _activePipe?.Dispose();
        if (Volatile.Read(ref _isRunning) == 0)
        {
            DisposeResources();
        }

        return ValueTask.CompletedTask;
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken,
        Action authenticated)
    {
        var first = await ReadWithHeartbeatAsync(pipe, cancellationToken).ConfigureAwait(false);
        var version = IpcProtocolVersion.Validate(first);
        if (!version.IsCompatible)
        {
            await WriteLockedAsync(
                pipe,
                IpcEnvelope.Create(
                    IpcMessageType.Error,
                    NextSequence(),
                    new ErrorPayload("protocol_version_mismatch", version.Error!)),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (first.MessageType != IpcMessageType.Hello)
        {
            await WriteLockedAsync(
                pipe,
                IpcEnvelope.Create(
                    IpcMessageType.Error,
                    NextSequence(),
                    new ErrorPayload("hello_required", "The first IPC message must be Hello.")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var hello = first.DeserializePayload<HelloPayload>();
        await WriteLockedAsync(
            pipe,
            IpcEnvelope.Create(IpcMessageType.HelloAck, NextSequence(), _options.HelloAckFactory()),
            cancellationToken).ConfigureAwait(false);
        authenticated();
        InvokeSafely(ClientConnected, hello);

        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            var message = await ReadWithHeartbeatAsync(pipe, cancellationToken).ConfigureAwait(false);
            var messageVersion = IpcProtocolVersion.Validate(message);
            if (!messageVersion.IsCompatible)
            {
                await WriteLockedAsync(
                    pipe,
                    IpcEnvelope.Create(
                        IpcMessageType.Error,
                        NextSequence(),
                        new ErrorPayload("protocol_version_mismatch", messageVersion.Error!)),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (message.MessageType)
            {
                case IpcMessageType.Ping:
                    var ping = message.DeserializePayload<PingPayload>();
                    await WriteLockedAsync(
                        pipe,
                        IpcEnvelope.Create(
                            IpcMessageType.Pong,
                            NextSequence(),
                            new PongPayload(ping.ClientTimestampUnixMilliseconds)),
                        cancellationToken).ConfigureAwait(false);
                    break;
                case IpcMessageType.Shutdown:
                    InvokeSafely(MessageReceived, message);
                    return;
                default:
                    InvokeSafely(MessageReceived, message);
                    break;
            }
        }
    }

    private async ValueTask<IpcEnvelope> ReadWithHeartbeatAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        heartbeat.CancelAfter(_options.HeartbeatTimeout);
        try
        {
            return await IpcStream.ReadAsync(stream, heartbeat.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Unity heartbeat timed out after {_options.HeartbeatTimeout.TotalSeconds:0.###} seconds.");
        }
    }

    private async ValueTask WriteLockedAsync(
        Stream stream,
        IpcEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IpcStream.WriteAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private long NextSequence() => Interlocked.Increment(ref _sequence);

    private void DisposeResources()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        _disposeCancellation.Dispose();
        _writeLock.Dispose();
    }

    private void NotifyClientError(Exception exception)
    {
        var handlers = ClientError;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(exception);
            }
            catch
            {
                // Diagnostics subscribers must not terminate the IPC accept loop.
            }
        }
    }

    private void InvokeSafely(Action? handlers)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                NotifyClientError(exception);
            }
        }
    }

    private void InvokeSafely<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                NotifyClientError(exception);
            }
        }
    }
}
