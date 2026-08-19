using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class PolygonRegionTests
{
    private static readonly Point2[] Square =
    [
        new(0f, 0f),
        new(2f, 0f),
        new(2f, 2f),
        new(0f, 2f)
    ];

    [Theory]
    [InlineData(1f, 1f, true)]
    [InlineData(0f, 1f, true)]
    [InlineData(2f, 2f, true)]
    [InlineData(3f, 1f, false)]
    public void Contains_TreatsBoundaryAsInside(float x, float y, bool expected)
    {
        Assert.Equal(expected, PolygonRegion.Contains(Square, new Point2(x, y)));
    }
}
