using System.Collections.ObjectModel;

namespace Blaze.Provider.CameraVision;

internal sealed class HandCandidate
{
    public HandCandidate(CameraPoint trackingPoint, DetectedHand hand)
    {
        if (!float.IsFinite(trackingPoint.X) || !float.IsFinite(trackingPoint.Y))
        {
            throw new ArgumentOutOfRangeException(
                nameof(trackingPoint),
                "Hand candidate coordinates must be finite.");
        }

        TrackingPoint = trackingPoint;
        Hand = hand ?? throw new ArgumentNullException(nameof(hand));
    }

    public CameraPoint TrackingPoint { get; }
    public DetectedHand Hand { get; }
}

internal sealed class ActiveHandTrack
{
    public ActiveHandTrack(long trackId, HandCandidate candidate)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId), "Track IDs must be positive.");
        }

        TrackId = trackId;
        Candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
    }

    public long TrackId { get; }
    public HandCandidate Candidate { get; }
}

internal sealed class HandTrackAssignment
{
    public HandTrackAssignment(
        IEnumerable<ActiveHandTrack> active,
        IEnumerable<long> removedTrackIds)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(removedTrackIds);
        Active = Array.AsReadOnly(active.ToArray());
        RemovedTrackIds = Array.AsReadOnly(removedTrackIds.ToArray());
    }

    public ReadOnlyCollection<ActiveHandTrack> Active { get; }
    public ReadOnlyCollection<long> RemovedTrackIds { get; }
}
