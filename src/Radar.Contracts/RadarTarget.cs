namespace Yuexin.Radar.Contracts;

public sealed record RadarTarget(
    int TrackId,
    float PhysicalX,
    float PhysicalY,
    float NormalizedX,
    float NormalizedY,
    float Confidence,
    int PointCount,
    bool IsConfirmed);
