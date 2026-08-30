namespace Yuexin.Radar.Contracts;

public readonly record struct RadarPoint(
    int DistanceCentimeters,
    int AngleRaw,
    float AngleDegrees,
    float X,
    float Y);
