namespace Yuexin.Radar.Bridge.Wpf.Services;

public sealed class RadarReplayGate
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool> _signal = NewSignal();
    private bool _isPaused;
    private int _stepBudget;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _isPaused;
            }
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _isPaused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _isPaused = false;
            _stepBudget = 0;
            Pulse();
        }
    }

    public void Step()
    {
        lock (_gate)
        {
            _isPaused = true;
            _stepBudget++;
            Pulse();
        }
    }

    public async Task WaitForFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_gate)
            {
                if (!_isPaused)
                {
                    return;
                }

                if (_stepBudget > 0)
                {
                    _stepBudget--;
                    return;
                }

                waitTask = _signal.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Pulse()
    {
        var previous = _signal;
        _signal = NewSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
