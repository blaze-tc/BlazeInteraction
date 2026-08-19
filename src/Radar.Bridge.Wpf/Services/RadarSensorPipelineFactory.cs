using Microsoft.Extensions.Logging;
using Yuexin.Radar.Configuration;

namespace Yuexin.Radar.Bridge.Wpf.Services;

public interface IRadarSensorPipelineFactory
{
    IRadarSensorPipeline Create(
        RadarScreenConfiguration screen,
        RadarSensorConfiguration sensor);
}

public sealed class RadarSensorPipelineFactory : IRadarSensorPipelineFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public RadarSensorPipelineFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    public IRadarSensorPipeline Create(
        RadarScreenConfiguration screen,
        RadarSensorConfiguration sensor)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(sensor);
        return new RadarSensorPipeline(screen, sensor, _loggerFactory.CreateLogger<RadarSensorPipeline>());
    }
}
