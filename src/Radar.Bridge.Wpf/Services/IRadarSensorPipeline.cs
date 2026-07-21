using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Services;

public enum RadarSensorRuntimeState
{
    Stopped,
    Starting,
    Running,
    Reconnecting,
    Faulted
}

public sealed record RadarSensorRuntimeSnapshot(
    string ScreenId,
    string SensorId,
    long Sequence,
    DateTimeOffset Timestamp,
    IReadOnlyList<RadarPoint> RawPoints,
    IReadOnlyList<RadarPoint> ValidPoints,
    IReadOnlyList<RadarCluster> Clusters,
    IReadOnlyList<SensorDetection> Detections,
    double ScanFrequencyHz,
    double ReceivedBytesPerSecond,
    long CrcErrorCount,
    long DiscardedByteCount,
    long DroppedInputFrameCount);

public interface IRadarSensorPipeline : IAsyncDisposable
{
    string ScreenId { get; }
    string SensorId { get; }
    RadarSensorRuntimeState State { get; }
    long DroppedInputFrameCount { get; }

    event Action<RadarSensorRuntimeSnapshot>? SnapshotUpdated;
    event Action<SensorDetectionFrame>? DetectionFrameUpdated;
    event Action<RadarSensorRuntimeState>? StateChanged;
    event Action<string>? LogReceived;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task StartRecordingAsync(string path, CancellationToken cancellationToken = default);
    Task StopRecordingAsync();
    Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default);
    void PauseReplay();
    void ResumeReplay();
    void StepReplay();
    Task StopReplayAsync();
}
