#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Blaze.Interaction
{
    public sealed class InteractionFrameDispatcher
    {
        private readonly Dictionary<PointKey, InteractionPoint> _pointsByKey =
            new Dictionary<PointKey, InteractionPoint>();
        private readonly List<InteractionPoint> _points = new List<InteractionPoint>();
        private readonly ReadOnlyCollection<InteractionPoint> _readOnlyPoints;

        public InteractionFrameDispatcher()
        {
            _readOnlyPoints = _points.AsReadOnly();
        }

        public IReadOnlyList<InteractionPoint> Points { get { return _readOnlyPoints; } }
        public bool IsConnected { get; private set; }
        public ProviderReferencePayload ActiveProvider { get; private set; }

        public event Action<InteractionPoint> PointAdded;
        public event Action<InteractionPoint> PointUpdated;
        public event Action<InteractionPoint> PointRemoved;
        public event Action<InteractionFrame> FrameReceived;
        public event Action<ProviderChangedPayload> ProviderChanged;
        public event Action<bool> ConnectionChanged;

        public void ApplyHelloAck(HelloAckPayload acknowledgement)
        {
            if (acknowledgement == null)
            {
                throw new ArgumentNullException(nameof(acknowledgement));
            }

            ActiveProvider = acknowledgement.ActiveProvider;
        }

        public void ApplyFrame(InteractionFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.Points == null)
            {
                throw new ArgumentException("InteractionFrame points are required.", nameof(frame));
            }

            for (var index = 0; index < frame.Points.Count; index++)
            {
                var point = frame.Points[index];
                if (point == null)
                {
                    continue;
                }

                var key = new PointKey(point.ProviderInstanceId, point.SurfaceId, point.Id);
                InteractionPoint existing;
                var exists = _pointsByKey.TryGetValue(key, out existing);
                if (point.Phase == InteractionPhase.Up || point.Phase == InteractionPhase.Cancel)
                {
                    if (exists)
                    {
                        _pointsByKey.Remove(key);
                        _points.Remove(existing);
                        InvokeSafely(PointRemoved, point);
                    }

                    continue;
                }

                if (exists)
                {
                    var listIndex = _points.IndexOf(existing);
                    _pointsByKey[key] = point;
                    _points[listIndex] = point;
                    InvokeSafely(PointUpdated, point);
                }
                else
                {
                    _pointsByKey.Add(key, point);
                    _points.Add(point);
                    InvokeSafely(PointAdded, point);
                }
            }

            InvokeSafely(FrameReceived, frame);
        }

        public void ApplyProviderChanged(ProviderChangedPayload change)
        {
            if (change == null)
            {
                throw new ArgumentNullException(nameof(change));
            }

            CancelAllPoints();
            ActiveProvider = change.ActiveProvider;
            InvokeSafely(ProviderChanged, change);
        }

        public void SetConnectionState(bool connected)
        {
            if (IsConnected == connected)
            {
                return;
            }

            if (!connected)
            {
                CancelAllPoints();
                ActiveProvider = null;
            }

            IsConnected = connected;
            InvokeSafely(ConnectionChanged, connected);
        }

        public void Reset()
        {
            CancelAllPoints();
            ActiveProvider = null;
            IsConnected = false;
        }

        private void CancelAllPoints()
        {
            if (_points.Count == 0)
            {
                return;
            }

            var snapshot = _points.ToArray();
            _points.Clear();
            _pointsByKey.Clear();
            for (var index = 0; index < snapshot.Length; index++)
            {
                InvokeSafely(PointRemoved, CopyWithPhase(snapshot[index], InteractionPhase.Cancel));
            }
        }

        private static InteractionPoint CopyWithPhase(InteractionPoint point, InteractionPhase phase)
        {
            return new InteractionPoint
            {
                Id = point.Id,
                SurfaceId = point.SurfaceId,
                ProviderId = point.ProviderId,
                ProviderInstanceId = point.ProviderInstanceId,
                SourceId = point.SourceId,
                Phase = phase,
                NormalizedPosition = point.NormalizedPosition,
                PixelPosition = point.PixelPosition,
                Confidence = point.Confidence,
                TimestampUnixMs = point.TimestampUnixMs,
                Extensions = point.Extensions
            };
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
                    // One Unity observer must not block later subscribers or corrupt point state.
                }
            }
        }

        private readonly struct PointKey : IEquatable<PointKey>
        {
            private readonly string _providerInstanceId;
            private readonly string _surfaceId;
            private readonly long _id;

            internal PointKey(string providerInstanceId, string surfaceId, long id)
            {
                _providerInstanceId = providerInstanceId ?? string.Empty;
                _surfaceId = surfaceId ?? string.Empty;
                _id = id;
            }

            public bool Equals(PointKey other)
            {
                return _id == other._id &&
                       string.Equals(_providerInstanceId, other._providerInstanceId, StringComparison.Ordinal) &&
                       string.Equals(_surfaceId, other._surfaceId, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is PointKey && Equals((PointKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = StringComparer.Ordinal.GetHashCode(_providerInstanceId);
                    hash = hash * 397 ^ StringComparer.Ordinal.GetHashCode(_surfaceId);
                    return hash * 397 ^ _id.GetHashCode();
                }
            }
        }
    }
}
