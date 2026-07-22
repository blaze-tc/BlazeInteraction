using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Services;

public sealed record RadarRuntimeSnapshot(
    long Sequence,
    DateTimeOffset Timestamp,
    IReadOnlyList<RadarPoint> RawPoints,
    IReadOnlyList<RadarPoint> ValidPoints,
    IReadOnlyList<RadarCluster> Clusters,
    IReadOnlyList<RadarTarget> Targets,
    IReadOnlyList<RadarPointer> Pointers,
    double ScanFrequencyHz,
    double ReceivedBytesPerSecond,
    long CrcErrorCount = 0,
    long DiscardedByteCount = 0);

public sealed record UnityClientStatus(
    bool IsConnected,
    int ProcessId,
    string UnityVersion,
    IReadOnlyList<RadarScreenInfo> Screens,
    DateTimeOffset? LastBatchSentAt,
    long LastBatchSequence,
    string? LastError)
{
    public static UnityClientStatus Disconnected { get; } = new(
        false,
        0,
        string.Empty,
        [],
        null,
        0,
        null);
}

public sealed record RadarScreenRuntimeSnapshot(
    RadarScreenInfo Screen,
    IReadOnlyList<RadarSensorRuntimeSnapshot> Sensors,
    IReadOnlyList<FusedScreenTarget> Targets,
    IReadOnlyList<RadarScreenPointer> Pointers,
    long Sequence,
    DateTimeOffset Timestamp);

public interface IRadarBridgeRuntime : IAsyncDisposable
{
    event Action<RadarSensorRuntimeSnapshot>? SensorSnapshotUpdated { add { } remove { } }
    event Action<RadarScreenRuntimeSnapshot>? ScreenSnapshotUpdated { add { } remove { } }
    event Action<RadarRuntimeSnapshot>? SnapshotUpdated;
    event Action<string>? LogReceived;
    event Action<RadarConnectionState>? ConnectionStateChanged;
    event Action<UnityClientStatus>? UnityStatusChanged;

    RadarConnectionState ConnectionState { get; }
    UnityClientStatus UnityStatus { get; }

    Task StartInfrastructureAsync(CancellationToken cancellationToken = default);

    Task ConnectSensorAsync(string screenId, string sensorId, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task DisconnectSensorAsync(string screenId, string sensorId) => Task.FromException(new NotSupportedException());
    Task ConnectScreenAsync(string screenId, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task DisconnectScreenAsync(string screenId) => Task.FromException(new NotSupportedException());
    Task ConnectAllAsync(CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task DisconnectAllAsync() => Task.FromException(new NotSupportedException());
    Task StartAllSimulationAsync(CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task StartRecordingAsync(string screenId, string sensorId, string path, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    Task StopRecordingAsync(string screenId, string sensorId) => Task.FromException(new NotSupportedException());
    Task ReplaySensorAsync(string screenId, string sensorId, string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());
    void PauseReplay(string screenId, string sensorId) => throw new NotSupportedException();
    void ResumeReplay(string screenId, string sensorId) => throw new NotSupportedException();
    void StepReplay(string screenId, string sensorId) => throw new NotSupportedException();
    Task StopReplayAsync(string screenId, string sensorId) => Task.FromException(new NotSupportedException());
    Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException());

}
