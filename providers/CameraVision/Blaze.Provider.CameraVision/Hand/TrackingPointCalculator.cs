namespace Blaze.Provider.CameraVision;

internal static class TrackingPointCalculator
{
    private const int IndexTipLandmarkIndex = 8;
    private static readonly int[] PalmLandmarkIndices = [0, 5, 9, 13, 17];

    public static CameraPoint Calculate(DetectedHand hand, HandTrackingPoint trackingPoint)
    {
        ArgumentNullException.ThrowIfNull(hand);

        return trackingPoint switch
        {
            HandTrackingPoint.IndexTip => FromLandmark(hand.Landmarks[IndexTipLandmarkIndex]),
            HandTrackingPoint.PalmCenter => CalculatePalmCenter(hand),
            _ => throw new ArgumentOutOfRangeException(
                nameof(trackingPoint), trackingPoint, "The hand tracking point is not defined.")
        };
    }

    private static CameraPoint CalculatePalmCenter(DetectedHand hand)
    {
        var x = 0f;
        var y = 0f;
        foreach (var index in PalmLandmarkIndices)
        {
            x += hand.Landmarks[index].X;
            y += hand.Landmarks[index].Y;
        }

        return new CameraPoint(x / PalmLandmarkIndices.Length, y / PalmLandmarkIndices.Length);
    }

    private static CameraPoint FromLandmark(HandLandmark landmark) =>
        new(landmark.X, landmark.Y);
}
