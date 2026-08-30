namespace Blaze.Provider.CameraVision.Tests;

public sealed class TrackingPointCalculatorTests
{
    [Fact]
    public void IndexTip_ReadsLandmarkEightWithoutClamping()
    {
        var hand = HandWithSelectedPoints((8, 1.2f, -0.25f));

        var point = TrackingPointCalculator.Calculate(hand, HandTrackingPoint.IndexTip);

        Assert.Equal(new CameraPoint(1.2f, -0.25f), point);
    }

    [Fact]
    public void PalmCenter_AveragesLandmarksZeroFiveNineThirteenSeventeen()
    {
        var hand = HandWithSelectedPoints(
            (0, 0f, 0f),
            (5, 0.2f, 0.2f),
            (9, 0.4f, 0.4f),
            (13, 0.6f, 0.6f),
            (17, 0.8f, 0.8f));

        var point = TrackingPointCalculator.Calculate(hand, HandTrackingPoint.PalmCenter);

        Assert.Equal(new CameraPoint(0.4f, 0.4f), point);
    }

    [Fact]
    public void Calculate_RejectsUndefinedTrackingPoint()
    {
        var hand = HandWithSelectedPoints();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TrackingPointCalculator.Calculate(hand, (HandTrackingPoint)999));
    }

    [Fact]
    public void Calculate_RejectsNullHand()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TrackingPointCalculator.Calculate(null!, HandTrackingPoint.IndexTip));
    }

    private static DetectedHand HandWithSelectedPoints(
        params (int Index, float X, float Y)[] selectedPoints)
    {
        var landmarks = Enumerable.Range(0, DetectedHand.LandmarkCount)
            .Select(_ => new HandLandmark(0f, 0f, 0f))
            .ToArray();

        foreach (var point in selectedPoints)
        {
            landmarks[point.Index] = new HandLandmark(point.X, point.Y, 0f);
        }

        return new DetectedHand(0.9f, landmarks);
    }
}
