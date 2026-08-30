using OpenCvSharp;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class MediaPipeNativeSmokeTests
{
    [Fact]
    [Trait("Category", "MediaPipeNativeSmoke")]
    public async Task MediaPipeNativeSmoke_TracksHandAcrossFrameEdgeWithoutInvalidCropCoordinates()
    {
        var imagePath = RepositoryPath(
            "tests",
            "Blaze.Provider.CameraVision.Tests",
            "TestAssets",
            "pointing_up.jpg");
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "hand_landmarker.task");
        using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
        Assert.False(source.Empty(), "Attributed hand test image could not be decoded.");
        using var scaled = new Mat();
        Cv2.Resize(source, scaled, new Size(571, 600));

        await using var backend = new MediaPipeHandBackend();
        await backend.InitializeAsync(
            new HandDetectionOptions(modelPath, 8, 0.5f, 0.5f),
            CancellationToken.None);

        var detectedFrames = 0;
        var offsets = Enumerable.Range(0, 40)
            .Select(index => 354 - index * 18)
            .Concat(Enumerable.Range(0, 40).Select(index => -348 + index * 18))
            .ToArray();
        for (var frameIndex = 0; frameIndex < offsets.Length; frameIndex++)
        {
            using var bgr = new Mat(new Size(1280, 720), MatType.CV_8UC3, Scalar.All(0));
            PasteClipped(scaled, bgr, offsets[frameIndex], 60);
            using var rgb = new Mat();
            Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);

            var result = await backend.DetectAsync(
                new RgbFrameView(
                    rgb.Data,
                    rgb.Width,
                    rgb.Height,
                    checked((int)rgb.Step())),
                1000 + frameIndex * 33L,
                CancellationToken.None);
            detectedFrames += result.Hands.Count > 0 ? 1 : 0;
        }

        Assert.True(detectedFrames > 0, "The attributed hand was never detected during the edge sequence.");
    }

    [Fact]
    [Trait("Category", "MediaPipeNativeSmoke")]
    public async Task MediaPipeNativeSmoke_ProcessesCameraSizedNonSquareFrame()
    {
        var imagePath = RepositoryPath(
            "tests",
            "Blaze.Provider.CameraVision.Tests",
            "TestAssets",
            "pointing_up.jpg");
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "hand_landmarker.task");
        Assert.True(File.Exists(imagePath), $"Attributed hand test image was not found: {imagePath}");
        Assert.True(File.Exists(modelPath), $"Hand landmarker model was not copied: {modelPath}");

        using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
        Assert.False(source.Empty(), "Attributed hand test image could not be decoded.");
        using var bgr = new Mat();
        Cv2.Resize(source, bgr, new Size(1280, 720));
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);

        await using var backend = new MediaPipeHandBackend();
        await backend.InitializeAsync(
            new HandDetectionOptions(modelPath, 8, 0.5f, 0.5f),
            CancellationToken.None);
        var frame = new RgbFrameView(
            rgb.Data,
            rgb.Width,
            rgb.Height,
            checked((int)rgb.Step()));

        for (var frameIndex = 0; frameIndex < 60; frameIndex++)
        {
            var result = await backend.DetectAsync(
                frame,
                1000 + frameIndex * 33L,
                CancellationToken.None);
            Assert.All(result.Hands, hand => Assert.Equal(21, hand.Landmarks.Count));
        }
    }

    [Fact]
    [Trait("Category", "MediaPipeNativeSmoke")]
    public async Task MediaPipeNativeSmoke_DetectsAttributedHandImage()
    {
        var imagePath = RepositoryPath(
            "tests",
            "Blaze.Provider.CameraVision.Tests",
            "TestAssets",
            "pointing_up.jpg");
        var modelPath = Path.Combine(AppContext.BaseDirectory, "models", "hand_landmarker.task");
        Assert.True(File.Exists(imagePath), $"Attributed hand test image was not found: {imagePath}");
        Assert.True(File.Exists(modelPath), $"Hand landmarker model was not copied: {modelPath}");

        using var bgr = Cv2.ImRead(imagePath, ImreadModes.Color);
        Assert.False(bgr.Empty(), "Attributed hand test image could not be decoded.");
        using var rgb = new Mat();
        Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);

        await using var backend = new MediaPipeHandBackend();
        await backend.InitializeAsync(
            new HandDetectionOptions(modelPath, 8, 0.5f, 0.5f),
            CancellationToken.None);
        var frame = new RgbFrameView(
            rgb.Data,
            rgb.Width,
            rgb.Height,
            checked((int)rgb.Step()));

        var result = await backend.DetectAsync(frame, 1000, CancellationToken.None);

        Assert.NotEmpty(result.Hands);
        Assert.All(result.Hands, hand =>
        {
            Assert.Equal(21, hand.Landmarks.Count);
            Assert.All(hand.Landmarks, landmark =>
            {
                Assert.True(float.IsFinite(landmark.X));
                Assert.True(float.IsFinite(landmark.Y));
                Assert.True(float.IsFinite(landmark.Z));
            });
        });
    }

    private static string RepositoryPath(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BlazeInteraction.sln")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        return Path.Combine([current!.FullName, .. segments]);
    }

    private static void PasteClipped(Mat source, Mat destination, int x, int y)
    {
        var left = Math.Max(0, x);
        var top = Math.Max(0, y);
        var right = Math.Min(destination.Width, x + source.Width);
        var bottom = Math.Min(destination.Height, y + source.Height);
        if (right <= left || bottom <= top)
        {
            return;
        }

        using var sourceRegion = new Mat(
            source,
            new Rect(left - x, top - y, right - left, bottom - top));
        using var destinationRegion = new Mat(
            destination,
            new Rect(left, top, right - left, bottom - top));
        sourceRegion.CopyTo(destinationRegion);
    }
}
