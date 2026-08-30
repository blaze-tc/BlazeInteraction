namespace Blaze.Provider.CameraVision;

public enum CameraRotation
{
    Rotate0 = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270
}

public sealed record CameraCaptureOptions
{
    public int DeviceIndex { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public double FramesPerSecond { get; init; } = 30;
    public bool MirrorX { get; init; }
    public CameraRotation Rotation { get; init; } = CameraRotation.Rotate0;
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        if (DeviceIndex < 0 || Width <= 0 || Height <= 0 ||
            !double.IsFinite(FramesPerSecond) || FramesPerSecond <= 0 ||
            ReconnectDelay <= TimeSpan.Zero || ReconnectDelay == Timeout.InfiniteTimeSpan ||
            !Enum.IsDefined(Rotation))
        {
            throw new ArgumentOutOfRangeException(nameof(CameraCaptureOptions),
                "Camera capture options are invalid.");
        }
    }
}

public sealed record CameraDeviceDescriptor(int Index, string DisplayName);

public sealed record CameraCaptureMode
{
    public CameraCaptureMode(int width, int height, double framesPerSecond)
    {
        if (width <= 0 || height <= 0 ||
            !double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width),
                "Camera capture mode is invalid.");
        }

        Width = width;
        Height = height;
        FramesPerSecond = framesPerSecond;
    }

    public int Width { get; }
    public int Height { get; }
    public double FramesPerSecond { get; }
}

public sealed record CameraDeviceCapabilities(
    CameraDeviceDescriptor Device,
    IReadOnlyList<CameraCaptureMode> Modes,
    bool UsesFallbackPresets,
    string? Warning);

public interface ICameraCaptureBackendFactory
{
    ICameraCaptureBackend Create();
}

public interface ICameraCaptureBackend : IAsyncDisposable
{
    bool IsOpen { get; }
    bool TryOpen(CameraCaptureOptions options);
    bool TryGetActiveMode(out CameraCaptureMode? mode)
    {
        mode = null;
        return false;
    }
    bool TryRead(out CameraFrame? frame);
    void Close();
}

public enum CameraCaptureStatus
{
    Stopped = 0,
    Connecting = 1,
    Connected = 2,
    Disconnected = 3,
    Reconnecting = 4
}

public sealed class CameraCaptureStatusChangedEventArgs(
    CameraCaptureStatus previousStatus,
    CameraCaptureStatus status) : EventArgs
{
    public CameraCaptureStatus PreviousStatus { get; } = previousStatus;
    public CameraCaptureStatus Status { get; } = status;
}

public sealed record CameraFrameStatistics(
    long CapturedFrames,
    long DroppedFrames,
    long ReconnectAttempts,
    int ActualWidth,
    int ActualHeight,
    double RequestedFramesPerSecond,
    long? LastFrameTimestampUnixMs);
