using System.Text.Json.Serialization;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Configuration;

public enum RadarResolutionMode
{
    FollowUnityDefault = 0,
    Override = 1
}

public enum RadarSensorSourceMode
{
    Real = 0,
    Simulation = 1,
    Replay = 2
}

public readonly record struct RadarPixelRect(int X, int Y, int Width, int Height);

public sealed class RadarFusionConfiguration
{
    public int OutputRateHz { get; set; } = 30;
    public int SensorDataMaxAgeMilliseconds { get; set; } = 150;
    public float FusionDistancePixels { get; set; } = 80f;
}

public sealed class RadarScreenTrackingConfiguration : RadarTrackingConfiguration
{
    public float MaximumAssociationDistancePixels { get; set; } = 160f;

    internal static RadarScreenTrackingConfiguration FromLegacy(LegacyRadarTrackingConfiguration legacy, ICollection<string>? warnings = null)
    {
        warnings?.Add("Legacy maximumAssociationDistanceMeters has no reliable pixel equivalent before screen calibration; using 160 pixels.");
        return new RadarScreenTrackingConfiguration
        {
            ConfirmFrames = legacy.ConfirmFrames,
            LostFrames = legacy.LostFrames,
            SmoothingAlpha = legacy.SmoothingAlpha,
            MaximumAssociationDistanceMeters = legacy.MaximumAssociationDistanceMeters,
            MaximumAssociationDistancePixels = 160f
        };
    }
}

public sealed class RadarSensorConfiguration
{
    public string SensorId { get; set; } = "sensor-1";
    public string DisplayName { get; set; } = "Radar 1";
    public bool Enabled { get; set; } = true;
    public RadarSensorSourceMode SourceMode { get; set; } = RadarSensorSourceMode.Real;
    public RadarDeviceConfiguration Device { get; set; } = new();
    public RadarTransformConfiguration Transform { get; set; } = new();
    public RadarRangeConfiguration Range { get; set; } = new();
    public RadarClusteringConfiguration Clustering { get; set; } = new();
    public RadarCalibrationConfiguration Calibration { get; set; } = new();
    public RadarPixelRect OutputRectPixels { get; set; } = new(0, 0, 1920, 1080);
    public string ReplayFilePath { get; set; } = string.Empty;
    public double ReplaySpeed { get; set; } = 1d;
    public bool ReplayLoop { get; set; }

    internal RadarSensorConfiguration DeepClone(string sensorId) => new()
    {
        SensorId = sensorId,
        DisplayName = DisplayName,
        Enabled = Enabled,
        SourceMode = SourceMode,
        Device = new RadarDeviceConfiguration
        {
            DeviceModel = Device.DeviceModel, RadarIp = Device.RadarIp, Port = Device.Port,
            LocalIp = Device.LocalIp, AutoReconnect = Device.AutoReconnect
        },
        Transform = new RadarTransformConfiguration
        {
            RotationDegrees = Transform.RotationDegrees, FlipX = Transform.FlipX, FlipY = Transform.FlipY,
            OffsetXMeters = Transform.OffsetXMeters, OffsetYMeters = Transform.OffsetYMeters
        },
        Range = new RadarRangeConfiguration
        {
            MinimumDistanceMeters = Range.MinimumDistanceMeters, MaximumDistanceMeters = Range.MaximumDistanceMeters,
            VisualizationRangeMeters = Range.VisualizationRangeMeters, MinimumAngleDegrees = Range.MinimumAngleDegrees,
            MaximumAngleDegrees = Range.MaximumAngleDegrees,
            ActivePolygon = Range.ActivePolygon.ToList(),
            MaskedPolygons = Range.MaskedPolygons.Select(polygon => polygon.ToList()).ToList(),
            EdgeDeadZones = new RadarEdgeDeadZoneConfiguration
            {
                LeftMeters = Range.EdgeDeadZones.LeftMeters, RightMeters = Range.EdgeDeadZones.RightMeters,
                TopMeters = Range.EdgeDeadZones.TopMeters, BottomMeters = Range.EdgeDeadZones.BottomMeters
            }
        },
        Clustering = new RadarClusteringConfiguration
        {
            BaseGapMeters = Clustering.BaseGapMeters, DistanceScale = Clustering.DistanceScale,
            MinimumClusterPointCount = Clustering.MinimumClusterPointCount,
            MaximumClusterWidthMeters = Clustering.MaximumClusterWidthMeters
        },
        Calibration = new RadarCalibrationConfiguration
        {
            IsValid = Calibration.IsValid, DeviceModel = Calibration.DeviceModel,
            PhysicalCorners = Calibration.PhysicalCorners.ToList(), HomographyMatrix = Calibration.HomographyMatrix.ToList(),
            CreatedAt = Calibration.CreatedAt,
            TransformSnapshot = new RadarTransformConfiguration
            {
                RotationDegrees = Calibration.TransformSnapshot.RotationDegrees,
                FlipX = Calibration.TransformSnapshot.FlipX, FlipY = Calibration.TransformSnapshot.FlipY,
                OffsetXMeters = Calibration.TransformSnapshot.OffsetXMeters,
                OffsetYMeters = Calibration.TransformSnapshot.OffsetYMeters
            },
            MaximumCornerError = Calibration.MaximumCornerError
        },
        OutputRectPixels = OutputRectPixels,
        ReplayFilePath = ReplayFilePath,
        ReplaySpeed = ReplaySpeed,
        ReplayLoop = ReplayLoop
    };
}

