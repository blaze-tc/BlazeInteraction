using Yuexin.Radar.Bridge.Wpf.Controls;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarRenderSchedulerTests
{
    [Fact]
    public void RequestRender_OneThousandRequests_QueuesOneDispatcherRender()
    {
        var context = new ManualRadarUiDispatcher();
        var timing = new ManualRadarRenderTiming();
        var renderCount = 0;
        using var subject = new RadarRenderScheduler(context, () => renderCount++, timing);

        for (var index = 0; index < 1_000; index++) subject.RequestRender();

        Assert.Equal(1, context.PendingCount);
        Assert.Equal(999, subject.CoalescedRequestCount);
        context.Drain();
        Assert.Equal(1, renderCount);
        Assert.Equal(1, subject.RenderedCount);
    }

    [Fact]
    public void RequestRender_NormalCadence_RendersAfterThirtyThreePointFourMilliseconds()
    {
        var context = new ManualRadarUiDispatcher();
        var timing = new ManualRadarRenderTiming();
        var renderCount = 0;
        using var subject = new RadarRenderScheduler(context, () => renderCount++, timing);
        subject.RequestRender();
        context.Drain();

        subject.RequestRender();
        timing.Advance(TimeSpan.FromMilliseconds(33.3));
        Assert.Equal(0, context.PendingCount);
        timing.Advance(TimeSpan.FromMilliseconds(0.1));
        Assert.Equal(1, context.PendingCount);
        context.Drain();

        Assert.Equal(2, renderCount);
    }

    [Fact]
    public void RequestRender_InteractionCadence_WaitsFiftyMilliseconds()
    {
        var context = new ManualRadarUiDispatcher();
        var timing = new ManualRadarRenderTiming();
        var renderCount = 0;
        using var subject = new RadarRenderScheduler(context, () => renderCount++, timing);
        subject.RequestRender();
        context.Drain();
        subject.BeginInteraction();

        subject.RequestRender();
        timing.Advance(TimeSpan.FromMilliseconds(49));
        Assert.Equal(0, context.PendingCount);
        timing.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(1, context.PendingCount);
        context.Drain();

        Assert.Equal(2, renderCount);
        subject.EndInteraction();
    }

    [Fact]
    public void Dispose_DropsPendingAndFutureRenderRequests()
    {
        var context = new ManualRadarUiDispatcher();
        var timing = new ManualRadarRenderTiming();
        var renderCount = 0;
        var subject = new RadarRenderScheduler(context, () => renderCount++, timing);
        subject.RequestRender();

        subject.Dispose();
        context.Drain();
        subject.RequestRender();
        timing.Advance(TimeSpan.FromSeconds(1));
        context.Drain();

        Assert.Equal(0, renderCount);
    }

    private sealed class ManualRadarRenderTiming : IRadarRenderTiming
    {
        private readonly List<ScheduledCallback> _callbacks = [];

        public TimeSpan Elapsed { get; private set; }

        public IDisposable Schedule(TimeSpan delay, Action callback)
        {
            var scheduled = new ScheduledCallback(Elapsed + delay, callback);
            _callbacks.Add(scheduled);
            return scheduled;
        }

        public void Advance(TimeSpan duration)
        {
            Elapsed += duration;
            foreach (var callback in _callbacks.Where(item => !item.IsDisposed && item.DueAt <= Elapsed).ToArray())
            {
                callback.Dispose();
                callback.Callback();
            }
            _callbacks.RemoveAll(item => item.IsDisposed);
        }

        private sealed class ScheduledCallback(TimeSpan dueAt, Action callback) : IDisposable
        {
            public TimeSpan DueAt { get; } = dueAt;
            public Action Callback { get; } = callback;
            public bool IsDisposed { get; private set; }
            public void Dispose() => IsDisposed = true;
        }
    }
}
