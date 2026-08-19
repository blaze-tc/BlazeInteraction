namespace Yuexin.Radar.Contracts;

public enum RadarPointerPhase
{
    Hover = 0,
    Down = 1,
    Move = 2,
    Up = 3
}

public sealed record RadarPointer(
    int PointerId,
    float NormalizedX,
    float NormalizedY,
    RadarPointerPhase Phase,
    float Confidence,
    long TimestampUnixMilliseconds);
