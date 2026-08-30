using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class RadarCoordinateConverterTests
{
    [Fact]
    public void Transform_AppliesFlipThenRotationThenOffset()
    {
        var source = new RadarPoint(100, 0, 0f, 1f, 0f);
        var options = new RadarTransformOptions
        {
            FlipX = true,
            RotationDegrees = 90f,
            OffsetXMeters = 2f,
            OffsetYMeters = 3f
        };

        var transformed = RadarCoordinateConverter.ApplyTransform(source, options);

        Assert.Equal(2f, transformed.X, 5);
        Assert.Equal(2f, transformed.Y, 5);
        Assert.Equal(source.DistanceCentimeters, transformed.DistanceCentimeters);
        Assert.Equal(source.AngleRaw, transformed.AngleRaw);
    }
}
