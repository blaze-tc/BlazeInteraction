using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarPointPersistenceBufferTests
{
    [Fact]
    public void Clear_AllowsFirstFrameFromAnotherSensorToReuseSequence()
    {
        var buffer = new RadarPointPersistenceBuffer(TimeSpan.FromMilliseconds(220), 6);
        var timestamp = DateTimeOffset.UtcNow;
        var first = new RadarPoint(1, 1, 1, 1, 1);
        var second = new RadarPoint(2, 2, 1, 1, 1);

        buffer.Add(10, timestamp, [first]);
        buffer.Clear();
        buffer.Add(10, timestamp, [second]);

        var layer = Assert.Single(buffer.GetLayers(timestamp));
        Assert.Single(layer.Points);
        Assert.Equal(second, layer.Points[0]);
        Assert.Empty(buffer.GetLayers(timestamp.AddMilliseconds(221)));
    }
    [Fact]
    public void GetLayers_KeepsRecentFramesWithNewestFrameFullyOpaque()
    {
        var buffer = new RadarPointPersistenceBuffer(TimeSpan.FromMilliseconds(220), maximumFrames: 6);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        var first = new RadarPoint(100, 10, 1f, 1f, 0f);
        var second = new RadarPoint(110, 11, 1.1f, 1.1f, 0f);

        buffer.Add(sequence: 1, start, [first]);
        buffer.Add(sequence: 2, start.AddMilliseconds(70), [second]);

        var layers = buffer.GetLayers(start.AddMilliseconds(80));

        Assert.Equal(2, layers.Count);
        Assert.Equal(first, Assert.Single(layers[0].Points));
        Assert.InRange(layers[0].Opacity, 0.1d, 0.99d);
        Assert.Equal(second, Assert.Single(layers[1].Points));
        Assert.Equal(1d, layers[1].Opacity);
    }

    [Fact]
    public void GetLayers_RemovesFramesAfterPersistenceWindow()
    {
        var buffer = new RadarPointPersistenceBuffer(TimeSpan.FromMilliseconds(200), maximumFrames: 6);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(2_000);

        buffer.Add(sequence: 1, start, [new RadarPoint(100, 10, 1f, 1f, 0f)]);

        Assert.Empty(buffer.GetLayers(start.AddMilliseconds(201)));
    }

    [Fact]
    public void Add_DoesNotDuplicateSameSnapshotSequence()
    {
        var buffer = new RadarPointPersistenceBuffer(TimeSpan.FromMilliseconds(200), maximumFrames: 6);
        var start = DateTimeOffset.FromUnixTimeMilliseconds(3_000);
        var points = new[] { new RadarPoint(100, 10, 1f, 1f, 0f) };

        buffer.Add(sequence: 8, start, points);
        buffer.Add(sequence: 8, start.AddMilliseconds(10), points);

        Assert.Single(buffer.GetLayers(start.AddMilliseconds(20)));
    }
}
