namespace Yuexin.Radar.Bridge.Wpf.Controls;

internal readonly record struct RadarDisplayBudget(
    int MaximumPointsPerLayer,
    int MaximumTrailLayers)
{
    public static RadarDisplayBudget ForViewport(double width, double height, bool isInteracting)
    {
        var finiteWidth = double.IsFinite(width) ? Math.Max(0d, width) : 0d;
        var finiteHeight = double.IsFinite(height) ? Math.Max(0d, height) : 0d;
        var normalPointBudget = (int)Math.Clamp(
            Math.Ceiling(finiteWidth * finiteHeight / 160d),
            2_000d,
            12_000d);

        if (!isInteracting)
        {
            return new RadarDisplayBudget(normalPointBudget, 6);
        }

        return new RadarDisplayBudget(
            Math.Clamp((normalPointBudget + 2) / 3, 1_000, 4_000),
            2);
    }
}
