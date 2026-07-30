using System.Windows;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarViewportTransformTests
{
    [Fact]
    public void WorldAndScreenConversions_AreInverseWithCenteredOrigin()
    {
        var screen = RadarViewportTransform.WorldToScreen(new Point2(1f, -0.5f), 200, 100, 2f);
        var world = RadarViewportTransform.ScreenToWorld(screen, 200, 100, 2f);

        Assert.Equal(new Point(125, 62.5), screen);
        Assert.Equal(1f, world.X, 5);
        Assert.Equal(-0.5f, world.Y, 5);
    }

    [Fact]
    public void FittedRange_ExpandsTwoMeterPreviewToContainTheWholeRegion()
    {
        var method = typeof(RadarViewportTransform).GetMethod(
            "CalculateFittedRange",
            [typeof(float), typeof(IReadOnlyList<Point2>)]);
        Assert.NotNull(method);
        IReadOnlyList<Point2> region =
        [
            new(-2.5f, 2.5f),
            new(2.5f, 2.5f),
            new(2.5f, -2.5f),
            new(-2.5f, -2.5f)
        ];

        var fitted = Assert.IsType<float>(method.Invoke(null, [2f, region]));

        Assert.Equal(2.75f, fitted, 3);
    }

    [Fact]
    public void PannedWorldAndScreenConversions_RemainInverseForVertexDragging()
    {
        var offset = new Vector(80d, -30d);
        var worldToScreen = typeof(RadarViewportTransform).GetMethod(
            nameof(RadarViewportTransform.WorldToScreen),
            [typeof(Point2), typeof(double), typeof(double), typeof(float), typeof(Vector)]);
        var screenToWorld = typeof(RadarViewportTransform).GetMethod(
            nameof(RadarViewportTransform.ScreenToWorld),
            [typeof(Point), typeof(double), typeof(double), typeof(float), typeof(Vector)]);
        Assert.NotNull(worldToScreen);
        Assert.NotNull(screenToWorld);

        var screen = Assert.IsType<Point>(worldToScreen.Invoke(null, [new Point2(1f, -0.5f), 200d, 100d, 2f, offset]));
        var world = Assert.IsType<Point2>(screenToWorld.Invoke(null, [screen, 200d, 100d, 2f, offset]));

        Assert.Equal(new Point(205d, 32.5d), screen);
        Assert.Equal(1f, world.X, 5);
        Assert.Equal(-0.5f, world.Y, 5);
    }
}
