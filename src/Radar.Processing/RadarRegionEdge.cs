namespace Yuexin.Radar.Processing;

public enum RadarRegionEdgeSide
{
    Left,
    Right,
    Top,
    Bottom
}

public readonly record struct RadarRegionEdge(Point2 Start, Point2 End, RadarRegionEdgeSide Side)
{
    public float DistanceTo(Point2 point)
    {
        var deltaX = End.X - Start.X;
        var deltaY = End.Y - Start.Y;
        var lengthSquared = deltaX * deltaX + deltaY * deltaY;
        if (lengthSquared <= float.Epsilon)
        {
            return MathF.Sqrt((point.X - Start.X) * (point.X - Start.X) + (point.Y - Start.Y) * (point.Y - Start.Y));
        }

        var projection = ((point.X - Start.X) * deltaX + (point.Y - Start.Y) * deltaY) / lengthSquared;
        projection = Math.Clamp(projection, 0f, 1f);
        var closestX = Start.X + projection * deltaX;
        var closestY = Start.Y + projection * deltaY;
        var distanceX = point.X - closestX;
        var distanceY = point.Y - closestY;
        return MathF.Sqrt(distanceX * distanceX + distanceY * distanceY);
    }
}

/// <summary>Classifies each active-region segment by the direction outside the polygon.</summary>
public static class RadarRegionEdges
{
    public static IReadOnlyList<RadarRegionEdge> Classify(IReadOnlyList<Point2> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count < 3)
        {
            return [];
        }

        var signedDoubleArea = 0f;
        for (var index = 0; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var next = polygon[(index + 1) % polygon.Count];
            signedDoubleArea += current.X * next.Y - next.X * current.Y;
        }

        var counterClockwise = signedDoubleArea > 0f;
        var edges = new List<RadarRegionEdge>(polygon.Count);
        for (var index = 0; index < polygon.Count; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Count];
            var deltaX = end.X - start.X;
            var deltaY = end.Y - start.Y;
            var length = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (length <= float.Epsilon)
            {
                continue;
            }

            var outwardX = counterClockwise ? deltaY / length : -deltaY / length;
            var outwardY = counterClockwise ? -deltaX / length : deltaX / length;
            var side = MathF.Abs(outwardX) >= MathF.Abs(outwardY)
                ? outwardX < 0f ? RadarRegionEdgeSide.Left : RadarRegionEdgeSide.Right
                : outwardY < 0f ? RadarRegionEdgeSide.Bottom : RadarRegionEdgeSide.Top;
            edges.Add(new RadarRegionEdge(start, end, side));
        }

        return edges;
    }
}
