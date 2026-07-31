using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Processing;

public static class RadarPointFilter
{
    public static IReadOnlyList<RadarPoint> Apply(
        IEnumerable<RadarPoint> points,
        RadarFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(options);

        var filtered = new List<RadarPoint>();
        foreach (var point in points)
        {
            var distanceMeters = point.DistanceCentimeters / 100f;
            if (distanceMeters < options.MinimumDistanceMeters || distanceMeters > options.MaximumDistanceMeters)
            {
                continue;
            }

            if (IsAngleInRange(point.AngleDegrees, options.BlindZoneStartDegrees, options.BlindZoneEndDegrees) ||
                !IsAngleInRange(point.AngleDegrees, options.MinimumAngleDegrees, options.MaximumAngleDegrees))
            {
                continue;
            }

            var position = new Point2(point.X, point.Y);
            if (options.ActivePolygon.Count >= 3 && !PolygonRegion.Contains(options.ActivePolygon, position))
            {
                continue;
            }

            if (options.ActivePolygon.Count >= 3 && IsInsideEdgeDeadZone(position, options))
            {
                continue;
            }

            if (options.MaskedPolygons.Any(mask => mask.Count >= 3 && PolygonRegion.Contains(mask, position)))
            {
                continue;
            }

            filtered.Add(point);
        }

        return filtered;
    }

    private static bool IsAngleInRange(float angle, float start, float end)
    {
        angle = NormalizeAngle(angle);
        start = NormalizeAngle(start);
        if (MathF.Abs(end - 360f) < 1e-5f && MathF.Abs(start) < 1e-5f)
        {
            return true;
        }

        end = NormalizeAngle(end);
        return start <= end
            ? angle >= start && angle <= end
            : angle >= start || angle <= end;
    }

    private static float NormalizeAngle(float angle)
    {
        var normalized = angle % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }

    private static bool IsInsideEdgeDeadZone(Point2 point, RadarFilterOptions options)
    {
        foreach (var edge in RadarRegionEdges.Classify(options.ActivePolygon))
        {
            var deadZoneMeters = edge.Side switch
            {
                RadarRegionEdgeSide.Left => options.LeftEdgeDeadZoneMeters,
                RadarRegionEdgeSide.Right => options.RightEdgeDeadZoneMeters,
                RadarRegionEdgeSide.Top => options.TopEdgeDeadZoneMeters,
                RadarRegionEdgeSide.Bottom => options.BottomEdgeDeadZoneMeters,
                _ => 0f
            };
            if (deadZoneMeters > 0f && edge.DistanceTo(point) < deadZoneMeters)
            {
                return true;
            }
        }

        return false;
    }
}
