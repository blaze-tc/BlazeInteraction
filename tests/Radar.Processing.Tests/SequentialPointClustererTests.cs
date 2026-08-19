using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class SequentialPointClustererTests
{
    [Fact]
    public void Cluster_SeparatesLargeGapAndFiltersIsolatedPoint()
    {
        var points = new[]
        {
            Point(0.00f, 1f), Point(0.04f, 1f), Point(0.08f, 1f),
            Point(0.60f, 1f), Point(0.64f, 1f), Point(0.68f, 1f),
            Point(1.5f, 1f)
        };
        var clusterer = new SequentialPointClusterer(new RadarClusteringOptions
        {
            BaseGapMeters = 0.08f,
            DistanceScale = 0.015f,
            MinimumClusterPointCount = 2,
            MaximumClusterWidthMeters = 0.8f
        });

        var clusters = clusterer.Cluster(points);

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, cluster => Assert.Equal(3, cluster.Points.Count));
        Assert.Equal(0.04f, clusters[0].CenterX, 5);
        Assert.Equal(0.64f, clusters[1].CenterX, 5);
    }

    [Fact]
    public void Cluster_DynamicThresholdKeepsFartherPointsTogether()
    {
        var points = new[] { Point(0f, 10f), Point(0.18f, 10f), Point(0.36f, 10f) };
        var clusterer = new SequentialPointClusterer(new RadarClusteringOptions
        {
            BaseGapMeters = 0.05f,
            DistanceScale = 0.02f,
            MinimumClusterPointCount = 2,
            MaximumClusterWidthMeters = 1f
        });

        var clusters = clusterer.Cluster(points);

        Assert.Single(clusters);
    }

    private static RadarPoint Point(float x, float distanceMeters)
    {
        return new RadarPoint((int)(distanceMeters * 100), 0, 0f, x, 0f);
    }
}
