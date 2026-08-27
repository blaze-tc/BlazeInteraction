using System.Windows;
using System.Windows.Media;

namespace Blaze.Provider.CameraVision;

internal sealed class CameraPointSurface : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Colors.Black));
    private static readonly Brush CenterBrush = Frozen(new SolidColorBrush(Color.FromRgb(0, 212, 255)));
    private static readonly Brush FootprintBrush = Frozen(new SolidColorBrush(Color.FromRgb(255, 176, 32)));

    internal CameraUnityPreviewModel? Model { get; private set; }

    internal void Update(CameraUnityPreviewModel model)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (Model is null) return;
        foreach (var point in Model.Points)
        {
            drawingContext.DrawEllipse(
                CenterBrush,
                null,
                new Point(point.Center.X, point.Center.Y),
                5,
                5);
            foreach (var footprintPoint in point.Fp)
            {
                drawingContext.DrawEllipse(
                    FootprintBrush,
                    null,
                    new Point(footprintPoint.X, footprintPoint.Y),
                    2.5,
                    2.5);
            }
        }
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
