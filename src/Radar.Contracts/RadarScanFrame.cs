namespace Yuexin.Radar.Contracts;

public sealed record RadarScanFrame(
    long Sequence,
    DateTimeOffset Timestamp,
    IReadOnlyList<RadarPoint> Points);
