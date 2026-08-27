using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision;

internal sealed record CameraVisionStatusSnapshot
{
    internal CameraVisionStatusSnapshot(
        ProviderRuntimeStatus providerStatus,
        CameraCaptureStatus cameraStatus,
        double cameraFramesPerSecond,
        double inferenceFramesPerSecond,
        double outputFramesPerSecond,
        double inferenceLatencyMilliseconds,
        int detectedHandCount,
        IEnumerable<Vector2Data> trackingCoordinates,
        long droppedFrames,
        bool unityConnected,
        CameraPreviewSnapshot? preview,
        IEnumerable<CameraHandSnapshot>? hands,
        string? error,
        long timestampUnixMs = 0,
        int actualWidth = 0,
        int actualHeight = 0,
        IEnumerable<InteractionPoint>? outputPoints = null,
        IEnumerable<Vector2Data>? calibrationPoints = null)
    {
        ArgumentNullException.ThrowIfNull(trackingCoordinates);
        ProviderStatus = providerStatus;
        CameraStatus = cameraStatus;
        CameraFramesPerSecond = RequireRate(cameraFramesPerSecond, nameof(cameraFramesPerSecond));
        InferenceFramesPerSecond = RequireRate(inferenceFramesPerSecond, nameof(inferenceFramesPerSecond));
        OutputFramesPerSecond = RequireRate(outputFramesPerSecond, nameof(outputFramesPerSecond));
        InferenceLatencyMilliseconds = RequireRate(
            inferenceLatencyMilliseconds,
            nameof(inferenceLatencyMilliseconds));
        DetectedHandCount = detectedHandCount;
        TrackingCoordinates = Array.AsReadOnly(trackingCoordinates.ToArray());
        DroppedFrames = droppedFrames;
        UnityConnected = unityConnected;
        Preview = preview;
        Hands = Array.AsReadOnly((hands ?? Enumerable.Empty<CameraHandSnapshot>()).ToArray());
        if (timestampUnixMs < 0 || actualWidth < 0 || actualHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampUnixMs));
        }
        TimestampUnixMs = timestampUnixMs;
        ActualWidth = actualWidth;
        ActualHeight = actualHeight;
        OutputPoints = Array.AsReadOnly(
            (outputPoints ?? Enumerable.Empty<InteractionPoint>()).ToArray());
        CalibrationPoints = Array.AsReadOnly(
            (calibrationPoints ?? Enumerable.Empty<Vector2Data>()).ToArray());
        Error = error;
    }

    public ProviderRuntimeStatus ProviderStatus { get; }
    public CameraCaptureStatus CameraStatus { get; }
    public double CameraFramesPerSecond { get; }
    public double InferenceFramesPerSecond { get; }
    public double OutputFramesPerSecond { get; }
    public double InferenceLatencyMilliseconds { get; }
    public int DetectedHandCount { get; }
    public IReadOnlyList<Vector2Data> TrackingCoordinates { get; }
    public long DroppedFrames { get; }
    public bool UnityConnected { get; }
    public CameraPreviewSnapshot? Preview { get; }
    public IReadOnlyList<CameraHandSnapshot> Hands { get; }
    public long TimestampUnixMs { get; }
    public int ActualWidth { get; }
    public int ActualHeight { get; }
    public IReadOnlyList<InteractionPoint> OutputPoints { get; }
    public IReadOnlyList<Vector2Data> CalibrationPoints { get; }
    public string? Error { get; }

    private static double RequireRate(double value, string parameterName) =>
        double.IsFinite(value) && value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}
