using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class PointerStateMachineTests
{
    [Fact]
    public void Touch_EmitsDownMoveAndDelayedUp()
    {
        var machine = new PointerStateMachine(new RadarPointerOptions
        {
            Mode = RadarInteractionMode.Touch,
            LostFrames = 3,
            MinimumPressDuration = TimeSpan.Zero
        });
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1000);

        Assert.Equal(RadarPointerPhase.Down, Assert.Single(machine.Update([Target(7, 0.2f, 0.3f)], start)).Phase);
        Assert.Equal(RadarPointerPhase.Move, Assert.Single(machine.Update([Target(7, 0.25f, 0.3f)], start.AddMilliseconds(16))).Phase);
        Assert.Empty(machine.Update([], start.AddMilliseconds(32)));
        Assert.Empty(machine.Update([], start.AddMilliseconds(48)));
        var up = Assert.Single(machine.Update([], start.AddMilliseconds(64)));

        Assert.Equal(RadarPointerPhase.Up, up.Phase);
        Assert.Equal(7, up.PointerId);
        Assert.Equal(0.25f, up.NormalizedX);
    }

    [Fact]
    public void Dwell_EmitsClickPairAfterStableDuration()
    {
        var machine = new PointerStateMachine(new RadarPointerOptions
        {
            Mode = RadarInteractionMode.Dwell,
            LostFrames = 2,
            DwellDuration = TimeSpan.FromMilliseconds(800),
            DwellRadiusNormalized = 0.03f
        });
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1000);

        Assert.Equal(RadarPointerPhase.Hover, Assert.Single(machine.Update([Target(1, 0.5f, 0.5f)], start)).Phase);
        Assert.Equal(RadarPointerPhase.Hover, Assert.Single(machine.Update([Target(1, 0.51f, 0.5f)], start.AddMilliseconds(400))).Phase);
        var click = machine.Update([Target(1, 0.5f, 0.5f)], start.AddMilliseconds(800));

        Assert.Equal([RadarPointerPhase.Down, RadarPointerPhase.Up], click.Select(pointer => pointer.Phase));
    }

    [Fact]
    public void Dwell_MovementOutsideRadiusRestartsTimer()
    {
        var machine = new PointerStateMachine(new RadarPointerOptions
        {
            Mode = RadarInteractionMode.Dwell,
            DwellDuration = TimeSpan.FromMilliseconds(800),
            DwellRadiusNormalized = 0.03f
        });
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        machine.Update([Target(1, 0.2f, 0.2f)], start);
        machine.Update([Target(1, 0.3f, 0.2f)], start.AddMilliseconds(700));

        var output = machine.Update([Target(1, 0.3f, 0.2f)], start.AddMilliseconds(900));

        Assert.Equal(RadarPointerPhase.Hover, Assert.Single(output).Phase);
    }

    [Fact]
    public void EnterTrigger_FiresOnceUntilTargetLeavesAndReenters()
    {
        var machine = new PointerStateMachine(new RadarPointerOptions
        {
            Mode = RadarInteractionMode.EnterTrigger,
            LostFrames = 1
        });
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1000);

        Assert.Equal(2, machine.Update([Target(4, 0.2f, 0.2f)], start).Count);
        Assert.Empty(machine.Update([Target(4, 0.2f, 0.2f)], start.AddMilliseconds(16)));
        machine.Update([], start.AddMilliseconds(32));
        Assert.Equal(2, machine.Update([Target(4, 0.2f, 0.2f)], start.AddMilliseconds(48)).Count);
    }

    [Fact]
    public void HoverOnly_IgnoresUnconfirmedTargets()
    {
        var machine = new PointerStateMachine(new RadarPointerOptions { Mode = RadarInteractionMode.HoverOnly });
        var time = DateTimeOffset.FromUnixTimeMilliseconds(1000);

        Assert.Empty(machine.Update([Target(1, 0.1f, 0.1f, confirmed: false)], time));
        Assert.Equal(RadarPointerPhase.Hover, Assert.Single(machine.Update([Target(1, 0.1f, 0.1f)], time)).Phase);
    }

    private static RadarTarget Target(int id, float x, float y, bool confirmed = true)
    {
        return new RadarTarget(id, x, y, x, y, 1f, 3, confirmed);
    }
}
