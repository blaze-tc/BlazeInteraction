namespace Yuexin.Radar.Configuration;

/// <summary>Schema 1 DTO used only while reading files that predate Screens.</summary>
internal sealed class LegacyRadarAppConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public RadarDeviceConfiguration Device { get; set; } = new();
    public RadarTransformConfiguration Transform { get; set; } = new();
    public RadarRangeConfiguration Range { get; set; } = new();
    public RadarClusteringConfiguration Clustering { get; set; } = new();
    public LegacyRadarTrackingConfiguration Tracking { get; set; } = new();
    public RadarInteractionConfiguration Interaction { get; set; } = new();
    public RadarIpcConfiguration Ipc { get; set; } = new();
    public RadarCalibrationConfiguration Calibration { get; set; } = new();
}

internal sealed class LegacyRadarTrackingConfiguration
{
    public int ConfirmFrames { get; set; } = 2;
    public int LostFrames { get; set; } = 3;
    public float MaximumAssociationDistanceMeters { get; set; } = 0.35f;
    public float SmoothingAlpha { get; set; } = 0.5f;
}
