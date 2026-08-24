using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class HomographySurfaceMapperTests
{
    [Theory]
    [InlineData(0f, 0f, 0f, 0f)]
    [InlineData(100f, 0f, 1f, 0f)]
    [InlineData(100f, 100f, 1f, 1f)]
    [InlineData(0f, 100f, 0f, 1f)]
    public void TryMapTrackingPoint_MapsCalibrationCorners(
        float cameraX,
        float cameraY,
        float expectedX,
        float expectedY)
    {
        using var mapper = new HomographySurfaceMapper(SquareCalibration());

        var mapped = mapper.TryMapTrackingPoint(
            new Vector2Data(cameraX, cameraY),
            out var surfacePoint);

        Assert.True(mapped);
        AssertClose(expectedX, surfacePoint.X);
        AssertClose(expectedY, surfacePoint.Y);
    }

    [Fact]
    public void TryMap_MapsCenterToCenter()
    {
        using var mapper = new HomographySurfaceMapper(SquareCalibration());

        var mapped = mapper.TryMap(new Vector2Data(50f, 50f), out var surfacePoint);

        Assert.True(mapped);
        AssertClose(0.5f, surfacePoint.X);
        AssertClose(0.5f, surfacePoint.Y);
    }

    [Theory]
    [InlineData(-1f, 50f)]
    [InlineData(101f, 50f)]
    [InlineData(50f, -1f)]
    [InlineData(50f, 101f)]
    public void TryMapTrackingPoint_RejectsPointOutsideCalibrationPolygon(float x, float y)
    {
        using var mapper = new HomographySurfaceMapper(SquareCalibration());

        Assert.False(mapper.TryMapTrackingPoint(new Vector2Data(x, y), out _));
    }

    [Fact]
    public void MapLandmark_AllowsFiniteMappedPointOutsideUnitInterval()
    {
        using var mapper = new HomographySurfaceMapper(SquareCalibration());

        var mapped = mapper.MapLandmark(new Vector2Data(125f, -25f));

        AssertClose(1.25f, mapped.X);
        AssertClose(-0.25f, mapped.Y);
    }

    [Fact]
    public void Calibration_RejectsDuplicatePoints()
    {
        Assert.Throws<ArgumentException>(() => new CameraCalibration(
            new Vector2Data(0f, 0f),
            new Vector2Data(100f, 0f),
            new Vector2Data(100f, 0f),
            new Vector2Data(0f, 100f)));
    }

    [Fact]
    public void Calibration_RejectsCollinearPoints()
    {
        Assert.Throws<ArgumentException>(() => new CameraCalibration(
            new Vector2Data(0f, 0f),
            new Vector2Data(100f, 0f),
            new Vector2Data(200f, 0f),
            new Vector2Data(300f, 0f)));
    }

    private static CameraCalibration SquareCalibration() => new(
        new Vector2Data(0f, 0f),
        new Vector2Data(100f, 0f),
        new Vector2Data(100f, 100f),
        new Vector2Data(0f, 100f));

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.00001f, expected + 0.00001f);
}
