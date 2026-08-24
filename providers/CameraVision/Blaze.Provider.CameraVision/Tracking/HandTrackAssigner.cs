namespace Blaze.Provider.CameraVision;

internal sealed class HandTrackAssigner
{
    private readonly float _maximumMatchDistanceSquared;
    private readonly int _lostFrameTolerance;
    private readonly Dictionary<long, TrackState> _tracks = new();
    private long _nextTrackId = 1;

    public HandTrackAssigner(float maximumMatchDistance, int lostFrameTolerance)
    {
        if (!float.IsFinite(maximumMatchDistance) || maximumMatchDistance <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMatchDistance),
                "Maximum match distance must be finite and positive.");
        }

        if (lostFrameTolerance < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lostFrameTolerance),
                "Lost frame tolerance cannot be negative.");
        }

        _maximumMatchDistanceSquared = maximumMatchDistance * maximumMatchDistance;
        _lostFrameTolerance = lostFrameTolerance;
    }

    public HandTrackAssignment Update(IReadOnlyList<HandCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        for (var index = 0; index < candidates.Count; index++)
        {
            if (candidates[index] is null)
            {
                throw new ArgumentException(
                    "Hand candidate collections cannot contain null values.",
                    nameof(candidates));
            }
        }

        var existingTracks = _tracks.Values.ToArray();
        var pairs = new List<MatchPair>(existingTracks.Length * candidates.Count);
        foreach (var track in existingTracks)
        {
            for (var detectionIndex = 0; detectionIndex < candidates.Count; detectionIndex++)
            {
                var candidate = candidates[detectionIndex];
                var distanceSquared = DistanceSquared(track.LastPoint, candidate.TrackingPoint);
                if (distanceSquared <= _maximumMatchDistanceSquared)
                {
                    pairs.Add(new MatchPair(track.TrackId, detectionIndex, distanceSquared));
                }
            }
        }

        pairs.Sort(static (left, right) =>
        {
            var distanceOrder = left.DistanceSquared.CompareTo(right.DistanceSquared);
            if (distanceOrder != 0)
            {
                return distanceOrder;
            }

            var trackOrder = left.TrackId.CompareTo(right.TrackId);
            return trackOrder != 0
                ? trackOrder
                : left.DetectionIndex.CompareTo(right.DetectionIndex);
        });

        var matchedTrackIds = new HashSet<long>();
        var matchedDetectionIndices = new HashSet<int>();
        var activeByDetection = new Dictionary<int, ActiveHandTrack>();
        foreach (var pair in pairs)
        {
            if (matchedTrackIds.Contains(pair.TrackId)
                || matchedDetectionIndices.Contains(pair.DetectionIndex))
            {
                continue;
            }

            matchedTrackIds.Add(pair.TrackId);
            matchedDetectionIndices.Add(pair.DetectionIndex);
            var candidate = candidates[pair.DetectionIndex];
            var track = _tracks[pair.TrackId];
            track.LastPoint = candidate.TrackingPoint;
            track.MissedFrames = 0;
            activeByDetection.Add(
                pair.DetectionIndex,
                new ActiveHandTrack(track.TrackId, candidate));
        }

        var removedTrackIds = new List<long>();
        foreach (var track in existingTracks)
        {
            if (matchedTrackIds.Contains(track.TrackId))
            {
                continue;
            }

            track.MissedFrames++;
            if (track.MissedFrames > _lostFrameTolerance)
            {
                _tracks.Remove(track.TrackId);
                removedTrackIds.Add(track.TrackId);
            }
        }

        for (var detectionIndex = 0; detectionIndex < candidates.Count; detectionIndex++)
        {
            if (matchedDetectionIndices.Contains(detectionIndex))
            {
                continue;
            }

            var candidate = candidates[detectionIndex];
            var trackId = AllocateTrackId();
            _tracks.Add(trackId, new TrackState(trackId, candidate.TrackingPoint));
            activeByDetection.Add(detectionIndex, new ActiveHandTrack(trackId, candidate));
        }

        return new HandTrackAssignment(
            activeByDetection.OrderBy(static pair => pair.Key).Select(static pair => pair.Value),
            removedTrackIds.Order());
    }

    private long AllocateTrackId()
    {
        var trackId = _nextTrackId;
        _nextTrackId = checked(_nextTrackId + 1);
        return trackId;
    }

    private static float DistanceSquared(CameraPoint first, CameraPoint second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return (x * x) + (y * y);
    }

    private sealed class TrackState
    {
        public TrackState(long trackId, CameraPoint lastPoint)
        {
            TrackId = trackId;
            LastPoint = lastPoint;
        }

        public long TrackId { get; }
        public CameraPoint LastPoint { get; set; }
        public int MissedFrames { get; set; }
    }

    private readonly record struct MatchPair(
        long TrackId,
        int DetectionIndex,
        float DistanceSquared);
}
