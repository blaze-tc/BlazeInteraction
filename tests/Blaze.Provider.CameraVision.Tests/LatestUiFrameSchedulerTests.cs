using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class LatestUiFrameSchedulerTests
{
    [Fact]
    public void WpfDispatcher_QueuesWorkPostedFromDispatcherThread()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                var subject = new WpfCameraUiDispatcher(dispatcher);
                var invoked = false;

                var operation = subject.InvokeAsync(() => invoked = true);

                Assert.False(invoked);
                dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(operation.IsCompletedSuccessfully);
                Assert.True(invoked);
                dispatcher.InvokeShutdown();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF dispatcher test timed out.");
        failure?.Throw();
    }

    [Fact]
    public async Task Offer_BurstBeforeDispatch_RendersOnlyLatestFrame()
    {
        var clock = new ManualClock();
        var dispatcher = new RecordingCameraUiDispatcher();
        var rendered = new List<int>();
        using var scheduler = new LatestUiFrameScheduler<int>(
            TimeSpan.FromMilliseconds(66.666),
            dispatcher,
            rendered.Add,
            clock);

        scheduler.Offer(1);
        scheduler.Offer(2);
        scheduler.Offer(3);
        await dispatcher.DrainAsync();

        Assert.Equal(new[] { 3 }, rendered);
        Assert.Equal(1, scheduler.RenderedCount);
        Assert.Equal(2, scheduler.SupersededCount);
        Assert.Equal(0, dispatcher.PendingCount);
    }

    [Fact]
    public async Task Dispose_DiscardsPendingFrameAndPreventsFutureRendering()
    {
        var dispatcher = new RecordingCameraUiDispatcher();
        var rendered = new List<int>();
        var scheduler = new LatestUiFrameScheduler<int>(
            TimeSpan.Zero,
            dispatcher,
            rendered.Add,
            new ManualClock());
        scheduler.Offer(1);

        scheduler.Dispose();
        scheduler.Offer(2);
        await dispatcher.DrainAsync();

        Assert.Empty(rendered);
    }

    private sealed class ManualClock : IMonotonicClock
    {
        public TimeSpan Elapsed { get; private set; }
        public void Advance(TimeSpan duration) => Elapsed += duration;
    }

    private sealed class RecordingCameraUiDispatcher : ICameraUiDispatcher
    {
        private readonly Queue<(Action Action, TaskCompletionSource Completion)> _pending = new();
        public int PendingCount => _pending.Count;

        public Task InvokeAsync(Action action)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((action, completion));
            return completion.Task;
        }

        public async Task DrainAsync()
        {
            while (_pending.Count > 0)
            {
                var (action, completion) = _pending.Dequeue();
                action();
                completion.TrySetResult();
                await Task.Yield();
            }
        }
    }
}
