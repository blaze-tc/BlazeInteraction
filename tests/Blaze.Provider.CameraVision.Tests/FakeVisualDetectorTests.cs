namespace Blaze.Provider.CameraVision.Tests;

public sealed class FakeVisualDetectorTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 0.5)]
    [InlineData(2.0, 0.0)]
    public void Detect_ProducesPredictablePingPongPoint(double seconds, float expectedX)
    {
        var detector = new FakeVisualDetector();

        var detection = Assert.Single(detector.Detect(null, TimeSpan.FromSeconds(seconds)));

        Assert.Equal("fake-visual-detector", detection.DetectorId);
        Assert.Equal("Fake", detection.DetectionType);
        Assert.Equal(expectedX, detection.CameraX, 3);
        Assert.Equal(.5f, detection.CameraY, 3);
        Assert.Equal(1f, detection.Confidence);
    }
}
