using System.IO.Pipes;
using System.Text.Json;
using Blaze.Interaction.Contracts;

namespace Blaze.Interaction.Ipc;

public sealed class InteractionPipeServerOptions
{
    public string PipeName { get; init; } = InteractionIpcProtocol.PipeName;

    public int MaximumPayloadLength { get; init; } = InteractionIpcProtocol.DefaultMaximumPayloadLength;

    public int ControlQueueCapacity { get; init; } = 64;

    public Func<HelloPayload, CancellationToken, ValueTask<HelloAckPayload>> CreateHelloAckAsync { get; init; } =
        static (_, _) => ValueTask.FromResult(new HelloAckPayload("1.0.0", null, []));
}

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

        ArgumentNullException.ThrowIfNull(_options.CreateHelloAckAsync);
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
            PipeOptions.Asynchronous);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken serverCancellation)
    {
        InteractionEnvelope first;
        try
        {
            first = await InteractionIpcStream.ReadAsync(
                pipe,
                serverCancellation,
                _options.MaximumPayloadLength).ConfigureAwait(false);
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

        await using var session = new InteractionPipeSession(
            pipe,
            _options.ControlQueueCapacity,
            _options.MaximumPayloadLength,
            serverCancellation);
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

        if (hello.Surfaces.Count == 0 ||
            hello.Surfaces.GroupBy(static surface => surface.SurfaceId, StringComparer.Ordinal)
                .Any(static group => group.Count() > 1) ||
            hello.Surfaces.Count(static surface => surface.IsPrimary) != 1)
        {
            return ("invalid_surface_topology", "Surface IDs must be unique and exactly one surface must be primary.");
        }

        return null;
    }

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
    private readonly CancellationTokenSource _cancellation;
    private int _active = 1;
    private int _disposed;

    internal InteractionPipeSession(
        Stream stream,
        int controlQueueCapacity,
        int maximumPayloadLength,
        CancellationToken serverCancellation)
    {
        _stream = stream;
        _maximumPayloadLength = maximumPayloadLength;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        Outbound = new InteractionOutboundQueue(controlQueueCapacity);
    }

    internal InteractionOutboundQueue Outbound { get; }

    internal bool IsActive => Volatile.Read(ref _active) != 0;

    internal async Task RunAsync()
    {
        var reader = ReadLoopAsync(_cancellation.Token);
        var writer = WriteLoopAsync(_cancellation.Token);
        await Task.WhenAny(reader, writer).ConfigureAwait(false);
        Deactivate();
        try
        {
            await Task.WhenAll(reader, writer).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidDataException)
        {
        }
        catch (JsonException)
        {
        }
    }

    internal void Deactivate()
    {
        if (Interlocked.Exchange(ref _active, 0) == 0)
        {
            return;
        }

        _cancellation.Cancel();
        Outbound.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Deactivate();
        await Outbound.DisposeAsync().ConfigureAwait(false);
        _cancellation.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await InteractionIpcStream.ReadAsync(
                _stream,
                cancellationToken,
                _maximumPayloadLength).ConfigureAwait(false);
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
                    var ping = message.DeserializePayload<PingPayload>();
                    await Outbound.EnqueueControlAsync(
                        InteractionEnvelope.Create(
                            InteractionMessageType.Pong,
                            message.Sequence,
                            new PongPayload(ping.TimestampUnixMs)),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case InteractionMessageType.Shutdown:
                    return;

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
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await Outbound.DequeueAsync(cancellationToken).ConfigureAwait(false);
            await InteractionIpcStream.WriteAsync(_stream, message, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class InteractionOutboundQueue : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Queue<InteractionEnvelope> _controls = new();
    private readonly SemaphoreSlim _controlSlots;
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _completion = new();
    private InteractionEnvelope? _latestFrame;
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
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _completion.Token);
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
            if (_controls.Count > 0)
            {
                var control = _controls.Dequeue();
                _controlSlots.Release();
                return control;
            }

            if (_latestFrame is not null)
            {
                var frame = _latestFrame;
                _latestFrame = null;
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
        }

        _completion.Cancel();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        Complete();
        _completion.Dispose();
        _controlSlots.Dispose();
        _available.Dispose();
        return ValueTask.CompletedTask;
    }
}
