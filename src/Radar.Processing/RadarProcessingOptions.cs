namespace Yuexin.Radar.Processing;

public sealed class RadarTransformOptions
{
    public float RotationDegrees { get; set; }
    public bool FlipX { get; set; }
    public bool FlipY { get; set; }
    public float OffsetXMeters { get; set; }
    public float OffsetYMeters { get; set; }
}

public sealed class RadarFilterOptions
{
    public float MinimumDistanceMeters { get; set; } = 0.1f;
    public float MaximumDistanceMeters { get; set; } = 5f;
    public float BlindZoneStartDegrees { get; set; } = 230f;
    public float BlindZoneEndDegrees { get; set; } = 310f;
    public float MinimumAngleDegrees { get; set; }
    public float MaximumAngleDegrees { get; set; } = 360f;
    public IReadOnlyList<Point2> ActivePolygon { get; set; } = [];
    public IReadOnlyList<IReadOnlyList<Point2>> MaskedPolygons { get; set; } = [];
    public float LeftEdgeDeadZoneMeters { get; set; }
    public float RightEdgeDeadZoneMeters { get; set; }
    public float TopEdgeDeadZoneMeters { get; set; }
    public float BottomEdgeDeadZoneMeters { get; set; }
}

public sealed class RadarClusteringOptions
{
    public float BaseGapMeters { get; set; } = 0.08f;
    public float DistanceScale { get; set; } = 0.015f;
    public int MinimumClusterPointCount { get; set; } = 2;
    public float MaximumClusterWidthMeters { get; set; } = 0.8f;
}

public sealed class RadarTrackingOptions
{
    public int ConfirmFrames { get; set; } = 2;
    public int LostFrames { get; set; } = 3;
    public float MaximumAssociationDistanceMeters { get; set; } = 0.35f;
    public float SmoothingAlpha { get; set; } = 0.5f;
}
