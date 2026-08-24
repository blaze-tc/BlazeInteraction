using System.Text.Json.Serialization;

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

[method: JsonConstructor]
public sealed record RadarScreenPointer(
    int PointerId,
    RadarPointerPhase Phase,
    float NormalizedX,
    float NormalizedY,
    float PixelX,
    float PixelY,
    float Confidence,
    long TimestampUnixMilliseconds)
{
    public RadarScreenPointer(
        int PointerId,
        RadarPointerPhase Phase,
        float NormalizedX,
        float NormalizedY,
        float PixelX,
        float PixelY,
        float Confidence,
        long TimestampUnixMilliseconds,
        IReadOnlyList<RadarScreenPoint> Footprint)
        : this(
            PointerId,
            Phase,
            NormalizedX,
            NormalizedY,
            PixelX,
            PixelY,
            Confidence,
            TimestampUnixMilliseconds)
    {
        this.Footprint = Footprint;
    }

    private IReadOnlyList<RadarScreenPoint> _footprint = Array.Empty<RadarScreenPoint>();

    public IReadOnlyList<RadarScreenPoint> Footprint
    {
        get => _footprint;
        init => _footprint = RadarScreenFootprints.Freeze(value);
    }
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
