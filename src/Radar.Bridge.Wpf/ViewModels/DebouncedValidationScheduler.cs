namespace Yuexin.Radar.Bridge.Wpf.ViewModels;

internal interface IValidationScheduler : IDisposable
{
    void Schedule(Action validation);
    void Flush();
}

internal sealed class DebouncedValidationScheduler : IValidationScheduler
{
    private readonly object _gate = new();
    private readonly TimeSpan _delay;
    private readonly SynchronizationContext _context;
    private readonly Timer _timer;
    private Action? _pending;
    private long _version;
    private bool _disposed;

    public DebouncedValidationScheduler(TimeSpan delay, SynchronizationContext context)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        _delay = delay;
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _timer = new Timer(OnTimerElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Schedule(Action validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = validation;
            _version++;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        Action? validation;
        lock (_gate)
        {
            if (_disposed || _pending is null)
            {
                return;
            }

            validation = _pending;
            _pending = null;
            _version++;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        validation();
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
            _pending = null;
            _version++;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _timer.Dispose();
    }

    private void OnTimerElapsed(object? _)
    {
        long version;
        lock (_gate)
        {
            if (_disposed || _pending is null)
            {
                return;
            }

            version = _version;
        }

        _context.Post(state => ExecutePosted((long)state!), version);
    }

    private void ExecutePosted(long version)
    {
        Action? validation;
        lock (_gate)
        {
            if (_disposed || version != _version || _pending is null)
            {
                return;
            }

            validation = _pending;
            _pending = null;
        }

        validation();
    }
}
