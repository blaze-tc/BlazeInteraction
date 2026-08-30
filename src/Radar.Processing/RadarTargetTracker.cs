using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Processing;

public sealed record RadarTrackingUpdate(
    IReadOnlyList<RadarTarget> ObservedTargets,
    IReadOnlyList<int> LostTrackIds);

public sealed class RadarTargetTracker
{
    private readonly RadarTrackingOptions _options;
    private readonly Dictionary<int, TrackState> _tracks = [];
    private int _nextTrackId = 1;

    public RadarTargetTracker(RadarTrackingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (_options.ConfirmFrames < 1 || _options.LostFrames < 1 ||
            _options.MaximumAssociationDistanceMeters <= 0f ||
            _options.SmoothingAlpha is <= 0f or > 1f)
        {
            throw new ArgumentException("Tracking options are invalid.", nameof(options));
        }
    }

    public RadarTrackingUpdate Update(IReadOnlyList<RadarCluster> clusters)
    {
        ArgumentNullException.ThrowIfNull(clusters);

        var matchedTracks = new HashSet<int>();
        var matchedClusters = new HashSet<int>();
        var candidates = new List<Association>();

        foreach (var track in _tracks.Values)
        {
            for (var clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
            {
                var cluster = clusters[clusterIndex];
                var distance = Distance(track.X, track.Y, cluster.CenterX, cluster.CenterY);
                if (distance <= _options.MaximumAssociationDistanceMeters)
                {
                    candidates.Add(new Association(track.TrackId, clusterIndex, distance));
                }
            }
        }

        foreach (var candidate in candidates.OrderBy(candidate => candidate.Distance))
        {
            if (!matchedTracks.Add(candidate.TrackId) || !matchedClusters.Add(candidate.ClusterIndex))
            {
                continue;
            }

            var track = _tracks[candidate.TrackId];
            var cluster = clusters[candidate.ClusterIndex];
            track.X = Smooth(track.X, cluster.CenterX);
            track.Y = Smooth(track.Y, cluster.CenterY);
            track.PointCount = cluster.Points.Count;
            track.ObservedFrames++;
            track.MissingFrames = 0;
        }

        for (var clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
        {
            if (matchedClusters.Contains(clusterIndex))
            {
                continue;
            }

            var cluster = clusters[clusterIndex];
            var track = new TrackState
            {
                TrackId = _nextTrackId++,
                X = cluster.CenterX,
                Y = cluster.CenterY,
                PointCount = cluster.Points.Count,
                ObservedFrames = 1
            };
            _tracks.Add(track.TrackId, track);
            matchedTracks.Add(track.TrackId);
        }

        var lostTrackIds = new List<int>();
        foreach (var track in _tracks.Values.ToArray())
        {
            if (matchedTracks.Contains(track.TrackId))
            {
                continue;
            }

            track.MissingFrames++;
            if (track.MissingFrames >= _options.LostFrames)
            {
                lostTrackIds.Add(track.TrackId);
                _tracks.Remove(track.TrackId);
            }
        }

        var observedTargets = matchedTracks
            .Select(trackId => _tracks[trackId])
            .OrderBy(track => track.TrackId)
            .Select(track => new RadarTarget(
                track.TrackId,
                track.X,
                track.Y,
                float.NaN,
                float.NaN,
                MathF.Min(1f, track.ObservedFrames / (float)_options.ConfirmFrames),
                track.PointCount,
                track.ObservedFrames >= _options.ConfirmFrames))
            .ToArray();

        return new RadarTrackingUpdate(observedTargets, lostTrackIds);
    }

    public void Reset()
    {
        _tracks.Clear();
        _nextTrackId = 1;
    }

    private float Smooth(float previous, float current)
    {
        return previous + _options.SmoothingAlpha * (current - previous);
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        var x = x2 - x1;
        var y = y2 - y1;
        return MathF.Sqrt(x * x + y * y);
    }

    private sealed class TrackState
    {
        public int TrackId { get; init; }
        public float X { get; set; }
        public float Y { get; set; }
        public int PointCount { get; set; }
        public int ObservedFrames { get; set; }
        public int MissingFrames { get; set; }
    }

    private sealed record Association(int TrackId, int ClusterIndex, float Distance);
}
