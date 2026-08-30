using System.Diagnostics;

namespace Yuexin.Radar.Bridge.Wpf.Controls;

internal interface IRadarRenderTiming
{
    TimeSpan Elapsed { get; }
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class RadarRenderScheduler : IDisposable
{
    private static readonly TimeSpan NormalInterval = TimeSpan.FromSeconds(1d / 30d);
    private static readonly TimeSpan InteractionInterval = TimeSpan.FromSeconds(1d / 20d);
    private readonly object _gate = new();
    private readonly SynchronizationContext _context;
    private readonly Action _render;
    private readonly IRadarRenderTiming _timing;
    private IDisposable? _timerRegistration;
    private TimeSpan _lastRenderedAt;
    private long _renderedCount;
    private long _coalescedRequestCount;
    private bool _hasRendered;
    private bool _renderRequested;
    private bool _dispatcherCallbackPending;
    private bool _isInteracting;
    private bool _disposed;

    public RadarRenderScheduler(SynchronizationContext context, Action render)
        : this(context, render, new SystemRadarRenderTiming())
    {
    }

    internal RadarRenderScheduler(
        SynchronizationContext context,
        Action render,
        IRadarRenderTiming timing)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _timing = timing ?? throw new ArgumentNullException(nameof(timing));
    }

    public long RenderedCount
    {
        get { lock (_gate) return _renderedCount; }
    }

    public long CoalescedRequestCount
    {
        get { lock (_gate) return _coalescedRequestCount; }
    }

    public void RequestRender()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_renderRequested)
            {
                _coalescedRequestCount++;
                return;
            }

            _renderRequested = true;
            ScheduleNextLocked();
        }
    }

    public void BeginInteraction()
    {
        lock (_gate)
        {
            if (_disposed || _isInteracting) return;
            _isInteracting = true;
            RescheduleTimerLocked();
        }
    }

    public void EndInteraction()
    {
        lock (_gate)
        {
            if (_disposed || !_isInteracting) return;
            _isInteracting = false;
            RescheduleTimerLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _renderRequested = false;
            _timerRegistration?.Dispose();
            _timerRegistration = null;
        }
    }

    private void ScheduleNextLocked()
    {
        if (_dispatcherCallbackPending || _timerRegistration is not null || !_renderRequested) return;
        if (!_hasRendered || _timing.Elapsed - _lastRenderedAt >= CurrentInterval)
        {
            _dispatcherCallbackPending = true;
            _context.Post(static state => ((RadarRenderScheduler)state!).ExecutePostedRender(), this);
            return;
        }

        var delay = CurrentInterval - (_timing.Elapsed - _lastRenderedAt);
        _timerRegistration = _timing.Schedule(delay, OnTimerElapsed);
    }

    private void RescheduleTimerLocked()
    {
        if (_timerRegistration is null) return;
        _timerRegistration.Dispose();
        _timerRegistration = null;
        ScheduleNextLocked();
    }

    private void OnTimerElapsed()
    {
        lock (_gate)
        {
            _timerRegistration?.Dispose();
            _timerRegistration = null;
            if (_disposed || !_renderRequested || _dispatcherCallbackPending) return;
            _dispatcherCallbackPending = true;
            _context.Post(static state => ((RadarRenderScheduler)state!).ExecutePostedRender(), this);
        }
    }

    private void ExecutePostedRender()
    {
        lock (_gate)
        {
            _dispatcherCallbackPending = false;
            if (_disposed || !_renderRequested) return;
            if (_hasRendered && _timing.Elapsed - _lastRenderedAt < CurrentInterval)
            {
                ScheduleNextLocked();
                return;
            }

            _renderRequested = false;
            _hasRendered = true;
            _lastRenderedAt = _timing.Elapsed;
            _renderedCount++;
        }

        _render();
    }

    private TimeSpan CurrentInterval => _isInteracting ? InteractionInterval : NormalInterval;

    private sealed class SystemRadarRenderTiming : IRadarRenderTiming
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public IDisposable Schedule(TimeSpan delay, Action callback) =>
            new Timer(static state => ((Action)state!).Invoke(), callback, delay, Timeout.InfiniteTimeSpan);
    }
}
