using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Device;

public sealed class F20RadarModelProfile : IRadarModelProfile
{
    public RadarModel Model => RadarModel.F20;
    public string DisplayName => "FaseLase F20";
    public float MinimumDistanceMeters => 0.05f;
    public float MaximumDistanceMeters => 40f;
    public int MinimumScanFrequencyHz => 10;
    public int MaximumScanFrequencyHz => 30;
    public int DefaultScanFrequencyHz => 25;
    public float DefaultAngularResolutionDegrees => 0.3f;
    public float BlindZoneStartDegrees => 230f;
    public float BlindZoneEndDegrees => 310f;
}
