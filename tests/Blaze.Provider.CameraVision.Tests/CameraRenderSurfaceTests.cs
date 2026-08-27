using System.Windows.Media;
using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraRenderSurfaceTests
{
    [Fact]
    public void ImageSurface_ReusesBitmapUntilPixelDimensionsChange()
    {
        RunSta(() =>
        {
            var surface = new CameraImageSurface();
            var firstFrame = Frame(2, 2);
            var firstModel = RawModel(firstFrame);
            for (var index = 0; index < 100; index++)
            {
                surface.UpdateFrame(firstFrame, firstModel);
            }
            var firstBitmap = surface.Bitmap;

            Assert.NotNull(firstBitmap);
            Assert.Equal(1, surface.BitmapReplacementCount);

            var resizedFrame = Frame(3, 2);
            var resizedModel = RawModel(resizedFrame);
            surface.UpdateFrame(resizedFrame, resizedModel);
            var resizedBitmap = surface.Bitmap;
            for (var index = 0; index < 100; index++)
            {
                surface.UpdateFrame(resizedFrame, resizedModel);
            }

            Assert.NotSame(firstBitmap, resizedBitmap);
            Assert.Same(resizedBitmap, surface.Bitmap);
            Assert.Equal(2, surface.BitmapReplacementCount);
        });
    }

    [Fact]
    public void PointSurface_UsesCustomDrawingWithoutPerPointVisualChildren()
    {
        RunSta(() =>
        {
            var surface = new CameraPointSurface();
            var interactionPoint = new InteractionPoint
            {
                Id = 1,
                SurfaceId = "main",
                ProviderId = CameraVisionPlugin.ProviderId,
                ProviderInstanceId = "camera-main",
                SourceId = "hand-track-1",
                Phase = InteractionPhase.Hover,
                NormalizedPosition = new Vector2Data(0.5f, 0.5f),
                PixelPosition = new Vector2Data(100, 50),
                Confidence = 1,
                TimestampUnixMs = 1,
                Fp = Enumerable.Range(0, 21)
                    .Select(index => new Vector2Data(index, index))
                    .ToArray()
            };
            var transform = AspectFitTransform.Create(200, 100, 400, 400);
            var model = new CameraUnityPreviewModel(
                1,
                transform,
                [new CameraUnityPointModel(
                    interactionPoint,
                    transform.SourceToViewport(interactionPoint.PixelPosition),
                    interactionPoint.Fp.Select(transform.SourceToViewport).ToArray())]);

            surface.Update(model);

            Assert.Equal(0, VisualTreeHelper.GetChildrenCount(surface));
            Assert.Same(model, surface.Model);
        });
    }

    private static CameraPreviewSnapshot Frame(int width, int height) =>
        new(width, height, width * 3, new byte[width * height * 3]);

    private static CameraRawPreviewModel RawModel(CameraPreviewSnapshot frame) => new(
        1,
        frame,
        AspectFitTransform.Create(frame.Width, frame.Height, 200, 100),
        Array.Empty<Vector2Data>(),
        Array.Empty<CameraOverlayJoint>(),
        Array.Empty<CameraOverlayTrackingPoint>(),
        Array.Empty<CameraOverlayOutline>());

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }
}
