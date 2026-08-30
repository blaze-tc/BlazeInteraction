namespace Yuexin.Radar.Processing;

public static class PolygonRegion
{
    private const float BoundaryEpsilon = 1e-5f;

    public static bool Contains(IReadOnlyList<Point2> polygon, Point2 point)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count < 3)
        {
            return false;
        }

        var inside = false;
        for (var current = 0; current < polygon.Count; current++)
        {
            var previous = current == 0 ? polygon.Count - 1 : current - 1;
            var a = polygon[previous];
            var b = polygon[current];
            if (IsOnSegment(a, b, point))
            {
                return true;
            }

            var crosses = (a.Y > point.Y) != (b.Y > point.Y);
            if (crosses)
            {
                var intersectionX = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
                if (point.X < intersectionX)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    private static bool IsOnSegment(Point2 a, Point2 b, Point2 point)
    {
        var cross = (point.Y - a.Y) * (b.X - a.X) - (point.X - a.X) * (b.Y - a.Y);
        if (MathF.Abs(cross) > BoundaryEpsilon)
        {
            return false;
        }

        return point.X >= MathF.Min(a.X, b.X) - BoundaryEpsilon &&
               point.X <= MathF.Max(a.X, b.X) + BoundaryEpsilon &&
               point.Y >= MathF.Min(a.Y, b.Y) - BoundaryEpsilon &&
               point.Y <= MathF.Max(a.Y, b.Y) + BoundaryEpsilon;
    }
}
