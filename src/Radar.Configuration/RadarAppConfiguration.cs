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

    /// <summary>Controls whether this in-memory configuration may replace its source file.</summary>
    [JsonIgnore]
    public RadarConfigurationPersistenceState PersistenceState { get; internal set; } = RadarConfigurationPersistenceState.Writable;

    [JsonIgnore]
    public bool CanPersist => PersistenceState == RadarConfigurationPersistenceState.Writable;

    /// <summary>Copies non-serialized load diagnostics when a configuration is staged in memory.</summary>
    public void PreservePersistenceDiagnosticsFrom(RadarAppConfiguration source)
    {
        ArgumentNullException.ThrowIfNull(source);
        PersistenceState = source.PersistenceState;
        LoadWarnings.Clear();
        LoadWarnings.AddRange(source.LoadWarnings);
    }

    public static RadarAppConfiguration CreateDefault() => new();
}

public enum RadarConfigurationPersistenceState
{
    Writable = 0,
    RejectedLoad = 1
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
    public List<RadarPoint2> ActivePolygon { get; set; } = [new(-2.5f, 2.5f), new(2.5f, 2.5f), new(2.5f, -2.5f), new(-2.5f, -2.5f)];
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
