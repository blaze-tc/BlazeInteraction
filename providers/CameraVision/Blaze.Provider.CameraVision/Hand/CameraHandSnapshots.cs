using System.Collections.ObjectModel;
using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision;

internal sealed class CameraMappedLandmark
{
    public CameraMappedLandmark(
        int index,
        Vector2Data cameraPixelPosition,
        Vector2Data normalizedPosition,
        float z)
    {
        if (index is < 0 or >= DetectedHand.LandmarkCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (!float.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }

        Index = index;
        CameraPixelPosition = cameraPixelPosition
            ?? throw new ArgumentNullException(nameof(cameraPixelPosition));
        NormalizedPosition = normalizedPosition
            ?? throw new ArgumentNullException(nameof(normalizedPosition));
        Z = z;
    }

    public int Index { get; }
    public Vector2Data CameraPixelPosition { get; }
    public Vector2Data NormalizedPosition { get; }
    public float Z { get; }
}

internal sealed class CameraHandSnapshot
{
    public CameraHandSnapshot(
        long trackId,
        float confidence,
        Vector2Data cameraTrackingPoint,
        Vector2Data normalizedPosition,
        IEnumerable<CameraMappedLandmark> landmarks)
    {
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        if (!float.IsFinite(confidence) || confidence is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        ArgumentNullException.ThrowIfNull(landmarks);
        var landmarkSnapshot = landmarks.ToArray();
        if (landmarkSnapshot.Length != DetectedHand.LandmarkCount)
        {
            throw new ArgumentException(
                $"A camera hand snapshot requires {DetectedHand.LandmarkCount} landmarks.",
                nameof(landmarks));
        }

        TrackId = trackId;
        Confidence = confidence;
        CameraTrackingPoint = cameraTrackingPoint
            ?? throw new ArgumentNullException(nameof(cameraTrackingPoint));
        NormalizedPosition = normalizedPosition
            ?? throw new ArgumentNullException(nameof(normalizedPosition));
        Landmarks = Array.AsReadOnly(landmarkSnapshot);
    }

    public long TrackId { get; }
    public float Confidence { get; }
    public Vector2Data CameraTrackingPoint { get; }
    public Vector2Data NormalizedPosition { get; }
    public ReadOnlyCollection<CameraMappedLandmark> Landmarks { get; }
}

internal sealed class CameraPreviewSnapshot
{
    public CameraPreviewSnapshot(int width, int height, int strideBytes, byte[] bgr24)
    {
        if (width <= 0 || height <= 0 || strideBytes != checked(width * 3))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        ArgumentNullException.ThrowIfNull(bgr24);
        if (bgr24.Length != checked(strideBytes * height))
        {
            throw new ArgumentException("Preview buffer size does not match its dimensions.", nameof(bgr24));
        }

        Width = width;
        Height = height;
        StrideBytes = strideBytes;
        Bgr24 = Array.AsReadOnly((byte[])bgr24.Clone());
    }

    public int Width { get; }
    public int Height { get; }
    public int StrideBytes { get; }
    public ReadOnlyCollection<byte> Bgr24 { get; }
}

internal sealed class CameraHandFrame
{
    public CameraHandFrame(
        long sourceSequence,
        long timestampUnixMs,
        IEnumerable<CameraHandSnapshot> activeHands,
        IEnumerable<long> removedTrackIds,
        CameraPreviewSnapshot preview,
        double inferenceMilliseconds,
        long processedFrameCount,
        long droppedFrameCount,
        int detectedHandCount,
        int rejectedHandCount)
    {
        ArgumentNullException.ThrowIfNull(activeHands);
        ArgumentNullException.ThrowIfNull(removedTrackIds);
        if (!double.IsFinite(inferenceMilliseconds) || inferenceMilliseconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(inferenceMilliseconds));
        }

        SourceSequence = sourceSequence;
        TimestampUnixMs = timestampUnixMs;
        ActiveHands = Array.AsReadOnly(activeHands.ToArray());
        RemovedTrackIds = Array.AsReadOnly(removedTrackIds.ToArray());
        Preview = preview ?? throw new ArgumentNullException(nameof(preview));
        InferenceMilliseconds = inferenceMilliseconds;
        ProcessedFrameCount = processedFrameCount;
        DroppedFrameCount = droppedFrameCount;
        DetectedHandCount = detectedHandCount;
        RejectedHandCount = rejectedHandCount;
    }

    public long SourceSequence { get; }
    public long TimestampUnixMs { get; }
    public ReadOnlyCollection<CameraHandSnapshot> ActiveHands { get; }
    public ReadOnlyCollection<long> RemovedTrackIds { get; }
    public CameraPreviewSnapshot Preview { get; }
    public double InferenceMilliseconds { get; }
    public long ProcessedFrameCount { get; }
    public long DroppedFrameCount { get; }
    public int DetectedHandCount { get; }
    public int RejectedHandCount { get; }
}
