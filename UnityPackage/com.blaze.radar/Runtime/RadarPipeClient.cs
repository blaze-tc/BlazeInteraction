#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Blaze.Radar.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Blaze.Radar
{
    internal interface IRadarPipeClient : IDisposable
    {
        bool IsConnected { get; }
        long DroppedBatchCount { get; }
        event Action<bool> ConnectionChanged;
        event Action<string> ErrorReceived;
        void Start(RadarHelloPayload hello);
        bool TryConsumeLatestBatch(out RadarPointerBatchPayload batch);
        void DrainMainThreadEvents();
        Task StopAsync();
    }

    public sealed class RadarPipeClient : IRadarPipeClient
    {
        private static readonly IReadOnlyList<RadarScreenInfo> NoScreens =
            new ReadOnlyCollection<RadarScreenInfo>(new List<RadarScreenInfo>());

        private readonly string _pipeName;
        private readonly int _connectTimeoutMilliseconds;
        private readonly int _reconnectDelayMilliseconds;
        private readonly int _serverResponseTimeoutMilliseconds;
        private readonly LifecycleBatchBuffer _latestBatch = new LifecycleBatchBuffer();
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly object _lifecycleSync = new object();
        private readonly object _errorSync = new object();
        private readonly Dictionary<string, long> _recentErrorTicks = new Dictionary<string, long>();
        private static readonly long ErrorRepeatWindowTimestampTicks = Stopwatch.Frequency * 10L;
        private CancellationTokenSource _cancellation;
        private Task _runTask;
        private Task _stopTask;
        private NamedPipeClientStream _activePipe;
        private long _sequence;
        private int _connected;
        private RadarHelloPayload _hello;
        private bool _disposed;

        public RadarPipeClient(
            string pipeName,
            int connectTimeoutMilliseconds,
            int reconnectDelayMilliseconds,
            int serverResponseTimeoutMilliseconds = 4000)
        {
            _pipeName = string.IsNullOrWhiteSpace(pipeName) ? "Yuexin.RadarBridge" : pipeName;
            _connectTimeoutMilliseconds = Math.Max(50, connectTimeoutMilliseconds);
            _reconnectDelayMilliseconds = Math.Max(50, reconnectDelayMilliseconds);
            _serverResponseTimeoutMilliseconds = Math.Max(1000, serverResponseTimeoutMilliseconds);
        }

        public bool IsConnected { get { return Volatile.Read(ref _connected) != 0; } }
        public long DroppedBatchCount { get { return _latestBatch.DroppedCount; } }
        public long DroppedFrameCount { get { return DroppedBatchCount; } }
        public string BridgeVersion { get; private set; } = string.Empty;
        public int AcknowledgedProtocolVersion { get; private set; }
        public string Capability { get; private set; } = string.Empty;
        public bool HasRunningSensors { get; private set; }
        public IReadOnlyList<RadarScreenInfo> AcknowledgedScreens { get; private set; } = NoScreens;
        public string DeviceModel { get { return string.Empty; } }
        public string LastError { get; private set; } = string.Empty;

        public event Action<bool> ConnectionChanged;
        public event Action<string> ErrorReceived;

        public void Start(RadarHelloPayload hello)
        {
            if (hello == null)
            {
                throw new ArgumentNullException(nameof(hello));
            }

            lock (_lifecycleSync)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(RadarPipeClient));
                }

                if (_runTask != null)
                {
                    return;
                }

                _hello = hello;
                _stopTask = null;
                var cancellation = new CancellationTokenSource();
                _cancellation = cancellation;
                _runTask = Task.Run(() => RunAsync(cancellation.Token));
            }
        }

        public bool TryConsumeLatestBatch(out RadarPointerBatchPayload batch)
        {
            return _latestBatch.TryConsume(out batch);
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
            lock (_lifecycleSync)
            {
                if (_runTask == null)
                {
                    _latestBatch.Clear();
                    SetConnected(false);
                    return _stopTask ?? Task.CompletedTask;
                }

                if (_stopTask == null)
                {
                    _stopTask = StopCoreAsync(_runTask, _cancellation);
                }

                return _stopTask;
            }
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
                // Cooperative cancellation is the normal stop path.
            }
            catch (ObjectDisposedException)
            {
                // Disposing the pipe unblocks an in-flight read.
            }
            finally
            {
                if (cancellation != null)
                {
                    cancellation.Dispose();
                }

                _latestBatch.Clear();
                SetConnected(false);

                lock (_lifecycleSync)
                {
                    if (ReferenceEquals(_runTask, runTask))
                    {
                        _cancellation = null;
                        _runTask = null;
                    }
                }
            }
        }

        public async Task SendShutdownAsync()
        {
            var pipe = _activePipe;
            if (pipe == null || !pipe.IsConnected)
            {
                return;
            }

            await WriteEnvelopeAsync(
                pipe,
                RadarIpcProtocol.Create(RadarIpcMessageType.Shutdown, NextSequence(), new object()),
                CancellationToken.None).ConfigureAwait(false);
        }

        public void Dispose()
        {
            lock (_lifecycleSync)
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

        public static async Task<bool> CanConnectAsync(
            string pipeName,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       pipeName,
                       PipeDirection.InOut,
                       PipeOptions.Asynchronous))
            {
                try
                {
                    await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                    return pipe.IsConnected;
                }
                catch (TimeoutException)
                {
                    return false;
                }
                catch (IOException)
                {
                    return false;
                }
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
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
                            RadarIpcProtocol.Create(RadarIpcMessageType.Hello, NextSequence(), _hello),
                            cancellationToken).ConfigureAwait(false);

                        using (var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                        {
                            var heartbeatTask = SendHeartbeatAsync(pipe, sessionCancellation.Token);
                            try
                            {
                                await ReadMessagesAsync(pipe, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                sessionCancellation.Cancel();
                                try
                                {
                                    await heartbeatTask.ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    // Session cancellation stops the heartbeat loop.
                                }
                                catch (ObjectDisposedException)
                                {
                                    // Stop may dispose the pipe while heartbeat is writing.
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
                        // Waiting for RadarBridge is the normal cold-start and reconnect state.
                        // The connection state already communicates that status to Unity; repeating
                        // the same Named Pipe timeout every retry only hides actionable errors.
                    }
                    catch (Exception exception) when (
                        exception is IOException ||
                        exception is InvalidDataException ||
                        exception is TimeoutException ||
                        exception is UnauthorizedAccessException ||
                        exception is JsonException)
                    {
                        ReportError(exception.Message);
                    }
                    finally
                    {
                        Interlocked.CompareExchange(ref _activePipe, null, pipe);
                        _latestBatch.Clear();
                        SetConnected(false);
                    }
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
                            throw new EndOfStreamException("RadarBridge closed the Named Pipe connection.");
                        }

                        var payloads = decoder.Append(buffer, 0, count);
                        for (var index = 0; index < payloads.Count; index++)
                        {
                            var json = Encoding.UTF8.GetString(payloads[index]);
                            var envelope = JsonConvert.DeserializeObject<RadarIpcEnvelope>(json);
                            if (envelope == null)
                            {
                                throw new InvalidDataException("RadarBridge sent an empty IPC envelope.");
                            }

                            if (envelope.protocolVersion != RadarIpcProtocol.Version)
                            {
                                throw new InvalidDataException(
                                    "IPC protocol " + envelope.protocolVersion +
                                    " is incompatible with Unity SDK protocol " + RadarIpcProtocol.Version + ".");
                            }

                            acknowledged = HandleEnvelope(envelope, acknowledged, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            "RadarBridge did not respond for " + _serverResponseTimeoutMilliseconds + " ms.");
                    }
                }
            }
        }

        private bool HandleEnvelope(RadarIpcEnvelope envelope, bool acknowledged, CancellationToken cancellationToken)
        {
            switch (envelope.messageType)
            {
                case RadarIpcMessageType.HelloAck:
                    if (acknowledged)
                    {
                        throw new InvalidDataException("RadarBridge sent a duplicate HelloAck for one IPC session.");
                    }

                    var helloAck = ReadRequiredPayload<RadarHelloAckPayload>(envelope, "HelloAck");
                    if (helloAck.protocolVersion != RadarIpcProtocol.Version)
                    {
                        throw new InvalidDataException(
                            "RadarBridge acknowledgement protocol " + helloAck.protocolVersion +
                            " is incompatible with Unity SDK protocol " + RadarIpcProtocol.Version + ".");
                    }

                    BridgeVersion = helloAck.bridgeVersion ?? string.Empty;
                    AcknowledgedProtocolVersion = helloAck.protocolVersion;
                    Capability = helloAck.capability ?? string.Empty;
                    HasRunningSensors = helloAck.connected;
                    AcknowledgedScreens = CopyScreens(helloAck.screens);
                    LastError = string.Empty;
                    SetConnected(true);
                    return true;

                case RadarIpcMessageType.PointerBatch:
                    if (!acknowledged)
                    {
                        throw new InvalidDataException("RadarBridge sent PointerBatch before HelloAck completed the IPC handshake.");
                    }

                    var batch = ReadRequiredPayload<RadarPointerBatchPayload>(envelope, "PointerBatch");
                    if (batch.screens == null)
                    {
                        throw new InvalidDataException("RadarBridge sent an invalid PointerBatch payload: screens is required.");
                    }

                    _latestBatch.Publish(batch, cancellationToken);
                    return acknowledged;

                case RadarIpcMessageType.PointerFrame:
                    ReportError("RadarBridge sent legacy PointerFrame on IPC v2.");
                    return acknowledged;

                case RadarIpcMessageType.Error:
                    var error = ReadRequiredPayload<RadarErrorPayload>(envelope, "Error");
                    ReportError(error.message ?? "RadarBridge returned an unspecified error.");
                    return acknowledged;

                default:
                    return acknowledged;
            }
        }

        private static T ReadRequiredPayload<T>(RadarIpcEnvelope envelope, string payloadName)
            where T : class
        {
            if (envelope.payload == null || envelope.payload.Type == JTokenType.Null)
            {
                throw new InvalidDataException("RadarBridge sent an empty or invalid " + payloadName + " payload.");
            }

            try
            {
                var value = envelope.payload.ToObject<T>();
                if (value == null)
                {
                    throw new InvalidDataException("RadarBridge sent an empty or invalid " + payloadName + " payload.");
                }

                return value;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "RadarBridge sent an invalid " + payloadName + " payload: " + exception.Message,
                    exception);
            }
        }

        private static IReadOnlyList<RadarScreenInfo> CopyScreens(List<RadarScreenInfo> screens)
        {
            if (screens == null || screens.Count == 0)
            {
                return NoScreens;
            }

            return new ReadOnlyCollection<RadarScreenInfo>(new List<RadarScreenInfo>(screens));
        }

        private async Task SendHeartbeatAsync(Stream stream, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                await WriteEnvelopeAsync(
                    stream,
                    RadarIpcProtocol.Create(
                        RadarIpcMessageType.Ping,
                        NextSequence(),
                        new RadarPingPayload
                        {
                            clientTimestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        }),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WriteEnvelopeAsync(
            Stream stream,
            RadarIpcEnvelope envelope,
            CancellationToken cancellationToken)
        {
            var payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(envelope));
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
            var captured = string.IsNullOrWhiteSpace(message) ? "Unknown IPC error." : message;
            var nowTicks = Stopwatch.GetTimestamp();
            lock (_errorSync)
            {
                LastError = captured;
                long previousTicks;
                if (_recentErrorTicks.TryGetValue(captured, out previousTicks) &&
                    nowTicks - previousTicks < ErrorRepeatWindowTimestampTicks)
                {
                    return;
                }

                _recentErrorTicks[captured] = nowTicks;
                if (_recentErrorTicks.Count > 32)
                {
                    var oldestMessage = string.Empty;
                    var oldestTicks = long.MaxValue;
                    foreach (var entry in _recentErrorTicks)
                    {
                        if (entry.Value < oldestTicks)
                        {
                            oldestMessage = entry.Key;
                            oldestTicks = entry.Value;
                        }
                    }

                    if (!string.IsNullOrEmpty(oldestMessage))
                    {
                        _recentErrorTicks.Remove(oldestMessage);
                    }
                }
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
                    // One Unity callback must not block later subscribers.
                }
            }
        }

        private long NextSequence()
        {
            return Interlocked.Increment(ref _sequence);
        }
    }
}
