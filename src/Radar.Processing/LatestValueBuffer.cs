namespace Yuexin.Radar.Processing;

public sealed class LatestValueBuffer<T>
{
    private readonly object _gate = new();
    private T? _value;
    private bool _hasValue;

    public long DroppedValueCount { get; private set; }

    public void Write(T value)
    {
        lock (_gate)
        {
            if (_hasValue)
            {
                DroppedValueCount++;
            }

            _value = value;
            _hasValue = true;
        }
    }

    public bool TryRead(out T? value)
    {
        lock (_gate)
        {
            if (!_hasValue)
            {
                value = default;
                return false;
            }

            value = _value;
            _value = default;
            _hasValue = false;
            return true;
        }
    }
}
