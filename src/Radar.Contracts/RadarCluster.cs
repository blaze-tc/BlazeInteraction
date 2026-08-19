namespace Yuexin.Radar.Contracts;

public sealed record RadarCluster(
    int ClusterIndex,
    IReadOnlyList<RadarPoint> Points,
    float CenterX,
    float CenterY,
    float WidthMeters,
    float EstimatedDistanceMeters);
