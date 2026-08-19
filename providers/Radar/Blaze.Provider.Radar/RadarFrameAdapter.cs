using System.Text.Json;
using Blaze.Interaction.Contracts;
using Yuexin.Radar.Contracts;

namespace Blaze.Provider.Radar;

/// <summary>
/// Adapts the existing screen-fused Radar pointer stream without claiming
/// provenance from any individual F10/F20 sensor.
/// </summary>
public sealed class RadarFrameAdapter
{
    public const string ProviderId = "blaze.radar.f10f20";
    public const string FusedSourceId = "radar-fused-output";
    public const string FusedSensorId = "fused-output";

    private static readonly JsonElement FusedRadarExtension = JsonSerializer.SerializeToElement(
        new RadarInteractionExtension(FusedSensorId),
        InteractionJson.Options);

    private readonly string _providerInstanceId;

    public RadarFrameAdapter(string providerInstanceId)
    {
        _providerInstanceId = string.IsNullOrWhiteSpace(providerInstanceId)
            ? throw new ArgumentException("A provider instance identifier is required.", nameof(providerInstanceId))
            : providerInstanceId;
    }

    public IReadOnlyList<InteractionFrame> Adapt(PointerBatchPayload batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Screens);

        return Array.AsReadOnly(batch.Screens.Select(AdaptFrame).ToArray());
    }

    private InteractionFrame AdaptFrame(RadarScreenPointerFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Screen);
        ArgumentNullException.ThrowIfNull(frame.Pointers);

        var points = frame.Pointers.Select(pointer => AdaptPoint(frame.Screen.ScreenId, pointer)).ToArray();
        return new InteractionFrame
        {
            ProviderId = ProviderId,
            ProviderInstanceId = _providerInstanceId,
            SurfaceId = frame.Screen.ScreenId,
            Sequence = frame.Sequence,
            TimestampUnixMs = frame.TimestampUnixMilliseconds,
            Points = points
        };
    }

    private InteractionPoint AdaptPoint(string surfaceId, RadarScreenPointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);

        return new InteractionPoint
        {
            Id = pointer.PointerId,
            SurfaceId = surfaceId,
            ProviderId = ProviderId,
            ProviderInstanceId = _providerInstanceId,
            SourceId = FusedSourceId,
            Phase = MapPhase(pointer.Phase),
            NormalizedPosition = new Vector2Data(pointer.NormalizedX, pointer.NormalizedY),
            PixelPosition = new Vector2Data(pointer.PixelX, pointer.PixelY),
            Confidence = pointer.Confidence,
            TimestampUnixMs = pointer.TimestampUnixMilliseconds,
            Extensions = new InteractionExtensions(
            [
                new KeyValuePair<string, JsonElement>("radar", FusedRadarExtension)
            ])
        };
    }

    private static InteractionPhase MapPhase(RadarPointerPhase phase) => phase switch
    {
        RadarPointerPhase.Hover => InteractionPhase.Hover,
        RadarPointerPhase.Down => InteractionPhase.Down,
        RadarPointerPhase.Move => InteractionPhase.Move,
        RadarPointerPhase.Up => InteractionPhase.Up,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "The Radar pointer phase is not supported.")
    };
}
