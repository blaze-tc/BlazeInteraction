namespace Blaze.Provider.CameraVision.Tests;

public sealed class HandTrackAssignerTests
{
    [Fact]
    public void FirstFrame_AssignsEightUniquePositiveIds()
    {
        var assigner = CreateAssigner();

        var assignment = assigner.Update(
            Enumerable.Range(0, 8).Select(index => Candidate(index * 0.1f)).ToArray());

        Assert.Equal(8, assignment.Active.Count);
        Assert.Equal(8, assignment.Active.Select(track => track.TrackId).Distinct().Count());
        Assert.All(assignment.Active, track => Assert.True(track.TrackId > 0));
    }

    [Fact]
    public void OrdinaryMovement_PreservesIdsWhenBackendOrderReverses()
    {
        var assigner = CreateAssigner();
        var first = assigner.Update([Candidate(0.1f), Candidate(0.4f), Candidate(0.7f)]);
        var initialIds = first.Active.ToDictionary(track => track.Candidate.TrackingPoint.X,
            track => track.TrackId);

        var second = assigner.Update([Candidate(0.71f), Candidate(0.41f), Candidate(0.11f)]);

        Assert.Equal(initialIds[0.7f], TrackAt(second, 0.71f).TrackId);
        Assert.Equal(initialIds[0.4f], TrackAt(second, 0.41f).TrackId);
        Assert.Equal(initialIds[0.1f], TrackAt(second, 0.11f).TrackId);
    }

    [Fact]
    public void NinthHand_IsNotTruncated()
    {
        var assignment = CreateAssigner().Update(
            Enumerable.Range(0, 9).Select(index => Candidate(index * 0.05f)).ToArray());

        Assert.Equal(9, assignment.Active.Count);
    }

    [Fact]
    public void UnmatchedDetection_GetsNextMonotonicId()
    {
        var assigner = CreateAssigner(maximumMatchDistance: 0.1f);
        var first = assigner.Update([Candidate(0f)]);

        var second = assigner.Update([Candidate(1f)]);

        Assert.True(second.Active.Single().TrackId > first.Active.Single().TrackId);
    }

    [Fact]
    public void MissingHand_SurvivesConfiguredToleranceAndRecoversItsId()
    {
        var assigner = CreateAssigner(lostFrameTolerance: 1);
        var initialId = assigner.Update([Candidate(0.4f)]).Active.Single().TrackId;

        var missing = assigner.Update(Array.Empty<HandCandidate>());
        var recovered = assigner.Update([Candidate(0.41f)]);

        Assert.Empty(missing.Active);
        Assert.Empty(missing.RemovedTrackIds);
        Assert.Equal(initialId, recovered.Active.Single().TrackId);
    }

    [Fact]
    public void MissBeyondTolerance_RemovesTrackExactlyOnce()
    {
        var assigner = CreateAssigner(lostFrameTolerance: 1);
        var trackId = assigner.Update([Candidate(0.4f)]).Active.Single().TrackId;

        var firstMiss = assigner.Update(Array.Empty<HandCandidate>());
        var secondMiss = assigner.Update(Array.Empty<HandCandidate>());
        var thirdMiss = assigner.Update(Array.Empty<HandCandidate>());

        Assert.Empty(firstMiss.RemovedTrackIds);
        Assert.Equal(new[] { trackId }, secondMiss.RemovedTrackIds);
        Assert.Empty(thirdMiss.RemovedTrackIds);
    }

    [Fact]
    public void ReentryAfterRemoval_GetsNewId()
    {
        var assigner = CreateAssigner(lostFrameTolerance: 0);
        var initialId = assigner.Update([Candidate(0.4f)]).Active.Single().TrackId;
        assigner.Update(Array.Empty<HandCandidate>());

        var reentryId = assigner.Update([Candidate(0.4f)]).Active.Single().TrackId;

        Assert.True(reentryId > initialId);
    }

    [Fact]
    public void CrossingHands_MaySwapButNeverDuplicateAnId()
    {
        var assigner = CreateAssigner(maximumMatchDistance: 0.5f);
        var firstIds = assigner.Update([Candidate(0.4f), Candidate(0.6f)])
            .Active.Select(track => track.TrackId).Order().ToArray();

        var crossed = assigner.Update([Candidate(0.55f), Candidate(0.45f)]);
        var crossedIds = crossed.Active.Select(track => track.TrackId).Order().ToArray();

        Assert.Equal(firstIds, crossedIds);
        Assert.Equal(crossed.Active.Count,
            crossed.Active.Select(track => track.TrackId).Distinct().Count());
    }

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(-0.1f, 1)]
    [InlineData(float.NaN, 1)]
    [InlineData(0.1f, -1)]
    public void Constructor_RejectsInvalidConfiguration(
        float maximumMatchDistance,
        int lostFrameTolerance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HandTrackAssigner(maximumMatchDistance, lostFrameTolerance));
    }

    private static HandTrackAssigner CreateAssigner(
        float maximumMatchDistance = 0.2f,
        int lostFrameTolerance = 1) =>
        new(maximumMatchDistance, lostFrameTolerance);

    private static HandCandidate Candidate(float x, float y = 0.5f) =>
        new(new CameraPoint(x, y), ValidHand());

    private static DetectedHand ValidHand() => new(
        0.9f,
        Enumerable.Range(0, DetectedHand.LandmarkCount)
            .Select(_ => new HandLandmark(0.5f, 0.5f, 0f)));

    private static ActiveHandTrack TrackAt(HandTrackAssignment assignment, float x) =>
        assignment.Active.Single(track => track.Candidate.TrackingPoint.X == x);
}
