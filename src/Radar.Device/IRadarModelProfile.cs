using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Device;

public interface IRadarModelProfile
{
    RadarModel Model { get; }
    string DisplayName { get; }
    float MinimumDistanceMeters { get; }
    float MaximumDistanceMeters { get; }
    int MinimumScanFrequencyHz { get; }
    int MaximumScanFrequencyHz { get; }
    int DefaultScanFrequencyHz { get; }
    float DefaultAngularResolutionDegrees { get; }
    float BlindZoneStartDegrees { get; }
    float BlindZoneEndDegrees { get; }
}
