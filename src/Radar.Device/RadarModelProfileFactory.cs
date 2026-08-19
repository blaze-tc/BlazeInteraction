using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Device;

public static class RadarModelProfileFactory
{
    private static readonly IRadarModelProfile F10 = new F10RadarModelProfile();
    private static readonly IRadarModelProfile F20 = new F20RadarModelProfile();

    public static IRadarModelProfile CreateDefault() => F10;

    public static IRadarModelProfile Create(RadarModel model) => model switch
    {
        RadarModel.F20 => F20,
        _ => F10
    };
}
