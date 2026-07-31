using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class RadarPointFilterTests
{
    [Fact]
    public void Filter_AppliesRangeBlindZoneActiveAndMaskedPolygons()
    {
        var points = new[]
        {
            Point(0.05f, 10f),
            Point(1f, 250f),
            Point(1f, 10f, 0.5f, 0.5f),
            Point(1f, 20f, 1.5f, 1.5f),
            Point(6f, 30f)
        };
        var options = new RadarFilterOptions
        {
            MinimumDistanceMeters = 0.1f,
            MaximumDistanceMeters = 5f,
            BlindZoneStartDegrees = 230f,
            BlindZoneEndDegrees = 310f,
            MinimumAngleDegrees = 0f,
            MaximumAngleDegrees = 360f,
            ActivePolygon = [new(0f, 0f), new(2f, 0f), new(2f, 2f), new(0f, 2f)],
            MaskedPolygons = [[new(0.4f, 0.4f), new(0.6f, 0.4f), new(0.6f, 0.6f), new(0.4f, 0.6f)]]
        };

        var filtered = RadarPointFilter.Apply(points, options);

        var remaining = Assert.Single(filtered);
        Assert.Equal(1.5f, remaining.X);
        Assert.Equal(1.5f, remaining.Y);
    }

    [Fact]
    public void Filter_AppliesFourEdgeDeadZonesInsideActivePolygon()
    {
        var points = new[]
        {
            Point(1f, 10f, 0.05f, 1f),
            Point(1f, 10f, 1.95f, 1f),
            Point(1f, 10f, 1f, 0.05f),
            Point(1f, 10f, 1f, 1.95f),
            Point(1f, 10f, 1f, 1f)
        };
        var options = new RadarFilterOptions
        {
            MinimumDistanceMeters = 0f,
            MaximumDistanceMeters = 5f,
            BlindZoneStartDegrees = 230f,
            BlindZoneEndDegrees = 310f,
            ActivePolygon = [new(0f, 0f), new(2f, 0f), new(2f, 2f), new(0f, 2f)],
            LeftEdgeDeadZoneMeters = 0.1f,
            RightEdgeDeadZoneMeters = 0.1f,
            BottomEdgeDeadZoneMeters = 0.1f,
            TopEdgeDeadZoneMeters = 0.1f
        };

        var filtered = RadarPointFilter.Apply(points, options);

        var remaining = Assert.Single(filtered);
        Assert.Equal(1f, remaining.X);
        Assert.Equal(1f, remaining.Y);
    }

    [Fact]
    public void Filter_EdgeDeadZonesFollowRotatedPolygonEdgesInsteadOfItsBoundingBox()
    {
        var points = new[]
        {
            Point(1f, 10f, 0.9f, 0.9f),
            Point(1f, 10f, 0f, 0f)
        };
        var options = new RadarFilterOptions
        {
            MinimumDistanceMeters = 0f,
            MaximumDistanceMeters = 5f,
            BlindZoneStartDegrees = 230f,
            BlindZoneEndDegrees = 310f,
            ActivePolygon = [new(0f, 2f), new(2f, 0f), new(0f, -2f), new(-2f, 0f)],
            LeftEdgeDeadZoneMeters = 0.2f,
            RightEdgeDeadZoneMeters = 0.2f,
            BottomEdgeDeadZoneMeters = 0.2f,
            TopEdgeDeadZoneMeters = 0.2f
        };

        var filtered = RadarPointFilter.Apply(points, options);

        var remaining = Assert.Single(filtered);
        Assert.Equal(0f, remaining.X);
        Assert.Equal(0f, remaining.Y);
    }

    private static RadarPoint Point(float distanceMeters, float angle, float? x = null, float? y = null)
    {
        return new RadarPoint(
            (int)(distanceMeters * 100),
            (int)(angle * 16),
            angle,
            x ?? distanceMeters,
            y ?? 0f);
    }
}
