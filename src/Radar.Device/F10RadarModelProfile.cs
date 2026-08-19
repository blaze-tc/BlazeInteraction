using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Device;

public sealed class F10RadarModelProfile : IRadarModelProfile
{
    public RadarModel Model => RadarModel.F10;
    public string DisplayName => "FaseLase F10";
    public float MinimumDistanceMeters => 0.05f;
    public float MaximumDistanceMeters => 10f;
    public int MinimumScanFrequencyHz => 10;
    public int MaximumScanFrequencyHz => 25;
    public int DefaultScanFrequencyHz => 15;
    public float DefaultAngularResolutionDegrees => 0.27f;
    public float BlindZoneStartDegrees => 230f;
    public float BlindZoneEndDegrees => 310f;
}
