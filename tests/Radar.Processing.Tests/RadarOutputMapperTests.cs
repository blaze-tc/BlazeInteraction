using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class RadarOutputMapperTests
{
    [Fact]
    public void Map_UsesBottomLeftPixelRectangle()
    {
        var mapper = new RadarOutputMapper(new RadarPixelRect(2048, 0, 2048, 1536), 4096, 1536);

        var mapped = mapper.Map(0.5f, 0.25f);

        Assert.Equal(3072f, mapped.PixelX);
        Assert.Equal(384f, mapped.PixelY);
        Assert.Equal(0.75f, mapped.NormalizedX);
        Assert.Equal(0.25f, mapped.NormalizedY);
    }

    [Fact]
    public void Map_ClampsLocalCoordinatesAtBothEdges()
    {
        var mapper = new RadarOutputMapper(new RadarPixelRect(100, 50, 300, 200), 800, 600);

        var mapped = mapper.Map(-2f, 3f);

        Assert.Equal(100f, mapped.PixelX);
        Assert.Equal(250f, mapped.PixelY);
        Assert.Equal(0.125f, mapped.NormalizedX);
        Assert.Equal(250f / 600f, mapped.NormalizedY);
    }

    [Fact]
    public void Map_MapsExactLocalCornersToRectangleCorners()
    {
        var mapper = new RadarOutputMapper(new RadarPixelRect(100, 50, 300, 200), 800, 600);

        var bottomLeft = mapper.Map(0f, 0f);
        var topRight = mapper.Map(1f, 1f);

        Assert.Equal(new MappedScreenPoint(100f, 50f, 0.125f, 50f / 600f), bottomLeft);
        Assert.Equal(new MappedScreenPoint(400f, 250f, 0.5f, 250f / 600f), topRight);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    public void Constructor_RejectsInvalidScreenSize(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarOutputMapper(new RadarPixelRect(0, 0, 1, 1), width, height));
    }

    [Fact]
    public void Constructor_RejectsRectangleOutsideScreen()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadarOutputMapper(new RadarPixelRect(700, 0, 101, 600), 800, 600));
    }

    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(0f, float.PositiveInfinity)]
    public void Map_RejectsNonFiniteCoordinates(float x, float y)
    {
        var mapper = new RadarOutputMapper(new RadarPixelRect(0, 0, 800, 600), 800, 600);

        Assert.Throws<ArgumentOutOfRangeException>(() => mapper.Map(x, y));
    }
}
