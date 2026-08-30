using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Ipc;

public sealed class RadarPipeServerOptions
{
    public string PipeName { get; set; } = "Yuexin.RadarBridge";
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public int? ExpectedClientProcessId { get; set; }
    public Func<HelloPayload, PipeClientAuthenticationContext, CancellationToken, ValueTask<HelloAuthenticationResult>> AuthenticateHelloAsync { get; set; } =
        (_, _, _) => ValueTask.FromResult(HelloAuthenticationResult.Reject(
            "hello_handler_missing",
            "Hello handler is not configured."));
}

public sealed record PipeClientAuthenticationContext(
    int ClientProcessId,
    int ClientSessionId,
    int ServerSessionId,
    int? ExpectedClientProcessId,
    bool CurrentUserOnlyEnforced);

public sealed record HelloAuthenticationResult(HelloAckPayload? Ack, ErrorPayload? Error)
{
    public bool Accepted => Ack is not null && Error is null;

    public static HelloAuthenticationResult Accept(HelloAckPayload ack) => new(ack, null);

    public static HelloAuthenticationResult Reject(string code, string message) => new(null, new ErrorPayload(code, message));
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

        if (_options.ExpectedClientProcessId is <= 0)
        {
            throw new ArgumentException("ExpectedClientProcessId must be positive when supplied.", nameof(options));
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
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var authenticated = false;
                try
                {
                    await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                    var clientContext = ResolveClientContext(pipe);
                    await HandleClientAsync(
                        pipe,
                        clientContext,
                        linked.Token,
                        () =>
                        {
                            authenticated = true;
                        }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (
                    exception is IOException or TimeoutException or InvalidDataException or ObjectDisposedException or
                        Win32Exception or UnauthorizedAccessException)
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
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_activePipe, pipe))
            {
                pipe.Dispose();
            }

            NotifyClientError(new TimeoutException("IPC client did not accept the pointer batch within the configured send timeout."));
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
        PipeClientAuthenticationContext clientContext,
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
        if (!TryValidateTopology(hello, out var topologyError))
        {
            await WriteLockedAsync(
                pipe,
                IpcEnvelope.Create(IpcMessageType.Error, NextSequence(), topologyError),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var identityError = ValidateClientIdentity(hello, clientContext);
        if (identityError is not null)
        {
            await WriteLockedAsync(
                pipe,
                IpcEnvelope.Create(IpcMessageType.Error, NextSequence(), identityError),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var authentication = await _options.AuthenticateHelloAsync(hello, clientContext, cancellationToken).ConfigureAwait(false);
        if (!authentication.Accepted)
        {
            var error = authentication.Error ?? new ErrorPayload(
                "hello_rejected",
                "Hello authentication did not return an acknowledgement.");
            await WriteLockedAsync(
                pipe,
                IpcEnvelope.Create(IpcMessageType.Error, NextSequence(), error),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await WriteLockedAsync(
                pipe,
                IpcEnvelope.Create(IpcMessageType.HelloAck, NextSequence(), authentication.Ack!),
                cancellationToken).ConfigureAwait(false);
            _activePipe = pipe;
        }
        catch
        {
            if (ReferenceEquals(_activePipe, pipe))
            {
                _activePipe = null;
            }
            pipe.Dispose();
            throw;
        }
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

    private PipeClientAuthenticationContext ResolveClientContext(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure RadarBridge named-pipe authentication requires Windows.");
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the named-pipe client process ID.");
        }

        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var clientSessionId))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the named-pipe client session ID.");
        }

        if (clientProcessId == 0 || clientProcessId > int.MaxValue || clientSessionId > int.MaxValue)
        {
            throw new InvalidDataException("The named-pipe client identity is outside the supported range.");
        }

        using var currentProcess = Process.GetCurrentProcess();
        return new PipeClientAuthenticationContext(
            checked((int)clientProcessId),
            checked((int)clientSessionId),
            currentProcess.SessionId,
            _options.ExpectedClientProcessId,
            CurrentUserOnlyEnforced: true);
    }

    private static ErrorPayload? ValidateClientIdentity(
        HelloPayload hello,
        PipeClientAuthenticationContext context)
    {
        if (hello.UnityProcessId != context.ClientProcessId)
        {
            return new ErrorPayload(
                "client_pid_mismatch",
                $"Hello PID {hello.UnityProcessId} does not match verified pipe client PID {context.ClientProcessId}.");
        }

        if (context.ClientSessionId != context.ServerSessionId)
        {
            return new ErrorPayload(
                "client_session_mismatch",
                $"Pipe client session {context.ClientSessionId} does not match Bridge session {context.ServerSessionId}.");
        }

        if (context.ExpectedClientProcessId is int expectedProcessId &&
            context.ClientProcessId != expectedProcessId)
        {
            return new ErrorPayload(
                "unexpected_client_process",
                $"Verified pipe client PID {context.ClientProcessId} does not match expected parent PID {expectedProcessId}.");
        }

        return null;
    }

    private static bool TryValidateTopology(HelloPayload hello, out ErrorPayload error)
    {
        if (hello.Screens is null || hello.Screens.Count == 0)
        {
            error = new ErrorPayload("invalid_screen_topology", "Hello must contain at least one screen.");
            return false;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryCount = 0;
        foreach (var screen in hello.Screens)
        {
            if (!IsConfigurationId(screen.ScreenId) || !ids.Add(screen.ScreenId) ||
                string.IsNullOrWhiteSpace(screen.Name) || screen.DefaultWidthPixels <= 0 || screen.DefaultHeightPixels <= 0)
            {
                error = new ErrorPayload("invalid_screen_topology", "Hello contains an empty, duplicate, or invalid screen definition.");
                return false;
            }

            if (screen.IsPrimary) primaryCount++;
        }

        if (primaryCount != 1)
        {
            error = new ErrorPayload("invalid_screen_topology", "Hello must contain exactly one primary screen.");
            return false;
        }

        error = null!;
        return true;
    }

    private static bool IsConfigurationId(string? value) => value is { Length: >= 1 and <= 64 } &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(
        SafePipeHandle pipe,
        out uint clientSessionId);

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
