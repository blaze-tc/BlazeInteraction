using Yuexin.Radar.Contracts;
using Yuexin.Radar.Protocol;

namespace Yuexin.Radar.Protocol.Tests;

public sealed class RadarScanFrameBuilderTests
{
    [Fact]
    public void AngleWrap_PublishesImmutablePreviousScan()
    {
        var builder = new RadarScanFrameBuilder(minimumValidPointCount: 3);
        builder.AddPoint(Point(5000));
        builder.AddPoint(Point(5200));
        builder.AddPoint(Point(5400));

        var frame = builder.AddPoint(Point(100));

        Assert.NotNull(frame);
        Assert.Equal(1, frame.Sequence);
        Assert.Equal([5000, 5200, 5400], frame.Points.Select(point => point.AngleRaw));
        Assert.IsAssignableFrom<IReadOnlyList<RadarPoint>>(frame.Points);
        Assert.Single(builder.CurrentPoints);
    }

    [Fact]
    public void ShortScan_IsNotPublished()
    {
        var builder = new RadarScanFrameBuilder(minimumValidPointCount: 3);
        builder.AddPoint(Point(5200));

        var frame = builder.AddPoint(Point(100));

        Assert.Null(frame);
        Assert.Single(builder.CurrentPoints);
    }

    [Fact]
    public void SmallBackwardAngle_IsTreatedAsNoiseInsteadOfWrap()
    {
        var builder = new RadarScanFrameBuilder(minimumValidPointCount: 2, minimumWrapDropRaw: 1000);
        builder.AddPoint(Point(2000));

        var frame = builder.AddPoint(Point(1990));

        Assert.Null(frame);
        Assert.Single(builder.CurrentPoints);
        Assert.Equal(1, builder.RejectedAngleCount);
    }

    [Fact]
    public void Reset_ClearsUnfinishedScanAndRestartsSequence()
    {
        var builder = new RadarScanFrameBuilder(minimumValidPointCount: 2);
        builder.AddPoint(Point(5000));
        builder.Reset(resetSequence: true);
        builder.AddPoint(Point(5000));
        builder.AddPoint(Point(5200));

        var frame = builder.AddPoint(Point(100));

        Assert.NotNull(frame);
        Assert.Equal(1, frame.Sequence);
        Assert.Equal(2, frame.Points.Count);
    }

    private static RadarPoint Point(int angleRaw)
    {
        var degrees = angleRaw / 16f;
        return new RadarPoint(100, angleRaw, degrees, 0f, 0f);
    }
}
