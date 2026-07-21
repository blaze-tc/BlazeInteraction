using System.Text.Json.Serialization;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Configuration;

public sealed class RadarAppConfiguration
{
    public int SchemaVersion { get; set; } = 2;
    public RadarIpcConfiguration Ipc { get; set; } = new();
    public List<RadarScreenConfiguration> Screens { get; set; } = [new()];

    /// <summary>Non-persisted diagnostics recorded while loading or migrating a configuration file.</summary>
    [JsonIgnore]
    public List<string> LoadWarnings { get; } = [];

    [Obsolete("Temporary build bridge; use Screens.")]
    [JsonIgnore]
    public RadarDeviceConfiguration Device { get => MainSensor.Device; set => MainSensor.Device = value ?? new(); }

    [Obsolete("Temporary build bridge; use Screens.")]
    [JsonIgnore]
    public RadarTransformConfiguration Transform { get => MainSensor.Transform; set => MainSensor.Transform = value ?? new(); }

    [Obsolete("Temporary build bridge; use Screens.")]
    [JsonIgnore]
    public RadarRangeConfiguration Range { get => MainSensor.Range; set => MainSensor.Range = value ?? new(); }

    [Obsolete("Temporary build bridge; use Screens.")]
    [JsonIgnore]
    public RadarClusteringConfiguration Clustering { get => MainSensor.Clustering; set => MainSensor.Clustering = value ?? new(); }

    [Obsolete("Temporary build bridge; use Screens.")]
    [JsonIgnore]
    public RadarScreenTrackingConfiguration Tracking { get => MainScreen.Tracking; set => MainScreen.Tracking = value ?? new(); }

    [Obsolete("Temporary build bridge; use Screens.")]
    [JsonIgnore]
    public RadarInteractionConfiguration Interaction { get => MainScreen.Interaction; set => MainScreen.Interaction = value ?? new(); }

    [Obsolete("Temporary build bridge; use Screens.")]
    [JsonIgnore]
    public RadarCalibrationConfiguration Calibration { get => MainSensor.Calibration; set => MainSensor.Calibration = value ?? new(); }

    public static RadarAppConfiguration CreateDefault() => new();

    private RadarScreenConfiguration MainScreen
    {
        get
        {
            Screens ??= [];
            var screen = Screens.FirstOrDefault(candidate => string.Equals(candidate.ScreenId, "main", StringComparison.OrdinalIgnoreCase));
            if (screen is not null) return screen;
            screen = new RadarScreenConfiguration();
            Screens.Insert(0, screen);
            return screen;
        }
    }

    private RadarSensorConfiguration MainSensor
    {
        get
        {
            var screen = MainScreen;
            screen.Sensors ??= [];
            if (screen.Sensors.Count > 0) return screen.Sensors[0];
            var sensor = new RadarSensorConfiguration();
            screen.Sensors.Add(sensor);
            return sensor;
        }
    }
}

public sealed class RadarDeviceConfiguration
{
    public RadarModel DeviceModel { get; set; } = RadarModel.F10;
    public string RadarIp { get; set; } = "192.168.0.100";
    public int Port { get; set; } = 8487;
    public string LocalIp { get; set; } = string.Empty;
    public bool AutoReconnect { get; set; } = true;
}

public sealed class RadarTransformConfiguration
{
    public float RotationDegrees { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public float OffsetXMeters { get; set; }
    public float OffsetYMeters { get; set; }
}

public sealed class RadarRangeConfiguration
{
    public float MinimumDistanceMeters { get; set; } = 0.1f;
    public float MaximumDistanceMeters { get; set; } = 5f;
    public float VisualizationRangeMeters { get; set; } = 4f;
    public float MinimumAngleDegrees { get; set; }
    public float MaximumAngleDegrees { get; set; } = 360f;
    public List<RadarPoint2> ActivePolygon { get; set; } = [];
    public List<List<RadarPoint2>> MaskedPolygons { get; set; } = [];
    public RadarEdgeDeadZoneConfiguration EdgeDeadZones { get; set; } = new();
}

public sealed class RadarEdgeDeadZoneConfiguration
{
    public float LeftMeters { get; set; }
    public float RightMeters { get; set; }
    public float TopMeters { get; set; }
    public float BottomMeters { get; set; }
}

public readonly record struct RadarPoint2(float X, float Y);

public sealed class RadarClusteringConfiguration
{
    public float BaseGapMeters { get; set; } = 0.08f;
    public float DistanceScale { get; set; } = 0.015f;
    public int MinimumClusterPointCount { get; set; } = 2;
    public float MaximumClusterWidthMeters { get; set; } = 0.8f;
}

public class RadarTrackingConfiguration
{
    public int ConfirmFrames { get; set; } = 2;
    public int LostFrames { get; set; } = 3;

    [JsonIgnore]
    public float MaximumAssociationDistanceMeters { get; set; } = 0.35f;

    public float SmoothingAlpha { get; set; } = 0.5f;
}

public sealed class RadarInteractionConfiguration
{
    public RadarInteractionMode Mode { get; set; } = RadarInteractionMode.Touch;
    public int DwellMilliseconds { get; set; } = 800;
    public float DwellRadiusNormalized { get; set; } = 0.03f;
    public float DragThresholdNormalized { get; set; } = 0.015f;
    public int MinimumPressMilliseconds { get; set; } = 30;
    public float MaximumClickMovementNormalized { get; set; } = 0.03f;
}

public sealed class RadarIpcConfiguration
{
    public string PipeName { get; set; } = "Yuexin.RadarBridge";
    public bool SendRawPoints { get; set; }
    public bool SendClusters { get; set; }
}

public sealed class RadarCalibrationConfiguration
{
    public bool IsValid { get; set; }
    public RadarModel DeviceModel { get; set; } = RadarModel.F10;
    public List<RadarPoint2> PhysicalCorners { get; set; } = [];
    public List<double> HomographyMatrix { get; set; } = [];
    public DateTimeOffset? CreatedAt { get; set; }
    public RadarTransformConfiguration TransformSnapshot { get; set; } = new();
    public double MaximumCornerError { get; set; }
}
