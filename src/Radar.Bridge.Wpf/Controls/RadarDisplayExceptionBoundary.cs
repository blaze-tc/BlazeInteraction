using System.Diagnostics;

namespace Yuexin.Radar.Bridge.Wpf.Controls;

internal readonly record struct RadarDisplayMetrics(
    string SensorId,
    int InputPointCount,
    int DisplayedPointCount,
    int TrailLayerCount,
    long RenderedCount,
    long CoalescedCount);

internal sealed class RadarDisplayExceptionBoundary(Action<Exception, RadarDisplayMetrics> report)
{
    private readonly Action<Exception, RadarDisplayMetrics> _report = report ?? throw new ArgumentNullException(nameof(report));

    public bool TryRender(Action render, RadarDisplayMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(render);
        try
        {
            render();
            return true;
        }
        catch (Exception exception)
        {
            _report(exception, metrics);
            return false;
        }
    }
}

internal static class RadarDisplayDiagnostics
{
    public static Action<Exception, RadarDisplayMetrics>? Reporter { get; set; }

    public static void Report(Exception exception, RadarDisplayMetrics metrics)
    {
        var reporter = Reporter;
        if (reporter is not null)
        {
            reporter(exception, metrics);
            return;
        }

        Trace.TraceError(
            "Radar display render failed sensor={0} inputPoints={1} displayedPoints={2} trailLayers={3} rendered={4} coalesced={5} exception={6}",
            metrics.SensorId,
            metrics.InputPointCount,
            metrics.DisplayedPointCount,
            metrics.TrailLayerCount,
            metrics.RenderedCount,
            metrics.CoalescedCount,
            exception);
    }
}
