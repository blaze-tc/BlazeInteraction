using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Processing;

public static class RadarCoordinateConverter
{
    public static RadarPoint ApplyTransform(RadarPoint point, RadarTransformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var x = options.FlipX ? -point.X : point.X;
        var y = options.FlipY ? -point.Y : point.Y;
        var radians = options.RotationDegrees * MathF.PI / 180f;
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        var rotatedX = x * cosine - y * sine;
        var rotatedY = x * sine + y * cosine;

        return point with
        {
            X = rotatedX + options.OffsetXMeters,
            Y = rotatedY + options.OffsetYMeters
        };
    }
}
