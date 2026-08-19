using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class RadarTargetTrackerTests
{
    [Fact]
    public void MovingTarget_KeepsTrackIdAndConfirmsAfterRequiredFrames()
    {
        var tracker = CreateTracker();

        var first = tracker.Update([Cluster(0, 0f)]);
        var second = tracker.Update([Cluster(0, 0.1f)]);

        Assert.Single(first.ObservedTargets);
        Assert.False(first.ObservedTargets[0].IsConfirmed);
        Assert.Single(second.ObservedTargets);
        Assert.True(second.ObservedTargets[0].IsConfirmed);
        Assert.Equal(first.ObservedTargets[0].TrackId, second.ObservedTargets[0].TrackId);
    }

    [Fact]
    public void TwoTargets_DoNotSwapIdsWhenInputOrderChanges()
    {
        var tracker = CreateTracker();
        tracker.Update([Cluster(0, 0f), Cluster(1, 1f)]);
        var confirmed = tracker.Update([Cluster(0, 0.05f), Cluster(1, 0.95f)]);
        var leftId = confirmed.ObservedTargets.Single(target => target.PhysicalX < 0.5f).TrackId;
        var rightId = confirmed.ObservedTargets.Single(target => target.PhysicalX > 0.5f).TrackId;

        var moved = tracker.Update([Cluster(0, 0.9f), Cluster(1, 0.1f)]);

        Assert.Equal(leftId, moved.ObservedTargets.Single(target => target.PhysicalX < 0.5f).TrackId);
        Assert.Equal(rightId, moved.ObservedTargets.Single(target => target.PhysicalX > 0.5f).TrackId);
    }

    [Fact]
    public void MissingTarget_IsOnlyReportedLostAfterConfiguredFrames()
    {
        var tracker = CreateTracker();
        tracker.Update([Cluster(0, 0f)]);
        tracker.Update([Cluster(0, 0f)]);

        Assert.Empty(tracker.Update([]).LostTrackIds);
        Assert.Empty(tracker.Update([]).LostTrackIds);
        var lost = tracker.Update([]);

        Assert.Single(lost.LostTrackIds);
    }

    private static RadarTargetTracker CreateTracker()
    {
        return new RadarTargetTracker(new RadarTrackingOptions
        {
            ConfirmFrames = 2,
            LostFrames = 3,
            MaximumAssociationDistanceMeters = 0.5f,
            SmoothingAlpha = 0.5f
        });
    }

    private static RadarCluster Cluster(int index, float x)
    {
        var point = new RadarPoint(100, 0, 0f, x, 0f);
        return new RadarCluster(index, [point, point], x, 0f, 0.1f, 1f);
    }
}
