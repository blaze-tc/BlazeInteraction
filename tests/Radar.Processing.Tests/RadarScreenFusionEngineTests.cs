using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class RadarScreenFusionEngineTests
{
    [Fact]
    public void Tick_MergesOnlyCrossSensorDetectionsWithinThreshold()
    {
        var engine = CreateEngine(fusionDistancePixels: 80f, confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 1000, 500, 1f)]));
        engine.Publish(new SensorDetectionFrame("f2", now, [new(1, 1040, 520, 0.8f), new(2, 1080, 520, 0.7f)]));

        var result = engine.Tick(now);

        Assert.Equal(2, result.Targets.Count);
        Assert.Contains(result.Targets, target => target.SourceSensorCount == 2 && target.PixelX == 1020f);
    }

    [Fact]
    public void Tick_PreservesPointerAcrossSensorHandoffAndEmitsOneUpAfterLoss()
    {
        var engine = CreateEngine(fusionDistancePixels: 80f, confirmFrames: 1, lostFrames: 2);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 900, 500, 1f)]));
        var down = Assert.Single(engine.Tick(now).Pointers);
        engine.Publish(new SensorDetectionFrame("f2", now.AddMilliseconds(33), [new(1, 940, 500, 1f)]));
        var move = Assert.Single(engine.Tick(now.AddMilliseconds(33)).Pointers);
        var empty = engine.Tick(now.AddMilliseconds(200));
        var up = Assert.Single(engine.Tick(now.AddMilliseconds(233)).Pointers);

        Assert.Equal(down.PointerId, move.PointerId);
        Assert.Empty(empty.Pointers);
        Assert.Equal(RadarPointerPhase.Up, up.Phase);
        Assert.Empty(engine.Tick(now.AddMilliseconds(266)).Pointers);
    }

    [Fact]
    public void Tick_MergesThreeSensorsButKeepsCloseSameSensorDetectionsSeparate()
    {
        var engine = CreateEngine(fusionDistancePixels: 30f, confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("a", now, [new(1, 100, 100, 1f), new(2, 110, 100, 1f)]));
        engine.Publish(new SensorDetectionFrame("b", now, [new(1, 105, 100, 1f)]));
        engine.Publish(new SensorDetectionFrame("c", now, [new(1, 106, 100, 1f)]));

        var targets = engine.Tick(now).Targets;

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, target => target.SourceSensorCount == 3);
        Assert.Contains(targets, target => target.SourceSensorCount == 1);
    }

    [Fact]
    public void Tick_DiscardsStaleSensorFrames()
    {
        var engine = CreateEngine(sensorDataMaxAgeMilliseconds: 100, confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));

        Assert.Single(engine.Tick(now).Targets);
        Assert.Empty(engine.Tick(now.AddMilliseconds(101)).Targets);
    }

    [Fact]
    public void Tick_UsesStableOrderingWhenCandidateDistancesTie()
    {
        var engine = CreateEngine(fusionDistancePixels: 20f, confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("a", now, [new(1, 0, 0, 1f), new(2, 20, 0, 1f)]));
        engine.Publish(new SensorDetectionFrame("b", now, [new(1, 10, 0, 1f)]));

        var result = engine.Tick(now);

        Assert.Equal([5f, 20f], result.Targets.Select(target => target.PixelX));
    }

    [Fact]
    public void Tick_SmoothsMatchedTrackCoordinates()
    {
        var engine = CreateEngine(confirmFrames: 1, smoothingAlpha: 0.5f, maximumAssociationDistancePixels: 100f);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));
        engine.Tick(now);
        engine.Publish(new SensorDetectionFrame("f1", now.AddMilliseconds(16), [new(1, 140, 100, 0.5f)]));

        var target = Assert.Single(engine.Tick(now.AddMilliseconds(16)).Targets);

        Assert.Equal(120f, target.PixelX);
        Assert.Equal(0.75f, target.Confidence);
    }

    [Theory]
    [InlineData(RadarInteractionMode.Dwell, RadarPointerPhase.Hover)]
    [InlineData(RadarInteractionMode.HoverOnly, RadarPointerPhase.Hover)]
    public void Tick_UsesConfiguredNonTouchInteractionMode(RadarInteractionMode mode, RadarPointerPhase expectedPhase)
    {
        var engine = CreateEngine(confirmFrames: 1, interactionMode: mode);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));

        Assert.Equal(expectedPhase, Assert.Single(engine.Tick(now).Pointers).Phase);
    }

    [Fact]
    public void Tick_EnterTriggerEmitsClickPairOnlyOncePerTrack()
    {
        var engine = CreateEngine(confirmFrames: 1, interactionMode: RadarInteractionMode.EnterTrigger);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));

        Assert.Equal([RadarPointerPhase.Down, RadarPointerPhase.Up], engine.Tick(now).Pointers.Select(pointer => pointer.Phase));
        Assert.Empty(engine.Tick(now.AddMilliseconds(16)).Pointers);
    }

    [Fact]
    public void Reset_EmitsUpForPressedTouchPointersAndIsIdempotent()
    {
        var engine = CreateEngine(confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));
        Assert.Equal(RadarPointerPhase.Down, Assert.Single(engine.Tick(now).Pointers).Phase);

        var reset = Assert.Single(engine.Reset(now.AddMilliseconds(1)));

        Assert.Equal(RadarPointerPhase.Up, reset.Phase);
        Assert.Empty(engine.Reset(now.AddMilliseconds(2)));
        Assert.Empty(engine.Tick(now.AddMilliseconds(3)).Targets);
    }

    [Fact]
    public void PublishAndResults_DoNotExposeMutableCollectionAliases()
    {
        var detections = new[] { new SensorDetection(1, 100, 100, 1f) };
        var frame = new SensorDetectionFrame("f1", DateTimeOffset.UnixEpoch, detections);
        detections[0] = new SensorDetection(1, 900, 900, 1f);
        var engine = CreateEngine(confirmFrames: 1);

        engine.Publish(frame);
        var result = engine.Tick(DateTimeOffset.UnixEpoch);

        Assert.Equal(100f, Assert.Single(result.Targets).PixelX);
        Assert.False(result.Targets is FusedScreenTarget[]);
        Assert.False(result.Pointers is RadarScreenPointer[]);
    }

    private static RadarScreenFusionEngine CreateEngine(
        int sensorDataMaxAgeMilliseconds = 150,
        float fusionDistancePixels = 80f,
        float maximumAssociationDistancePixels = 160f,
        int confirmFrames = 2,
        int lostFrames = 3,
        float smoothingAlpha = 0.5f,
        RadarInteractionMode interactionMode = RadarInteractionMode.Touch)
    {
        return new RadarScreenFusionEngine(new RadarScreenFusionOptions
        {
            Screen = new RadarScreenInfo("front", "Front", 1920, 1080, true, 0),
            SensorDataMaxAgeMilliseconds = sensorDataMaxAgeMilliseconds,
            FusionDistancePixels = fusionDistancePixels,
            MaximumAssociationDistancePixels = maximumAssociationDistancePixels,
            ConfirmFrames = confirmFrames,
            LostFrames = lostFrames,
            SmoothingAlpha = smoothingAlpha,
            InteractionMode = interactionMode,
            MinimumPressMilliseconds = 0
        });
    }
}
