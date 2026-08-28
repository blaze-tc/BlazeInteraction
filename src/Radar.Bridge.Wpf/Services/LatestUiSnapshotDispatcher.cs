namespace Yuexin.Radar.Bridge.Wpf.Services;

internal sealed class LatestUiSnapshotDispatcher<TKey, TValue> : IDisposable
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly SynchronizationContext _context;
    private readonly Action<TKey, TValue> _apply;
    private Dictionary<TKey, TValue> _pending = [];
    private long _supersededCount;
    private bool _callbackScheduled;
    private bool _disposed;

    public LatestUiSnapshotDispatcher(
        SynchronizationContext context,
        Action<TKey, TValue> apply)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
    }

    public long SupersededCount
    {
        get
        {
            lock (_gate)
            {
                return _supersededCount;
            }
        }
    }

    public int PendingKeyCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public void Offer(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pending.ContainsKey(key))
            {
                _supersededCount++;
            }

            _pending[key] = value;
            if (_callbackScheduled)
            {
                return;
            }

            _callbackScheduled = true;
            _context.Post(static state => ((LatestUiSnapshotDispatcher<TKey, TValue>)state!).Drain(), this);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending.Clear();
        }
    }

    private void Drain()
    {
        Dictionary<TKey, TValue> batch;
        lock (_gate)
        {
            if (_disposed)
            {
                _callbackScheduled = false;
                _pending.Clear();
                return;
            }

            batch = _pending;
            _pending = [];
        }

        try
        {
            foreach (var (key, value) in batch)
            {
                _apply(key, value);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0)
                {
                    _callbackScheduled = false;
                    if (_disposed)
                    {
                        _pending.Clear();
                    }
                }
                else
                {
                    _context.Post(static state => ((LatestUiSnapshotDispatcher<TKey, TValue>)state!).Drain(), this);
                }
            }
        }
    }
}
