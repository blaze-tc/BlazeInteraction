using System.Diagnostics;

namespace Blaze.Provider.CameraVision;

internal interface IMonotonicClock
{
    TimeSpan Elapsed { get; }
}

internal sealed class StopwatchMonotonicClock : IMonotonicClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    public TimeSpan Elapsed => _stopwatch.Elapsed;
}

internal sealed class LatestUiFrameScheduler<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeSpan _minimumInterval;
    private readonly ICameraUiDispatcher _dispatcher;
    private readonly Action<T> _render;
    private readonly IMonotonicClock _clock;
    private T? _pending;
    private bool _hasPending;
    private bool _dispatchScheduled;
    private bool _hasRendered;
    private TimeSpan _lastRenderedAt;
    private long _renderedCount;
    private long _supersededCount;
    private int _disposed;

    internal LatestUiFrameScheduler(
        TimeSpan minimumInterval,
        ICameraUiDispatcher dispatcher,
        Action<T> render,
        IMonotonicClock? clock = null)
    {
        if (minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        _minimumInterval = minimumInterval;
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _clock = clock ?? new StopwatchMonotonicClock();
    }

    internal long RenderedCount => Interlocked.Read(ref _renderedCount);
    internal long SupersededCount => Interlocked.Read(ref _supersededCount);

    internal void Offer(T frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var schedule = false;
        lock (_gate)
        {
            if (_disposed != 0) return;
            if (_hasPending) Interlocked.Increment(ref _supersededCount);
            _pending = frame;
            _hasPending = true;
            if (!_dispatchScheduled)
            {
                _dispatchScheduled = true;
                schedule = true;
            }
        }

        if (schedule) _ = DispatchAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
        {
            _pending = default;
            _hasPending = false;
        }
    }

    private async Task DispatchAsync()
    {
        try
        {
            await _dispatcher.InvokeAsync(DrainOnDispatcher).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _dispatchScheduled = false;
            }
        }
    }

    private void DrainOnDispatcher()
    {
        T? frame = default;
        TimeSpan delay = TimeSpan.Zero;
        lock (_gate)
        {
            if (_disposed != 0 || !_hasPending)
            {
                _dispatchScheduled = false;
                return;
            }

            if (_hasRendered)
            {
                var elapsed = _clock.Elapsed - _lastRenderedAt;
                if (elapsed < _minimumInterval)
                {
                    delay = _minimumInterval - elapsed;
                }
            }

            if (delay == TimeSpan.Zero)
            {
                frame = _pending;
                _pending = default;
                _hasPending = false;
                _lastRenderedAt = _clock.Elapsed;
                _hasRendered = true;
                _dispatchScheduled = false;
            }
        }

        if (delay > TimeSpan.Zero)
        {
            _ = DelayAndDispatchAsync(delay);
            return;
        }

        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                Interlocked.Increment(ref _renderedCount);
                _render(frame!);
            }
            catch
            {
            }
        }

        var schedule = false;
        lock (_gate)
        {
            if (_disposed == 0 && _hasPending && !_dispatchScheduled)
            {
                _dispatchScheduled = true;
                schedule = true;
            }
        }
        if (schedule) _ = DispatchAsync();
    }

    private async Task DelayAndDispatchAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
            if (Volatile.Read(ref _disposed) == 0)
            {
                await DispatchAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                _dispatchScheduled = false;
            }
        }
    }
}
