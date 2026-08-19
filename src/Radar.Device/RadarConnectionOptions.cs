namespace Yuexin.Radar.Device;

public sealed class RadarConnectionOptions
{
    public string RadarIp { get; set; } = "192.168.0.100";
    public int Port { get; set; } = 8487;
    public string LocalIp { get; set; } = string.Empty;
    public bool AutoReconnect { get; set; } = true;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan DataWarningTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan DataDisconnectTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; set; } =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];
}
