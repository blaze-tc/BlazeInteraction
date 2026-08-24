namespace Blaze.Provider.CameraVision;

public sealed record VisualDetection
{
    public VisualDetection(
        string detectorId,
        string detectionType,
        float cameraX,
        float cameraY,
        float confidence)
    {
        DetectorId = string.IsNullOrWhiteSpace(detectorId)
            ? throw new ArgumentException("A detector identifier is required.", nameof(detectorId))
            : detectorId;
        DetectionType = string.IsNullOrWhiteSpace(detectionType)
            ? throw new ArgumentException("A detection type is required.", nameof(detectionType))
            : detectionType;
        if (!float.IsFinite(cameraX) || cameraX is < 0f or > 1f ||
            !float.IsFinite(cameraY) || cameraY is < 0f or > 1f ||
            !float.IsFinite(confidence) || confidence is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(cameraX),
                "Detection coordinates and confidence must be finite unit-interval values.");
        }

        CameraX = cameraX;
        CameraY = cameraY;
        Confidence = confidence;
    }

    public string DetectorId { get; }
    public string DetectionType { get; }
    public float CameraX { get; }
    public float CameraY { get; }
    public float Confidence { get; }
}

public interface IVisualDetector
{
    IReadOnlyList<VisualDetection> Detect(CameraFrame? frame, TimeSpan elapsed);
}
