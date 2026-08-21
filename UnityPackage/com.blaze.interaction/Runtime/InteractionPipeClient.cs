#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blaze.Interaction.Internal;
using Newtonsoft.Json;

namespace Blaze.Interaction
{
    public sealed class InteractionPipeClient : IDisposable
    {
        private static readonly long ErrorRepeatWindowTicks = Stopwatch.Frequency * 10L;
        private readonly string _pipeName;
        private readonly int _connectTimeoutMilliseconds;
        private readonly int _reconnectDelayMilliseconds;
        private readonly int _serverResponseTimeoutMilliseconds;
        private readonly LatestValueBuffer<InteractionFrame> _latestFrame =
            new LatestValueBuffer<InteractionFrame>();
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly object _lifecycleGate = new object();
        private readonly object _errorGate = new object();
        private readonly Dictionary<string, long> _recentErrors = new Dictionary<string, long>();
        private CancellationTokenSource _cancellation;
        private Task _runTask;
        private Task _stopTask;
        private NamedPipeClientStream _activePipe;
        private HelloPayload _hello;
        private long _sequence;
        private int _connected;
        private bool _disposed;

        public InteractionPipeClient(
            string pipeName = InteractionIpcProtocol.PipeName,
            int connectTimeoutMilliseconds = 500,
            int reconnectDelayMilliseconds = 250,
            int serverResponseTimeoutMilliseconds = 4000)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? InteractionIpcProtocol.PipeName : pipeName;
            _connectTimeoutMilliseconds = Math.Max(50, connectTimeoutMilliseconds);
            _reconnectDelayMilliseconds = Math.Max(25, reconnectDelayMilliseconds);
            _serverResponseTimeoutMilliseconds = Math.Max(250, serverResponseTimeoutMilliseconds);
        }

        public bool IsConnected { get { return Volatile.Read(ref _connected) != 0; } }
        public long DroppedFrameCount { get { return _latestFrame.DroppedCount; } }
        public string BridgeVersion { get; private set; } = string.Empty;
        public ProviderReferencePayload ActiveProvider { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        public event Action<bool> ConnectionChanged;
        public event Action<HelloAckPayload> HelloAcknowledged;
        public event Action<ProviderChangedPayload> ProviderChangedReceived;
        public event Action<StatusPayload> StatusReceived;
        public event Action<string> ErrorReceived;

        public void Start(HelloPayload hello)
        {
            if (hello == null)
            {
                throw new ArgumentNullException(nameof(hello));
            }

            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(InteractionPipeClient));
                }

                if (_runTask != null)
                {
                    return;
                }

