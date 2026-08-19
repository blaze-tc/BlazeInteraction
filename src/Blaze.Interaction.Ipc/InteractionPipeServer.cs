using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Blaze.Interaction.Contracts;

namespace Blaze.Interaction.Ipc;

public sealed class InteractionPipeServerOptions
{
    public string PipeName { get; init; } = InteractionIpcProtocol.PipeName;

    public int MaximumPayloadLength { get; init; } = InteractionIpcProtocol.DefaultMaximumPayloadLength;

    public int ControlQueueCapacity { get; init; } = 64;

    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int? ExpectedClientProcessId { get; init; }

    public Func<NamedPipeServerStream, InteractionPipeClientIdentity>? ResolveClientIdentity { get; init; }

    internal Func<NamedPipeServerStream, Stream> CreateAuthenticatedSessionStream { get; init; } =
        static pipe => pipe;

    public Func<HelloPayload, CancellationToken, ValueTask<HelloAckPayload>> CreateHelloAckAsync { get; init; } =
        static (_, _) => ValueTask.FromResult(new HelloAckPayload("1.0.0", null, []));
}

public sealed record InteractionPipeClientIdentity(int ProcessId, int SessionId);

public sealed class InteractionClientConnectedEventArgs(HelloPayload hello) : EventArgs
{
    public HelloPayload Hello { get; } = hello;
}

public sealed class InteractionPipeServer : IAsyncDisposable
{
    private readonly InteractionPipeServerOptions _options;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TaskCompletionSource _runCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sessionLock = new();
    private InteractionPipeSession? _session;
    private int _runStarted;
    private int _disposed;

    public InteractionPipeServer(InteractionPipeServerOptions? options = null)
    {
        _options = options ?? new InteractionPipeServerOptions();
        if (string.IsNullOrWhiteSpace(_options.PipeName))
        {
            throw new ArgumentException("The pipe name cannot be blank.", nameof(options));
        }

        if (_options.MaximumPayloadLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum payload length must be positive.");
        }

        if (_options.ControlQueueCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The control queue capacity must be positive.");
        }

        if (_options.ExpectedClientProcessId is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The expected client process ID must be positive when supplied.");
        }

        ValidateTimeout(_options.HandshakeTimeout, nameof(_options.HandshakeTimeout));
        ValidateTimeout(_options.HeartbeatTimeout, nameof(_options.HeartbeatTimeout));
        ValidateTimeout(_options.SendTimeout, nameof(_options.SendTimeout));

        ArgumentNullException.ThrowIfNull(_options.CreateHelloAckAsync);
        ArgumentNullException.ThrowIfNull(_options.CreateAuthenticatedSessionStream);
    }

    public event EventHandler<InteractionClientConnectedEventArgs>? ClientConnected;

