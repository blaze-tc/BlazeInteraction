namespace Blaze.Provider.CameraVision;

public sealed class FakeVisualDetector : IVisualDetector
{
    private static readonly IReadOnlyList<VisualDetection> Empty = Array.Empty<VisualDetection>();

    public IReadOnlyList<VisualDetection> Detect(CameraFrame? frame, TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            return Empty;
        }

        var phase = elapsed.TotalSeconds % 2d;
        var x = (float)(phase <= 1d ? phase : 2d - phase);
        return [new VisualDetection(
            "fake-visual-detector",
            "Fake",
            Math.Clamp(x, 0f, 1f),
            .5f,
            1f)];
    }
}