                _hello = hello;
                _stopTask = null;
                _cancellation = new CancellationTokenSource();
                _runTask = Task.Run(() => RunAsync(_cancellation.Token));
            }
        }

        public bool TryConsumeLatestFrame(out InteractionFrame frame)
        {
            return _latestFrame.TryConsume(out frame);
        }

        public void DrainMainThreadEvents()
        {
            Action action;
            while (_mainThreadActions.TryDequeue(out action))
            {
                action();
            }
        }

        public Task StopAsync()
        {
            lock (_lifecycleGate)
            {
                if (_runTask == null)
                {
                    ResetSession();
                    return _stopTask ?? Task.CompletedTask;
                }

                if (_stopTask == null)
                {
                    _stopTask = StopCoreAsync(_runTask, _cancellation);
                }

                return _stopTask;
            }
        }

        public void Dispose()
        {
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            StopAsync().GetAwaiter().GetResult();
            _writeLock.Dispose();
        }

        private async Task StopCoreAsync(Task runTask, CancellationTokenSource cancellation)
        {
            if (cancellation != null)
            {
                cancellation.Cancel();
            }

            var pipe = Interlocked.Exchange(ref _activePipe, null);
            if (pipe != null)
            {
                pipe.Dispose();
            }

            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                if (cancellation != null)
                {
                    cancellation.Dispose();
                }

                ResetSession();
                lock (_lifecycleGate)
                {
                    if (ReferenceEquals(_runTask, runTask))
                    {
                        _runTask = null;
                        _cancellation = null;
                    }
                }
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Exception sessionError = null;
                using (var pipe = new NamedPipeClientStream(
                           ".",
                           _pipeName,
                           PipeDirection.InOut,
                           PipeOptions.Asynchronous))
                {
                    Interlocked.Exchange(ref _activePipe, pipe);
                    try
                    {
                        await pipe.ConnectAsync(_connectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                        await WriteEnvelopeAsync(
                            pipe,
                            InteractionIpcProtocol.Create(
                                InteractionMessageType.Hello,
                                NextSequence(),
                                _hello),
                            cancellationToken).ConfigureAwait(false);

                        using (var sessionCancellation =
                               CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            var heartbeat = SendHeartbeatAsync(pipe, sessionCancellation.Token);
                            try
                            {
                                await ReadMessagesAsync(pipe, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                sessionCancellation.Cancel();
                                try
                                {
                                    await heartbeat.ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                }
                                catch (ObjectDisposedException)
                                {
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (TimeoutException) when (!pipe.IsConnected)
                    {
                    }
                    catch (Exception exception) when (
                        exception is IOException ||
                        exception is InvalidDataException ||
                        exception is TimeoutException ||
                        exception is UnauthorizedAccessException ||
                        exception is JsonException)
                    {
                        sessionError = exception;
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref _activePipe, null, pipe);
                        ResetSession();
                    }
                }

                if (sessionError != null)
                {
                    ReportError(sessionError.Message);
                }

                await Task.Delay(_reconnectDelayMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReadMessagesAsync(Stream stream, CancellationToken cancellationToken)
        {
            var decoder = new LengthPrefixedFrameDecoder();
            var buffer = new byte[8192];
            var acknowledged = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                using (var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    responseTimeout.CancelAfter(_serverResponseTimeoutMilliseconds);
                    try
                    {
                        var count = await stream.ReadAsync(
                            buffer,
                            0,
                            buffer.Length,
                            responseTimeout.Token).ConfigureAwait(false);
                        if (count == 0)
                        {
                            throw new EndOfStreamException("BlazeInteractionBridge closed the Named Pipe connection.");
                        }

                        var payloads = decoder.Append(buffer, 0, count);
                        for (var index = 0; index < payloads.Count; index++)
                        {
                            var envelope = InteractionIpcProtocol.Deserialize(
                                Encoding.UTF8.GetString(payloads[index]));
                            if (envelope.ProtocolVersion != InteractionIpcProtocol.Version)
                            {
                                throw new InvalidDataException(
                                    "Interaction IPC protocol " + envelope.ProtocolVersion +
                                    " is incompatible with Unity SDK protocol " + InteractionIpcProtocol.Version + ".");
                            }

                            acknowledged = HandleEnvelope(envelope, acknowledged);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            "BlazeInteractionBridge did not respond for " +
                            _serverResponseTimeoutMilliseconds + " ms.");
                    }
                }
            }
        }

        private bool HandleEnvelope(InteractionEnvelope envelope, bool acknowledged)
        {
            switch (envelope.MessageType)
            {
                case InteractionMessageType.HelloAck:
                    if (acknowledged)
                    {
                        throw new InvalidDataException("BlazeInteractionBridge sent a duplicate HelloAck.");
                    }

                    var acknowledgement = envelope.DeserializePayload<HelloAckPayload>();
                    BridgeVersion = acknowledgement.BridgeVersion ?? string.Empty;
                    ActiveProvider = acknowledgement.ActiveProvider;
                    LastError = string.Empty;
                    _mainThreadActions.Enqueue(() => InvokeSafely(HelloAcknowledged, acknowledgement));
                    SetConnected(true);
                    return true;

                case InteractionMessageType.InteractionFrame:
                    RequireAcknowledged(acknowledged, "InteractionFrame");
                    _latestFrame.Publish(envelope.DeserializePayload<InteractionFrame>());
                    return true;

                case InteractionMessageType.ProviderChanged:
                    RequireAcknowledged(acknowledged, "ProviderChanged");
                    var change = envelope.DeserializePayload<ProviderChangedPayload>();
                    ActiveProvider = change.ActiveProvider;
                    _mainThreadActions.Enqueue(() => InvokeSafely(ProviderChangedReceived, change));
                    return true;

                case InteractionMessageType.Status:
                    RequireAcknowledged(acknowledged, "Status");
                    var status = envelope.DeserializePayload<StatusPayload>();
                    _mainThreadActions.Enqueue(() => InvokeSafely(StatusReceived, status));
                    return true;

                case InteractionMessageType.Error:
                    var error = envelope.DeserializePayload<ErrorPayload>();
                    ReportError(error.Message);
                    return acknowledged;

                default:
                    return acknowledged;
            }
        }

        private static void RequireAcknowledged(bool acknowledged, string messageType)
        {
            if (!acknowledged)
            {
                throw new InvalidDataException(
                    "BlazeInteractionBridge sent " + messageType + " before HelloAck completed the handshake.");
            }
        }

        private async Task SendHeartbeatAsync(Stream stream, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                await WriteEnvelopeAsync(
                    stream,
                    InteractionIpcProtocol.Create(
                        InteractionMessageType.Ping,
                        NextSequence(),
                        new PingPayload
                        {
                            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WriteEnvelopeAsync(
            Stream stream,
            InteractionEnvelope envelope,
            CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(InteractionIpcProtocol.Serialize(envelope));
            var frame = new byte[payload.Length + 4];
            frame[0] = (byte)payload.Length;
            frame[1] = (byte)(payload.Length >> 8);
            frame[2] = (byte)(payload.Length >> 16);
            frame[3] = (byte)(payload.Length >> 24);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private void ResetSession()
        {
            _latestFrame.Clear();
            ActiveProvider = null;
            BridgeVersion = string.Empty;
            Action ignored;
            while (_mainThreadActions.TryDequeue(out ignored))
            {
            }

            SetConnected(false);
        }

        private void SetConnected(bool connected)
        {
            var value = connected ? 1 : 0;
            if (Interlocked.Exchange(ref _connected, value) == value)
            {
                return;
            }

            _mainThreadActions.Enqueue(() => InvokeSafely(ConnectionChanged, connected));
        }

        private void ReportError(string message)
        {
            var captured = string.IsNullOrWhiteSpace(message) ? "Unknown Interaction IPC error." : message;
            var now = Stopwatch.GetTimestamp();
            lock (_errorGate)
            {
                LastError = captured;
                long previous;
                if (_recentErrors.TryGetValue(captured, out previous) && now - previous < ErrorRepeatWindowTicks)
                {
                    return;
                }

                _recentErrors[captured] = now;
            }

            _mainThreadActions.Enqueue(() => InvokeSafely(ErrorReceived, captured));
        }

        private static void InvokeSafely<T>(Action<T> handlers, T value)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(value);
                }
                catch
                {
                    // One Unity observer must not block later subscribers.
                }
            }
        }

        private long NextSequence()
        {
            return Interlocked.Increment(ref _sequence);
        }
    }
}