    public bool IsClientConnected
    {
        get
        {
            lock (_sessionLock)
            {
                return _session is { IsActive: true };
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException("The Interaction pipe server can only be run once.");
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    await using var pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                    await HandleClientAsync(pipe, linked.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
            }
        }
        finally
        {
            _runCompletion.TrySetResult();
        }
    }

    public async ValueTask<bool> SendAsync(
        InteractionEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        if (message.ProtocolVersion != InteractionIpcProtocol.CurrentVersion)
        {
            throw new ArgumentException("Only Interaction IPC protocol 1 messages can be sent.", nameof(message));
        }

        var session = GetActiveSession();
        if (session is null)
        {
            return false;
        }

        if (message.MessageType == InteractionMessageType.InteractionFrame)
        {
            return session.Outbound.PublishLatestFrame(message);
        }

        try
        {
            await session.Outbound.EnqueueControlAsync(message, cancellationToken).ConfigureAwait(false);
            return session.IsActive;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !session.IsActive)
        {
            return false;
        }
    }

    public ValueTask<bool> PublishFrameAsync(
        InteractionFrame frame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        var session = GetActiveSession();
        if (session is null)
        {
            return ValueTask.FromResult(false);
        }

        var message = InteractionEnvelope.Create(
            InteractionMessageType.InteractionFrame,
            frame.Sequence,
            frame);
        return ValueTask.FromResult(session.Outbound.PublishLatestFrame(message));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        var session = GetActiveSession();
        if (session is not null)
        {
            session.Deactivate();
        }

        if (Volatile.Read(ref _runStarted) != 0)
        {
            await _runCompletion.Task.ConfigureAwait(false);
        }

        _lifetimeCancellation.Dispose();
    }

    private NamedPipeServerStream CreatePipe()
    {
        return new NamedPipeServerStream(
            _options.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serverCancellation)
    {
        InteractionEnvelope first;
        using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        handshakeCancellation.CancelAfter(_options.HandshakeTimeout);
        try
        {
            first = await InteractionIpcStream.ReadAsync(
                pipe,
                handshakeCancellation.Token,
                _options.MaximumPayloadLength).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            handshakeCancellation.IsCancellationRequested && !serverCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (EndOfStreamException)
        {
            return;
        }
        catch (InvalidDataException exception)
        {
            await TryWriteErrorAsync(pipe, 0, "invalid_message", exception.Message, serverCancellation)
                .ConfigureAwait(false);
            return;
        }

        if (first.ProtocolVersion != InteractionIpcProtocol.CurrentVersion)
        {
            await TryWriteErrorAsync(
                pipe,
                first.Sequence,
                "unsupported_protocol",
                $"Interaction IPC protocol {first.ProtocolVersion} is not supported.",
                serverCancellation).ConfigureAwait(false);
            return;
        }

        if (first.MessageType != InteractionMessageType.Hello)
        {
            await TryWriteErrorAsync(
                pipe,
                first.Sequence,
                "hello_required",
                "The first client message must be Hello.",
                serverCancellation).ConfigureAwait(false);
            return;
        }

        HelloPayload hello;
        try
        {
            hello = first.DeserializePayload<HelloPayload>();
        }
        catch (InvalidDataException exception)
        {
            await TryWriteErrorAsync(pipe, first.Sequence, "invalid_hello", exception.Message, serverCancellation)
                .ConfigureAwait(false);
            return;
        }

        var validationError = ValidateHello(hello);
        if (validationError is not null)
        {
            await TryWriteErrorAsync(
                pipe,
                first.Sequence,
                validationError.Value.Code,
                validationError.Value.Message,
                serverCancellation).ConfigureAwait(false);
            return;
        }

        InteractionPipeClientIdentity clientIdentity;
        try
        {
            clientIdentity = (_options.ResolveClientIdentity ?? ResolveClientIdentity)(pipe);
        }
        catch (Exception exception) when (exception is
            Win32Exception or InvalidDataException or PlatformNotSupportedException)
        {
            await TryWriteErrorAsync(
                pipe,
                first.Sequence,
                "client_identity_unverified",
                "The named-pipe client identity could not be verified.",
                serverCancellation).ConfigureAwait(false);
            return;
        }

        var identityError = ValidateClientIdentity(hello, clientIdentity);
        if (identityError is not null)
        {
            await TryWriteErrorAsync(
                pipe,
                first.Sequence,
                identityError.Value.Code,
                identityError.Value.Message,
                serverCancellation).ConfigureAwait(false);
            return;
        }

        HelloAckPayload acknowledgement;
        try
        {
            acknowledgement = await _options.CreateHelloAckAsync(hello, serverCancellation).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(acknowledgement);
        }
        catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            await TryWriteErrorAsync(
                pipe,
                first.Sequence,
                "hello_rejected",
                exception.Message,
                serverCancellation).ConfigureAwait(false);
            return;
        }

        await InteractionIpcStream.WriteAsync(
            pipe,
            InteractionEnvelope.Create(InteractionMessageType.HelloAck, first.Sequence, acknowledgement),
            serverCancellation).ConfigureAwait(false);

        var sessionStream = _options.CreateAuthenticatedSessionStream(pipe)
            ?? throw new InvalidOperationException("The authenticated session stream factory returned null.");
        await using var session = new InteractionPipeSession(
            sessionStream,
            _options.ControlQueueCapacity,
            _options.MaximumPayloadLength,
            serverCancellation,
            _options.HeartbeatTimeout,
            _options.SendTimeout,
            pipe.Dispose);
        lock (_sessionLock)
        {
            _session = session;
        }

        InvokeConnected(hello);
        try
        {
            await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_sessionLock)
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }
            }

            session.Deactivate();
        }
    }

    private InteractionPipeSession? GetActiveSession()
    {
        lock (_sessionLock)
        {
            return _session is { IsActive: true } session ? session : null;
        }
    }

    private void InvokeConnected(HelloPayload hello)
    {
        var handlers = ClientConnected;
        if (handlers is null)
        {
            return;
        }

        var args = new InteractionClientConnectedEventArgs(hello);
        foreach (EventHandler<InteractionClientConnectedEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A diagnostic subscriber cannot tear down the authenticated IPC session.
            }
        }
    }

    private static (string Code, string Message)? ValidateHello(HelloPayload hello)
    {
        if (hello.UnityPid <= 0 || string.IsNullOrWhiteSpace(hello.UnityVersion) ||
            string.IsNullOrWhiteSpace(hello.SdkVersion) || hello.Surfaces is null)
        {
            return ("invalid_hello", "Hello requires a Unity PID, Unity version, SDK version, and surface list.");
        }

        if (hello.Surfaces.Count == 0 || hello.Surfaces.Any(static surface => surface is null))
        {
            return ("invalid_surface_topology", "Surface topology must contain non-null surface definitions.");
        }

        if (hello.Surfaces.Any(static surface =>
                string.IsNullOrWhiteSpace(surface.SurfaceId) ||
                string.IsNullOrWhiteSpace(surface.Name) ||
                surface.LogicalWidth <= 0 ||
                surface.LogicalHeight <= 0 ||
                surface.Order < 0) ||
            hello.Surfaces.GroupBy(static surface => surface.SurfaceId, StringComparer.Ordinal)
                .Any(static group => group.Count() > 1) ||
            hello.Surfaces.GroupBy(static surface => surface.Order)
                .Any(static group => group.Count() > 1) ||
            hello.Surfaces.Count(static surface => surface.IsPrimary) != 1)
        {
            return (
                "invalid_surface_topology",
                "Surfaces must be valid, IDs and orders must be unique, and exactly one surface must be primary.");
        }

        return null;
    }

    private static void ValidateTimeout(TimeSpan timeout, string propertyName)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                timeout,
                "The timeout must be positive and finite.");
        }
    }

    private (string Code, string Message)? ValidateClientIdentity(
        HelloPayload hello,
        InteractionPipeClientIdentity identity)
    {
        if (hello.UnityPid != identity.ProcessId)
        {
            return (
                "client_identity_mismatch",
                $"Hello PID {hello.UnityPid} does not match verified pipe client PID {identity.ProcessId}.");
        }

        using var currentProcess = Process.GetCurrentProcess();
        if (identity.SessionId != currentProcess.SessionId)
        {
            return (
                "client_session_mismatch",
                $"Pipe client session {identity.SessionId} does not match Bridge session {currentProcess.SessionId}.");
        }

        if (_options.ExpectedClientProcessId is int expectedProcessId &&
            identity.ProcessId != expectedProcessId)
        {
            return (
                "unexpected_client_process",
                $"Verified pipe client PID {identity.ProcessId} does not match expected PID {expectedProcessId}.");
        }

        return null;
    }

    private static InteractionPipeClientIdentity ResolveClientIdentity(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Secure Interaction named-pipe authentication requires Windows.");
        }

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not resolve the named-pipe client process ID.");
        }

        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var clientSessionId))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not resolve the named-pipe client session ID.");
        }

        if (clientProcessId > int.MaxValue || clientSessionId > int.MaxValue)
        {
            throw new InvalidDataException("The named-pipe client identity is outside the supported range.");
        }

        return new InteractionPipeClientIdentity(
            checked((int)clientProcessId),
            checked((int)clientSessionId));
    }

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

    private static async ValueTask TryWriteErrorAsync(
        Stream stream,
        long sequence,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await InteractionIpcStream.WriteAsync(
                stream,
                InteractionEnvelope.Create(
                    InteractionMessageType.Error,
                    sequence,
                    new ErrorPayload(code, message)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class InteractionPipeSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly int _maximumPayloadLength;
    private readonly TimeSpan _heartbeatTimeout;
    private readonly TimeSpan _sendTimeout;
    private readonly Action? _abortConnection;
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private int _active = 1;
    private int _disposed;

    internal InteractionPipeSession(
        Stream stream,
        int controlQueueCapacity,
        int maximumPayloadLength,
        CancellationToken serverCancellation,
        TimeSpan? heartbeatTimeout = null,
        TimeSpan? sendTimeout = null,
        Action? abortConnection = null)
    {
        _stream = stream;
        _maximumPayloadLength = maximumPayloadLength;
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(10);
        _sendTimeout = sendTimeout ?? TimeSpan.FromSeconds(2);
        _abortConnection = abortConnection;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        Outbound = new InteractionOutboundQueue(controlQueueCapacity);
        _cancellationRegistration = _cancellation.Token.Register(
            static state => ((InteractionPipeSession)state!).Deactivate(),
            this);
    }

    internal InteractionOutboundQueue Outbound { get; }

    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal async Task<InteractionPipeSessionOutcome> RunAsync()
    {
        var reader = ReadLoopAsync(_cancellation.Token);
        var writer = WriteLoopAsync(_cancellation.Token);
        ObserveFault(reader);
        var completed = await Task.WhenAny(reader, writer).ConfigureAwait(false);
        if (ReferenceEquals(completed, reader))
        {
            var outcome = await reader.ConfigureAwait(false);
            Deactivate();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }

            return outcome;
        }

        try
        {
            await writer.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            Deactivate();
        }

        return InteractionPipeSessionOutcome.SendTimeout;
    }

    private static void ObserveFault(Task task)
    {
        _ = task.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal void Deactivate()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        Outbound.Complete();
        _abortConnection?.Invoke();
        _cancellation.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Deactivate();
        await Outbound.DisposeAsync().ConfigureAwait(false);
        _cancellationRegistration.Dispose();
        _cancellation.Dispose();
    }

    private async Task<InteractionPipeSessionOutcome> ReadLoopAsync(
        CancellationToken cancellationToken)
    {
        var lastValidMessageTimestamp = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            var remainingHeartbeat = _heartbeatTimeout -
                Stopwatch.GetElapsedTime(lastValidMessageTimestamp);
            if (remainingHeartbeat <= TimeSpan.Zero)
            {
                return InteractionPipeSessionOutcome.HeartbeatTimeout;
            }

            using var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readCancellation.CancelAfter(remainingHeartbeat);
            InteractionEnvelope message;
            try
            {
                message = await InteractionIpcStream.ReadAsync(
                    _stream,
                    readCancellation.Token,
                    _maximumPayloadLength).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return InteractionPipeSessionOutcome.Cancelled;
            }
            catch (OperationCanceledException) when (readCancellation.IsCancellationRequested)
            {
                return InteractionPipeSessionOutcome.HeartbeatTimeout;
            }
            catch (EndOfStreamException)
            {
                return InteractionPipeSessionOutcome.Disconnected;
            }
            catch (IOException)
            {
                return InteractionPipeSessionOutcome.Disconnected;
            }
            catch (InvalidDataException)
            {
                return InteractionPipeSessionOutcome.ProtocolError;
            }

            if (message.ProtocolVersion != InteractionIpcProtocol.CurrentVersion)
            {
                await Outbound.EnqueueControlAsync(
                    InteractionEnvelope.Create(
                        InteractionMessageType.Error,
                        message.Sequence,
                        new ErrorPayload("unsupported_protocol", "Only Interaction IPC protocol 1 is supported.")),
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            switch (message.MessageType)
            {
                case InteractionMessageType.Ping:
                    PingPayload ping;
                    try
                    {
                        ping = message.DeserializePayload<PingPayload>();
                    }
                    catch (InvalidDataException)
                    {
                        return InteractionPipeSessionOutcome.ProtocolError;
                    }

                    await Outbound.EnqueueControlAsync(
                        InteractionEnvelope.Create(
                            InteractionMessageType.Pong,
                            message.Sequence,
                            new PongPayload(ping.TimestampUnixMs)),
                        cancellationToken).ConfigureAwait(false);
                    lastValidMessageTimestamp = Stopwatch.GetTimestamp();
                    break;

                case InteractionMessageType.Pong:
                    try
                    {
                        _ = message.DeserializePayload<PongPayload>();
                    }
                    catch (InvalidDataException)
                    {
                        return InteractionPipeSessionOutcome.ProtocolError;
                    }

                    lastValidMessageTimestamp = Stopwatch.GetTimestamp();
                    break;

                case InteractionMessageType.Shutdown:
                    return InteractionPipeSessionOutcome.Shutdown;

                default:
                    await Outbound.EnqueueControlAsync(
                        InteractionEnvelope.Create(
                            InteractionMessageType.Error,
                            message.Sequence,
                            new ErrorPayload(
                                "unexpected_client_message",
                                $"{message.MessageType} is not valid after the Hello handshake.")),
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        return InteractionPipeSessionOutcome.Cancelled;
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await Outbound.DequeueAsync(cancellationToken).ConfigureAwait(false);
            using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            sendCancellation.CancelAfter(_sendTimeout);
            try
            {
                await InteractionIpcStream.WriteAsync(
                    _stream,
                    message,
                    sendCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                sendCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}

internal enum InteractionPipeSessionOutcome
{
    Disconnected = 0,
    Shutdown = 1,
    ProtocolError = 2,
    Cancelled = 3,
    HeartbeatTimeout = 4,
    SendTimeout = 5
}

internal sealed class InteractionOutboundQueue : IAsyncDisposable
{
    private const int MaximumControlBurstWhileFramePending = 8;
    private readonly object _sync = new();
    private readonly Queue<InteractionEnvelope> _controls = new();
    private readonly SemaphoreSlim _controlSlots;
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _completion = new();
    private InteractionEnvelope? _latestFrame;
    private TaskCompletionSource? _pendingEnqueuesDrained;
    private int _controlBurstWhileFramePending;
    private int _pendingEnqueues;
    private bool _completed;
    private int _disposed;

    internal InteractionOutboundQueue(int controlCapacity)
    {
        if (controlCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(controlCapacity));
        }

        _controlSlots = new SemaphoreSlim(controlCapacity, controlCapacity);
    }

    internal async ValueTask EnqueueControlAsync(
        InteractionEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            if (_completed)
            {
                throw new OperationCanceledException(_completion.Token);
            }

            _pendingEnqueues++;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _completion.Token);
        try
        {
            await _controlSlots.WaitAsync(linked.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_completed)
                {
                    _controlSlots.Release();
                    throw new OperationCanceledException(_completion.Token);
                }

                _controls.Enqueue(message);
            }

            _available.Release();
        }
        finally
        {
            lock (_sync)
            {
                _pendingEnqueues--;
                if (_completed && _pendingEnqueues == 0)
                {
                    _pendingEnqueuesDrained?.TrySetResult();
                }
            }
        }
    }

    internal bool PublishLatestFrame(InteractionEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageType != InteractionMessageType.InteractionFrame)
        {
            throw new ArgumentException("The latest-value slot only accepts InteractionFrame messages.", nameof(message));
        }

        var shouldSignal = false;
        lock (_sync)
        {
            if (_completed)
            {
                return false;
            }

            shouldSignal = _latestFrame is null;
            _latestFrame = message;
        }

        if (shouldSignal)
        {
            _available.Release();
        }

        return true;
    }

    internal async ValueTask<InteractionEnvelope> DequeueAsync(
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _completion.Token);
        await _available.WaitAsync(linked.Token).ConfigureAwait(false);
        lock (_sync)
        {
            var framePending = _latestFrame is not null;
            if (_controls.Count > 0 &&
                (!framePending ||
                 _controlBurstWhileFramePending < MaximumControlBurstWhileFramePending))
            {
                var control = _controls.Dequeue();
                _controlBurstWhileFramePending = framePending
                    ? _controlBurstWhileFramePending + 1
                    : 0;
                _controlSlots.Release();
                return control;
            }

            if (_latestFrame is not null)
            {
                var frame = _latestFrame;
                _latestFrame = null;
                _controlBurstWhileFramePending = 0;
                return frame;
            }
        }

        throw new InvalidOperationException("The outbound queue signal did not identify an available message.");
    }

    internal void Complete()
    {
        lock (_sync)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (_pendingEnqueues > 0)
            {
                _pendingEnqueuesDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        _completion.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Complete();
        Task pendingEnqueues;
        lock (_sync)
        {
            pendingEnqueues = _pendingEnqueuesDrained?.Task ?? Task.CompletedTask;
        }

        await pendingEnqueues.ConfigureAwait(false);
        _completion.Dispose();
        _controlSlots.Dispose();
        _available.Dispose();
    }
}
