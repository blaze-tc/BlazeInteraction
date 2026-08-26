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
        string? error)
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
    public string? Error { get; }

    private static double RequireRate(double value, string parameterName) =>
        double.IsFinite(value) && value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);
}