public sealed class RadarScreenConfiguration
{
    public string ScreenId { get; set; } = "main";
    public string UnityDisplayName { get; set; } = "Main";
    public bool IsAssociated { get; set; }
    public bool IsPrimary { get; set; } = true;
    public int UnityOrder { get; set; }
    public int UnityDefaultWidthPixels { get; set; } = 1920;
    public int UnityDefaultHeightPixels { get; set; } = 1080;
    public RadarResolutionMode ResolutionMode { get; set; }
    public int WidthPixels { get; set; } = 1920;
    public int HeightPixels { get; set; } = 1080;
    public RadarFusionConfiguration Fusion { get; set; } = new();
    public RadarScreenTrackingConfiguration Tracking { get; set; } = new();
    public RadarInteractionConfiguration Interaction { get; set; } = new();
    public List<RadarSensorConfiguration> Sensors { get; set; } = [new()];

    [JsonIgnore]
    public int EffectiveWidthPixels => ResolutionMode == RadarResolutionMode.Override ? WidthPixels : UnityDefaultWidthPixels;

    [JsonIgnore]
    public int EffectiveHeightPixels => ResolutionMode == RadarResolutionMode.Override ? HeightPixels : UnityDefaultHeightPixels;

    public RadarScreenConfiguration CloneWithId(string screenId)
    {
        var usedIds = new HashSet<string>(Sensors.Select(sensor => sensor.SensorId), StringComparer.OrdinalIgnoreCase);
        var copiedSensors = Sensors.Select(sensor => sensor.DeepClone(CreateCopiedSensorId(sensor.SensorId, usedIds))).ToList();
        return new RadarScreenConfiguration
        {
            ScreenId = screenId,
            UnityDisplayName = UnityDisplayName,
            IsAssociated = IsAssociated,
            IsPrimary = IsPrimary,
            UnityOrder = UnityOrder,
            UnityDefaultWidthPixels = UnityDefaultWidthPixels,
            UnityDefaultHeightPixels = UnityDefaultHeightPixels,
            ResolutionMode = ResolutionMode,
            WidthPixels = WidthPixels,
            HeightPixels = HeightPixels,
            Fusion = new RadarFusionConfiguration
            {
                OutputRateHz = Fusion.OutputRateHz, SensorDataMaxAgeMilliseconds = Fusion.SensorDataMaxAgeMilliseconds,
                FusionDistancePixels = Fusion.FusionDistancePixels
            },
            Tracking = new RadarScreenTrackingConfiguration
            {
                ConfirmFrames = Tracking.ConfirmFrames, LostFrames = Tracking.LostFrames,
                MaximumAssociationDistanceMeters = Tracking.MaximumAssociationDistanceMeters,
                MaximumAssociationDistancePixels = Tracking.MaximumAssociationDistancePixels,
                SmoothingAlpha = Tracking.SmoothingAlpha
            },
            Interaction = new RadarInteractionConfiguration
            {
                Mode = Interaction.Mode, DwellMilliseconds = Interaction.DwellMilliseconds,
                DwellRadiusNormalized = Interaction.DwellRadiusNormalized,
                DragThresholdNormalized = Interaction.DragThresholdNormalized,
                MinimumPressMilliseconds = Interaction.MinimumPressMilliseconds,
                MaximumClickMovementNormalized = Interaction.MaximumClickMovementNormalized
            },
            Sensors = copiedSensors
        };
    }

    private static string CreateCopiedSensorId(string sourceId, ISet<string> usedIds)
    {
        var root = string.IsNullOrWhiteSpace(sourceId) ? "sensor" : sourceId;
        for (var suffix = 1; ; suffix++)
        {
            var candidate = suffix == 1 ? $"{root}-copy" : $"{root}-copy-{suffix}";
            if (usedIds.Add(candidate)) return candidate;
        }
    }
}
