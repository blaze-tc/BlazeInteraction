using Microsoft.Extensions.Logging;
using Yuexin.Radar.Configuration;

namespace Yuexin.Radar.Bridge.Wpf.Services;

public static class RadarSensorPipelineFactory
{
    public static RadarSensorPipeline Create(
        RadarScreenConfiguration screen,
        RadarSensorConfiguration sensor,
        ILogger<RadarSensorPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(logger);
        return new RadarSensorPipeline(screen, sensor, logger);
    }
}
