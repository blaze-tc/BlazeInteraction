using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Processing;

public sealed class SequentialPointClusterer
{
    private readonly RadarClusteringOptions _options;

    public SequentialPointClusterer(RadarClusteringOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.BaseGapMeters < 0f || _options.DistanceScale < 0f ||
            _options.MinimumClusterPointCount < 1 || _options.MaximumClusterWidthMeters <= 0f)
        {
            throw new ArgumentException("Clustering options are invalid.", nameof(options));
        }
    }

    public IReadOnlyList<RadarCluster> Cluster(IReadOnlyList<RadarPoint> orderedPoints)
    {
        ArgumentNullException.ThrowIfNull(orderedPoints);
        if (orderedPoints.Count == 0)
        {
            return [];
        }

        var candidates = new List<List<RadarPoint>> { new() { orderedPoints[0] } };
        for (var index = 1; index < orderedPoints.Count; index++)
        {
            var previous = orderedPoints[index - 1];
            var current = orderedPoints[index];
            var gap = Distance(previous.X, previous.Y, current.X, current.Y);
            var averageDistance = (previous.DistanceCentimeters + current.DistanceCentimeters) / 200f;
            var threshold = _options.BaseGapMeters + averageDistance * _options.DistanceScale;
            if (gap > threshold)
            {
                candidates.Add([]);
            }

            candidates[^1].Add(current);
        }

        var result = new List<RadarCluster>();
        foreach (var points in candidates)
        {
            if (points.Count < _options.MinimumClusterPointCount)
            {
                continue;
            }

            var width = MaximumPairDistance(points);
            if (width > _options.MaximumClusterWidthMeters)
            {
                continue;
            }

            var centerX = Median(points.Select(point => point.X));
            var centerY = Median(points.Select(point => point.Y));
            var estimatedDistance = Median(points.Select(point => point.DistanceCentimeters / 100f));
            result.Add(new RadarCluster(result.Count, points.ToArray(), centerX, centerY, width, estimatedDistance));
        }

        return result;
    }

    private static float MaximumPairDistance(IReadOnlyList<RadarPoint> points)
    {
        var maximum = 0f;
        for (var first = 0; first < points.Count; first++)
        {
            for (var second = first + 1; second < points.Count; second++)
            {
                maximum = MathF.Max(maximum, Distance(points[first].X, points[first].Y, points[second].X, points[second].Y));
            }
        }

        return maximum;
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        var x = x2 - x1;
        var y = y2 - y1;
        return MathF.Sqrt(x * x + y * y);
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2f
            : sorted[middle];
    }
}
