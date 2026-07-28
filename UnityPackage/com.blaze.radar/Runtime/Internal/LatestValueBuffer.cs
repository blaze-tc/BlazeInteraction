#nullable disable

using System;
using System.Collections.Generic;
using System.Threading;

namespace Blaze.Radar.Internal
{
    /// <summary>
    /// Bounded queue for PointerBatch traffic. Down/Up batches retain FIFO order; adjacent visual-only
    /// batches are coalesced and an all-lifecycle queue applies backpressure to the pipe reader.
    /// </summary>
    public sealed class LifecycleBatchBuffer
    {
        private readonly object _gate = new object();
        private readonly LinkedList<RadarPointerBatchPayload> _batches = new LinkedList<RadarPointerBatchPayload>();
        private readonly int _capacity;
        private long _droppedCount;
        private long _clearGeneration;

        public LifecycleBatchBuffer(int capacity = 64)
        {
            if (capacity < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Lifecycle batch capacity must be at least two.");
            }

            _capacity = capacity;
        }

        public long DroppedCount
        {
            get { lock (_gate) { return _droppedCount; } }
        }

        public int PendingCount
        {
            get { lock (_gate) { return _batches.Count; } }
        }

        public void Publish(RadarPointerBatchPayload value)
        {
            Publish(value, CancellationToken.None);
        }

        public void Publish(RadarPointerBatchPayload value, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                var generation = _clearGeneration;
                var lifecycle = ContainsLifecycleEdge(value);
                if (!lifecycle && _batches.Last != null && !ContainsLifecycleEdge(_batches.Last.Value))
                {
                    _batches.Last.Value = value;
                    _droppedCount++;
                    return;
                }

                while (_batches.Count >= _capacity)
                {
                    if (TryRemoveVisualBatch())
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
                _batches.AddLast(value);
            }
        }

        public bool TryConsume(out RadarPointerBatchPayload value)
        {
            lock (_gate)
            {
                if (_batches.First == null)
                {
                    value = null;
                    return false;
                }

                value = _batches.First.Value;
                _batches.RemoveFirst();
                Monitor.PulseAll(_gate);
                return true;
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _batches.Clear();
                _clearGeneration++;
                Monitor.PulseAll(_gate);
            }
        }

        private bool TryRemoveVisualBatch()
        {
            var current = _batches.First;
            while (current != null)
            {
                var next = current.Next;
                if (!ContainsLifecycleEdge(current.Value))
                {
                    _batches.Remove(current);
                    return true;
                }

                current = next;
            }

            return false;
        }

        private static bool ContainsLifecycleEdge(RadarPointerBatchPayload batch)
        {
            if (batch == null || batch.screens == null)
            {
                return false;
            }

            for (var screenIndex = 0; screenIndex < batch.screens.Count; screenIndex++)
            {
                var frame = batch.screens[screenIndex];
                if (frame == null || frame.pointers == null)
                {
                    continue;
                }

                for (var pointerIndex = 0; pointerIndex < frame.pointers.Count; pointerIndex++)
                {
                    var pointer = frame.pointers[pointerIndex];
                    if (pointer != null &&
                        (pointer.phase == RadarPointerPhase.Down || pointer.phase == RadarPointerPhase.Up))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
