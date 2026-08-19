using Yuexin.Radar.Bridge.Wpf.Services;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarReplayGateTests
{
    [Fact]
    public async Task PauseStepAndResume_ControlFramePermission()
    {
        var gate = new RadarReplayGate();
        gate.Pause();

        var firstWait = gate.WaitForFrameAsync(CancellationToken.None);
        Assert.False(firstWait.IsCompleted);

        gate.Step();
        await firstWait;
        var secondWait = gate.WaitForFrameAsync(CancellationToken.None);
        Assert.False(secondWait.IsCompleted);

        gate.Resume();
        await secondWait;
        Assert.False(gate.IsPaused);
    }
}
