using Blaze.Interaction.Contracts;
using Yuexin.Radar.Contracts;

namespace Blaze.Provider.Radar.Tests;

public sealed class RadarFrameAdapterTests
{
    [Fact]
    public void Adapt_MapsEachScreenFrameToAnIdentityPreservingInteractionFrame()
    {
        var adapter = new RadarFrameAdapter("radar-instance-a");
        var batch = new PointerBatchPayload(
        [
            new RadarScreenPointerFrame(
                new RadarScreenInfo("left", "Left", 1920, 1080, false, 0),
                41,
                1_700_000_000_041,
                [new RadarScreenPointer(7, RadarPointerPhase.Move, .25f, .75f, 480f, 810f, .8f, 1_700_000_000_040)]),
            new RadarScreenPointerFrame(
                new RadarScreenInfo("front", "Front", 4096, 1536, true, 1),
                42,
                1_700_000_000_042,
                [])
        ]);

        var frames = adapter.Adapt(batch);

        Assert.Equal(2, frames.Count);
        var left = frames[0];
        Assert.Equal("blaze.radar.f10f20", left.ProviderId);
        Assert.Equal("radar-instance-a", left.ProviderInstanceId);
        Assert.Equal("left", left.SurfaceId);
        Assert.Equal(41, left.Sequence);
        Assert.Equal(1_700_000_000_041, left.TimestampUnixMs);
        var point = Assert.Single(left.Points);
        Assert.Equal(7, point.Id);
        Assert.Equal("left", point.SurfaceId);
        Assert.Equal("blaze.radar.f10f20", point.ProviderId);
        Assert.Equal("radar-instance-a", point.ProviderInstanceId);
        Assert.Equal("radar-fused-output", point.SourceId);
        Assert.Equal(InteractionPhase.Move, point.Phase);
        Assert.Equal(.25f, point.NormalizedPosition.X);
        Assert.Equal(.75f, point.NormalizedPosition.Y);
        Assert.Equal(480f, point.PixelPosition.X);
        Assert.Equal(810f, point.PixelPosition.Y);
        Assert.Equal(.8f, point.Confidence);
        Assert.Equal(1_700_000_000_040, point.TimestampUnixMs);
        Assert.True(point.TryGetRadarExtension(out var radar));
        Assert.Equal("fused-output", radar!.SensorId);
        Assert.Equal("front", frames[1].SurfaceId);
        Assert.Empty(frames[1].Points);
    }

    [Theory]
    [InlineData(RadarPointerPhase.Hover, InteractionPhase.Hover)]
    [InlineData(RadarPointerPhase.Down, InteractionPhase.Down)]
    [InlineData(RadarPointerPhase.Move, InteractionPhase.Move)]
    [InlineData(RadarPointerPhase.Up, InteractionPhase.Up)]
    public void Adapt_MapsEveryExistingRadarPointerPhaseExactly(
        RadarPointerPhase radarPhase,
        InteractionPhase expectedPhase)
    {
        var pointer = new RadarScreenPointer(1, radarPhase, .5f, .5f, 50f, 50f, 1f, 123);

        var frame = Assert.Single(new RadarFrameAdapter("radar-instance-a").Adapt(Batch(pointer)));

        Assert.Equal(expectedPhase, Assert.Single(frame.Points).Phase);
    }

    [Fact]
    public void Adapt_UnknownRadarPhaseFailsInsteadOfInventingAnInteractionMeaning()
    {
        var pointer = new RadarScreenPointer(1, (RadarPointerPhase)99, .5f, .5f, 50f, 50f, 1f, 123);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RadarFrameAdapter("radar-instance-a").Adapt(Batch(pointer)));
    }

    [Fact]
    public void Adapt_InvalidRadarCoordinatesFailTheInteractionContractBoundary()
    {
        var pointer = new RadarScreenPointer(1, RadarPointerPhase.Move, 1.1f, .5f, 50f, 50f, 1f, 123);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RadarFrameAdapter("radar-instance-a").Adapt(Batch(pointer)));
    }

    [Fact]
    public void Constructor_BlankProviderInstanceIdIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new RadarFrameAdapter(" "));
    }

    private static PointerBatchPayload Batch(RadarScreenPointer pointer) => new(
    [
        new RadarScreenPointerFrame(
            new RadarScreenInfo("front", "Front", 100, 100, true, 0),
            9,
            124,
            [pointer])
    ]);
}
