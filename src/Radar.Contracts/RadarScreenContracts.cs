namespace Yuexin.Radar.Contracts;

public readonly record struct RadarPixelRect(int X, int Y, int Width, int Height);

public sealed record RadarScreenDefinitionPayload(
    string ScreenId,
    string Name,
    int DefaultWidthPixels,
    int DefaultHeightPixels,
    bool IsPrimary,
    int Order);

public sealed record RadarScreenInfo(
    string ScreenId,
    string Name,
    int WidthPixels,
    int HeightPixels,
    bool IsPrimary,
    int Order);

public sealed record RadarScreenPointer(
    int PointerId,
    RadarPointerPhase Phase,
    float NormalizedX,
    float NormalizedY,
    float PixelX,
    float PixelY,
    float Confidence,
    long TimestampUnixMilliseconds);

public sealed record RadarScreenPointerFrame(
    RadarScreenInfo Screen,
    long Sequence,
    long TimestampUnixMilliseconds,
    IReadOnlyList<RadarScreenPointer> Pointers);
