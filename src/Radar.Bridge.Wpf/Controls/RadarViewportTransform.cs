using System.Windows;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Controls;

public static class RadarViewportTransform
{
    private const float FitPadding = 1.1f;

    public static Point WorldToScreen(Point2 world, double width, double height, float maximumRangeMeters)
        => WorldToScreen(world, width, height, maximumRangeMeters, new Vector());

    public static Point WorldToScreen(Point2 world, double width, double height, float maximumRangeMeters, Vector panOffset)
    {
        var scale = CalculateScale(width, height, maximumRangeMeters);
        return new Point(
            width / 2d + panOffset.X + world.X * scale,
            height / 2d + panOffset.Y - world.Y * scale);
    }

    public static Point2 ScreenToWorld(Point screen, double width, double height, float maximumRangeMeters)
        => ScreenToWorld(screen, width, height, maximumRangeMeters, new Vector());

    public static Point2 ScreenToWorld(Point screen, double width, double height, float maximumRangeMeters, Vector panOffset)
    {
        var scale = CalculateScale(width, height, maximumRangeMeters);
        return new Point2(
            (float)((screen.X - width / 2d - panOffset.X) / scale),
            (float)((height / 2d + panOffset.Y - screen.Y) / scale));
    }

    public static double CalculateScale(double width, double height, float maximumRangeMeters)
    {
        if (width <= 0d || height <= 0d || maximumRangeMeters <= 0f)
        {
            return 1d;
        }

        return Math.Min(width, height) / (2d * maximumRangeMeters);
    }

    public static float CalculateFittedRange(float configuredRangeMeters, IReadOnlyList<Point2> regionVertices)
    {
        var fittedRange = float.IsFinite(configuredRangeMeters) && configuredRangeMeters > 0f
            ? configuredRangeMeters
            : 0.1f;

        foreach (var vertex in regionVertices ?? [])
        {
            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y)) continue;
            fittedRange = MathF.Max(fittedRange, MathF.Max(MathF.Abs(vertex.X), MathF.Abs(vertex.Y)) * FitPadding);
        }

        return fittedRange;
    }
}
