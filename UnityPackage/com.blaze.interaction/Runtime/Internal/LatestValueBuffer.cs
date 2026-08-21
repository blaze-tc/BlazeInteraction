using System;
using System.Collections.Generic;
using System.Threading;

namespace Blaze.Interaction.Internal
{
    public sealed class LatestValueBuffer<T>
    {
        private readonly object _gate = new object();
        private T _value;
        private bool _hasValue;
        private long _droppedCount;

        public int PendingCount
        {
            get { lock (_gate) { return _hasValue ? 1 : 0; } }
        }

        public long DroppedCount
        {
            get { lock (_gate) { return _droppedCount; } }
        }

        public void Publish(T value)
        {
            if (ReferenceEquals(value, null))
            {
                throw new ArgumentNullException(nameof(value));
            }

            lock (_gate)
            {
                if (_hasValue)
                {
                    _droppedCount++;
                }

                _value = value;
                _hasValue = true;
            }
        }

        public bool TryConsume(out T value)
        {
            lock (_gate)
            {
                if (!_hasValue)
                {
                    value = default(T);
                    return false;
                }

                value = _value;
                _value = default(T);
                _hasValue = false;
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _value = default(T);
                _hasValue = false;
            }
        }
    }

    /// <summary>
    /// Bounded InteractionFrame queue. Down, Up, and Cancel edges retain FIFO order;
    /// adjacent hover/move-only frames are coalesced and an all-lifecycle queue applies
    /// backpressure to the pipe reader.
    /// </summary>
    public sealed class LifecycleFrameBuffer
    {
        private readonly object _gate = new object();
        private readonly LinkedList<InteractionFrame> _frames = new LinkedList<InteractionFrame>();
        private readonly int _capacity;
        private long _droppedCount;
        private long _clearGeneration;

        public LifecycleFrameBuffer(int capacity = 64)
        {
            if (capacity < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Lifecycle frame capacity must be at least two.");
            }

            _capacity = capacity;
        }

        public long DroppedCount
        {
            get { lock (_gate) { return _droppedCount; } }
        }

        public int PendingCount
        {
            get { lock (_gate) { return _frames.Count; } }
        }

        public void Publish(InteractionFrame value)
        {
            Publish(value, CancellationToken.None);
        }

        public void Publish(InteractionFrame value, CancellationToken cancellationToken)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            lock (_gate)
            {
                var generation = _clearGeneration;
                var lifecycle = ContainsLifecycleEdge(value);
                if (!lifecycle && _frames.Last != null && !ContainsLifecycleEdge(_frames.Last.Value))
                {
                    _frames.Last.Value = value;
                    _droppedCount++;
                    return;
                }

                while (_frames.Count >= _capacity)
                {
                    if (TryRemoveVisualFrame())
                    {
                        _droppedCount++;
                        break;
                    }

                    if (!lifecycle)
                    {
                        _droppedCount++;
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    Monitor.Wait(_gate, 50);
                    if (generation != _clearGeneration)
                    {
                        _droppedCount++;
                        return;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                _frames.AddLast(value);
            }
        }

        public bool TryConsume(out InteractionFrame value)
        {
            lock (_gate)
            {
                if (_frames.First == null)
                {
                    value = null;
                    return false;
                }

                value = _frames.First.Value;
                _frames.RemoveFirst();
                Monitor.PulseAll(_gate);
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _frames.Clear();
                _clearGeneration++;
                Monitor.PulseAll(_gate);
            }
        }

        private bool TryRemoveVisualFrame()
        {
            var current = _frames.First;
            while (current != null)
            {
                var next = current.Next;
                if (!ContainsLifecycleEdge(current.Value))
                {
                    _frames.Remove(current);
                    return true;
                }

                current = next;
            }

            return false;
        }

        private static bool ContainsLifecycleEdge(InteractionFrame frame)
        {
            if (frame == null || frame.Points == null)
            {
                return false;
            }

            for (var index = 0; index < frame.Points.Count; index++)
            {
                var point = frame.Points[index];
                if (point != null &&
                    (point.Phase == InteractionPhase.Down ||
                     point.Phase == InteractionPhase.Up ||
                     point.Phase == InteractionPhase.Cancel))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
