namespace Yuexin.Radar.Contracts;

public enum RadarConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Faulted = 4
}

public sealed record RadarStatus(
    RadarConnectionState ConnectionState,
    RadarModel DeviceModel,
    long ReceivedByteCount,
    long ParsedPointCount,
    long CrcErrorCount,
    long DiscardedByteCount,
    string? LastError,
    DateTimeOffset Timestamp);
