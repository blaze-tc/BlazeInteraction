using System;

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
}
