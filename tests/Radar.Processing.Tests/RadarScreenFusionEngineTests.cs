using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class RadarScreenFusionEngineTests
{
    [Fact]
    public void Tick_MergesAllCurrentFootprintsInSensorThenAcquisitionOrder()
    {
        var engine = CreateEngine(confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("sensor-b", now,
        [
            Detection(2, 100, 100, [new(12, 12), new(13, 13)])
        ]));
        engine.Publish(new SensorDetectionFrame("sensor-a", now,
        [
            Detection(1, 102, 100, [new(1, 1), new(2, 2)])
        ]));

        var pointer = Assert.Single(engine.Tick(now).Pointers);

        Assert.Equal(
            [new RadarScreenPoint(1, 1), new(2, 2), new(12, 12), new(13, 13)],
            pointer.Footprint);
    }

    [Fact]
    public void Tick_MissingTrackDoesNotRepeatPreviousFootprint()
    {
        var engine = CreateEngine(confirmFrames: 1, lostFrames: 3);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("sensor", now,
        [
            Detection(1, 100, 100, [new(90, 90)])
        ]));
        Assert.NotEmpty(Assert.Single(engine.Tick(now).Pointers).Footprint);

        var held = Assert.Single(engine.Tick(now.AddMilliseconds(1)).Pointers);

        Assert.Empty(held.Footprint);
    }

    [Fact]
    public void Tick_UsesFrozenFootprintSnapshots()
    {
        var source = new List<RadarScreenPoint> { new(90, 90) };
        var detection = Detection(1, 100, 100, source);
        source.Clear();
        var engine = CreateEngine(confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;

        engine.Publish(new SensorDetectionFrame("sensor", now, [detection]));

        var result = engine.Tick(now);
        var target = Assert.Single(result.Targets);
        var pointer = Assert.Single(result.Pointers);

        Assert.Equal([new RadarScreenPoint(90, 90)], target.Footprint);
        Assert.Equal([new RadarScreenPoint(90, 90)], pointer.Footprint);
        Assert.False(target.Footprint is RadarScreenPoint[]);
        Assert.False(pointer.Footprint is RadarScreenPoint[]);
    }

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
        var held = Assert.Single(empty.Pointers);
        Assert.Equal(down.PointerId, held.PointerId);
        Assert.Equal(RadarPointerPhase.Move, held.Phase);
        Assert.Equal(RadarPointerPhase.Up, up.Phase);
        Assert.Empty(engine.Tick(now.AddMilliseconds(266)).Pointers);
    }

    [Fact]
    public void Tick_HoldsLastKnownTargetAndTouchPointerAcrossAToleratedDetectionGap()
    {
        var engine = CreateEngine(confirmFrames: 1, lostFrames: 3, smoothingAlpha: 1f);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 320, 180, 0.9f)]));
        var down = Assert.Single(engine.Tick(now).Pointers);
        engine.Publish(new SensorDetectionFrame("f1", now.AddMilliseconds(16), []));

        var gap = engine.Tick(now.AddMilliseconds(16));

        var target = Assert.Single(gap.Targets);
        var pointer = Assert.Single(gap.Pointers);
        Assert.Equal(down.PointerId, target.TrackId);
        Assert.Equal(down.PointerId, pointer.PointerId);
        Assert.Equal(320f, pointer.PixelX);
        Assert.Equal(180f, pointer.PixelY);
        Assert.Equal(RadarPointerPhase.Move, pointer.Phase);
    }

    [Theory]
    [InlineData(RadarInteractionMode.Dwell)]
    [InlineData(RadarInteractionMode.HoverOnly)]
    public void Tick_HoldsLastKnownHoverPointerAcrossAToleratedDetectionGap(RadarInteractionMode mode)
    {
        var engine = CreateEngine(confirmFrames: 1, lostFrames: 3, interactionMode: mode);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 320, 180, 0.9f)]));
        var initial = Assert.Single(engine.Tick(now).Pointers);
        engine.Publish(new SensorDetectionFrame("f1", now.AddMilliseconds(16), []));

        var held = Assert.Single(engine.Tick(now.AddMilliseconds(16)).Pointers);

        Assert.Equal(initial.PointerId, held.PointerId);
        Assert.Equal(RadarPointerPhase.Hover, held.Phase);
        Assert.Equal(320f, held.PixelX);
        Assert.Equal(180f, held.PixelY);
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
        var engine = CreateEngine(sensorDataMaxAgeMilliseconds: 100, confirmFrames: 1, lostFrames: 1);
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

    [Fact]
    public void Tick_LosesTrackThatCompetesForAnAlreadyMatchedObservation()
    {
        var engine = CreateEngine(confirmFrames: 1, lostFrames: 1, smoothingAlpha: 1f, maximumAssociationDistancePixels: 20f);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 0, 100, 1f), new(2, 10, 100, 1f)]));
        var initial = engine.Tick(now).Pointers;
        engine.Publish(new SensorDetectionFrame("f1", now.AddMilliseconds(16), [new(1, 1, 100, 1f)]));

        var result = engine.Tick(now.AddMilliseconds(16));

        var winner = initial.Single(pointer => pointer.PixelX == 0f);
        var loser = initial.Single(pointer => pointer.PixelX == 10f);
        Assert.Equal(winner.PointerId, Assert.Single(result.Targets).TrackId);
        Assert.Equal(RadarPointerPhase.Move, Assert.Single(result.Pointers.Where(pointer => pointer.PointerId == winner.PointerId)).Phase);
        Assert.Equal(RadarPointerPhase.Up, Assert.Single(result.Pointers.Where(pointer => pointer.PointerId == loser.PointerId)).Phase);
    }

    [Fact]
    public void Publish_DoesNotReplaceSnapshotWithEqualTimestamp()
    {
        var engine = CreateEngine(confirmFrames: 1, smoothingAlpha: 1f);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 900, 100, 1f)]));

        var target = Assert.Single(engine.Tick(now).Targets);

        Assert.Equal(100f, target.PixelX);
    }

    [Fact]
    public void Publish_IgnoresOlderSnapshot()
    {
        var engine = CreateEngine(confirmFrames: 1, smoothingAlpha: 1f);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now.AddMilliseconds(16), [new(1, 200, 100, 1f)]));
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));

        var target = Assert.Single(engine.Tick(now.AddMilliseconds(16)).Targets);

        Assert.Equal(200f, target.PixelX);
    }

    [Fact]
    public void Tick_IgnoresRollbackWithoutChangingPointerLifecycle()
    {
        var engine = CreateEngine(confirmFrames: 1, lostFrames: 1, smoothingAlpha: 1f);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));
        var down = Assert.Single(engine.Tick(now).Pointers);

        Assert.Empty(engine.Tick(now.AddMilliseconds(-1)).Targets);
        Assert.Empty(engine.Tick(now.AddMilliseconds(-1)).Pointers);

        engine.Publish(new SensorDetectionFrame("f1", now.AddMilliseconds(16), [new(1, 110, 100, 1f)]));
        var move = Assert.Single(engine.Tick(now.AddMilliseconds(16)).Pointers);
        Assert.Equal(down.PointerId, move.PointerId);
        Assert.Equal(RadarPointerPhase.Move, move.Phase);
    }

    [Fact]
    public void Publish_TreatsCaseVariantsAsOneSensorSnapshot()
    {
        var engine = CreateEngine(confirmFrames: 1, smoothingAlpha: 1f);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));
        engine.Publish(new SensorDetectionFrame("F1", now.AddMilliseconds(16), [new(1, 120, 100, 1f)]));

        var target = Assert.Single(engine.Tick(now.AddMilliseconds(16)).Targets);

        Assert.Equal(120f, target.PixelX);
        Assert.Equal(1, target.SourceSensorCount);
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("sensor!")]
    [InlineData("")]
    public void Publish_RejectsSensorIdsOutsideConfigurationSyntax(string sensorId)
    {
        var engine = CreateEngine();

        Assert.Throws<ArgumentException>(() => engine.Publish(new SensorDetectionFrame(sensorId, DateTimeOffset.UnixEpoch, [])));
    }

    [Fact]
    public void Constructor_RejectsOptionsOutsideConfigurationRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(screen: new RadarScreenInfo("front", "Front", 32769, 1080, true, 0))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(sensorDataMaxAgeMilliseconds: 9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(sensorDataMaxAgeMilliseconds: 5001)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(confirmFrames: 121)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(lostFrames: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(fusionDistancePixels: float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(maximumAssociationDistancePixels: float.PositiveInfinity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(smoothingAlpha: 0f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(dwellRadiusNormalized: -0.01f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(dragThresholdNormalized: float.PositiveInfinity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarScreenFusionEngine(Options(maximumClickMovementNormalized: float.NaN)));
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

    [Theory]
    [InlineData(RadarInteractionMode.Touch)]
    [InlineData(RadarInteractionMode.HoverOnly)]
    [InlineData(RadarInteractionMode.Dwell)]
    [InlineData(RadarInteractionMode.EnterTrigger)]
    public void RetiredTrackChurn_ReleasesAllPointerPositionState(RadarInteractionMode mode)
    {
        var engine = CreateEngine(confirmFrames: 1, lostFrames: 1, interactionMode: mode);
        var timestamp = DateTimeOffset.UnixEpoch;

        for (var index = 0; index < 500; index++)
        {
            timestamp = timestamp.AddMilliseconds(1);
            engine.Publish(new SensorDetectionFrame(
                "f1",
                timestamp,
                [new SensorDetection(index, 100 + index % 100, 100, 1f)]));
            engine.Tick(timestamp);
            Assert.InRange(PointerPositionCount(engine), 0, 1);

            timestamp = timestamp.AddMilliseconds(1);
            engine.Publish(new SensorDetectionFrame("f1", timestamp, []));
            engine.Tick(timestamp);
            Assert.Equal(0, PointerPositionCount(engine));
        }
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
    public void Reset_EmitsUpForEveryPressedTouchPointerInStableIdOrder()
    {
        var engine = CreateEngine(confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f), new(2, 500, 100, 1f)]));
        var down = engine.Tick(now).Pointers;

        var reset = engine.Reset(now.AddMilliseconds(1));

        Assert.Equal(down.Select(pointer => pointer.PointerId).Order(), reset.Select(pointer => pointer.PointerId));
        Assert.All(reset, pointer => Assert.Equal(RadarPointerPhase.Up, pointer.Phase));
    }

    [Fact]
    public void SnapshotPressedPointers_LeavesFusionStateIntactUntilReset()
    {
        var engine = CreateEngine(confirmFrames: 1);
        var now = DateTimeOffset.UnixEpoch;
        engine.Publish(new SensorDetectionFrame("f1", now, [new(1, 100, 100, 1f)]));
        Assert.Equal(RadarPointerPhase.Down, Assert.Single(engine.Tick(now).Pointers).Phase);

        var snapshot = Assert.Single(engine.SnapshotPressedPointers(now.AddMilliseconds(1)));

        Assert.Equal(RadarPointerPhase.Up, snapshot.Phase);
        var reset = Assert.Single(engine.Reset(now.AddMilliseconds(2)));
        Assert.Equal(snapshot.PointerId, reset.PointerId);
        Assert.Equal(snapshot.Phase, reset.Phase);
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
        return new RadarScreenFusionEngine(Options(
            sensorDataMaxAgeMilliseconds,
            fusionDistancePixels,
            maximumAssociationDistancePixels,
            confirmFrames,
            lostFrames,
            smoothingAlpha,
            interactionMode));
    }

    private static RadarScreenFusionOptions Options(
        int sensorDataMaxAgeMilliseconds = 150,
        float fusionDistancePixels = 80f,
        float maximumAssociationDistancePixels = 160f,
        int confirmFrames = 2,
        int lostFrames = 3,
        float smoothingAlpha = 0.5f,
        RadarInteractionMode interactionMode = RadarInteractionMode.Touch,
        RadarScreenInfo? screen = null,
        float dwellRadiusNormalized = 0.03f,
        float dragThresholdNormalized = 0.015f,
        float maximumClickMovementNormalized = 0.03f)
    {
        return new RadarScreenFusionOptions
        {
            Screen = screen ?? new RadarScreenInfo("front", "Front", 1920, 1080, true, 0),
            SensorDataMaxAgeMilliseconds = sensorDataMaxAgeMilliseconds,
            FusionDistancePixels = fusionDistancePixels,
            MaximumAssociationDistancePixels = maximumAssociationDistancePixels,
            ConfirmFrames = confirmFrames,
            LostFrames = lostFrames,
            SmoothingAlpha = smoothingAlpha,
            InteractionMode = interactionMode,
            DwellRadiusNormalized = dwellRadiusNormalized,
            DragThresholdNormalized = dragThresholdNormalized,
            MaximumClickMovementNormalized = maximumClickMovementNormalized,
            MinimumPressMilliseconds = 0
        };
    }

    private static SensorDetection Detection(
        int id,
        float pixelX,
        float pixelY,
        IReadOnlyList<RadarScreenPoint> footprint) =>
        new(id, pixelX, pixelY, 1f, footprint);

    private static int PointerPositionCount(RadarScreenFusionEngine engine)
    {
        var field = typeof(RadarScreenFusionEngine).GetField(
            "_pointerPositions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<System.Collections.IDictionary>(field.GetValue(engine)).Count;
    }
}
