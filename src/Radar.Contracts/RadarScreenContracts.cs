namespace Yuexin.Radar.Contracts;

public readonly record struct RadarPixelRect(int X, int Y, int Width, int Height);

public readonly record struct RadarScreenPoint(float PixelX, float PixelY);

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

public sealed record RadarScreenPointer
{
    public RadarScreenPointer(
        int pointerId,
        RadarPointerPhase phase,
        float normalizedX,
        float normalizedY,
        float pixelX,
        float pixelY,
        float confidence,
        long timestampUnixMilliseconds,
        IReadOnlyList<RadarScreenPoint>? footprint = null)
    {
        PointerId = pointerId;
        Phase = phase;
        NormalizedX = normalizedX;
        NormalizedY = normalizedY;
        PixelX = pixelX;
        PixelY = pixelY;
        Confidence = confidence;
        TimestampUnixMilliseconds = timestampUnixMilliseconds;
        Footprint = RadarScreenFootprints.Freeze(footprint);
    }

    public int PointerId { get; }
    public RadarPointerPhase Phase { get; }
    public float NormalizedX { get; }
    public float NormalizedY { get; }
    public float PixelX { get; }
    public float PixelY { get; }
    public float Confidence { get; }
    public long TimestampUnixMilliseconds { get; }
    public IReadOnlyList<RadarScreenPoint> Footprint { get; }
}

public sealed record RadarScreenPointerFrame(
    RadarScreenInfo Screen,
    long Sequence,
    long TimestampUnixMilliseconds,
    IReadOnlyList<RadarScreenPointer> Pointers);

internal static class RadarScreenFootprints
{
    internal static IReadOnlyList<RadarScreenPoint> Freeze(IReadOnlyList<RadarScreenPoint>? footprint) =>
        footprint is null || footprint.Count == 0
            ? Array.Empty<RadarScreenPoint>()
            : Array.AsReadOnly(footprint.ToArray());
}
