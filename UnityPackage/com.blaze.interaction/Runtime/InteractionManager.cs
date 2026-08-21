#nullable disable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Blaze.Interaction
{
    public sealed class InteractionManager : IDisposable
    {
        private static readonly InteractionManager SharedInstance = new InteractionManager();
        private readonly InteractionPipeClient _client;
        private readonly InteractionFrameDispatcher _dispatcher;
        private bool _disposed;

        public InteractionManager()
            : this(InteractionIpcProtocol.PipeName, 500, 250, 4000)
        {
        }

        public InteractionManager(
            string pipeName,
            int connectTimeoutMilliseconds,
            int reconnectDelayMilliseconds,
            int serverResponseTimeoutMilliseconds)
        {
            _client = new InteractionPipeClient(
                pipeName,
                connectTimeoutMilliseconds,
                reconnectDelayMilliseconds,
                serverResponseTimeoutMilliseconds);
            _dispatcher = new InteractionFrameDispatcher();
            _client.HelloAcknowledged += _dispatcher.ApplyHelloAck;
            _client.ProviderChangedReceived += _dispatcher.ApplyProviderChanged;
            _client.ConnectionChanged += _dispatcher.SetConnectionState;
        }

        public static InteractionManager Instance { get { return SharedInstance; } }
        public IReadOnlyList<InteractionPoint> Points { get { return _dispatcher.Points; } }
        public bool IsConnected { get { return _dispatcher.IsConnected; } }
        public ProviderReferencePayload ActiveProvider { get { return _dispatcher.ActiveProvider; } }
        public long DroppedFrameCount { get { return _client.DroppedFrameCount; } }

        public event Action<InteractionPoint> PointAdded
        {
            add { _dispatcher.PointAdded += value; }
            remove { _dispatcher.PointAdded -= value; }
        }

        public event Action<InteractionPoint> PointUpdated
        {
            add { _dispatcher.PointUpdated += value; }
            remove { _dispatcher.PointUpdated -= value; }
        }

        public event Action<InteractionPoint> PointRemoved
        {
            add { _dispatcher.PointRemoved += value; }
            remove { _dispatcher.PointRemoved -= value; }
        }

        public event Action<ProviderChangedPayload> ProviderChanged
        {
            add { _dispatcher.ProviderChanged += value; }
            remove { _dispatcher.ProviderChanged -= value; }
        }

        public event Action<bool> ConnectionChanged
        {
            add { _dispatcher.ConnectionChanged += value; }
            remove { _dispatcher.ConnectionChanged -= value; }
        }

        public event Action<string> ErrorReceived
        {
            add { _client.ErrorReceived += value; }
            remove { _client.ErrorReceived -= value; }
        }

        public void Connect(HelloPayload hello)
        {
            ThrowIfDisposed();
            _client.Start(hello);
        }

        public void Tick()
        {
            ThrowIfDisposed();
            _client.DrainMainThreadEvents();
            InteractionFrame frame;
            while (_client.TryConsumeLatestFrame(out frame))
            {
                _dispatcher.ApplyFrame(frame);
            }
        }

        public async Task DisconnectAsync()
        {
            if (_disposed)
            {
                return;
            }

            await _client.StopAsync().ConfigureAwait(false);
            _client.DrainMainThreadEvents();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _client.HelloAcknowledged -= _dispatcher.ApplyHelloAck;
            _client.ProviderChangedReceived -= _dispatcher.ApplyProviderChanged;
            _client.ConnectionChanged -= _dispatcher.SetConnectionState;
            _client.Dispose();
            _dispatcher.Reset();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InteractionManager));
            }
        }
    }
}
