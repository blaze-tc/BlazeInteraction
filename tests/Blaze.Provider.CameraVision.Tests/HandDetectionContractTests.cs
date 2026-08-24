namespace Blaze.Provider.CameraVision.Tests;

public sealed class HandDetectionContractTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public void Options_AllowsAnyPositiveMaxHands(int maxHands)
    {
        var options = Options(maxHands);

        Assert.Equal(maxHands, options.MaxHands);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Options_RejectsNonPositiveMaxHands(int maxHands)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Options(maxHands));
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Options_RejectsInvalidConfidence(float confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HandDetectionOptions("model.task", 8, confidence, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new HandDetectionOptions("model.task", 8, 0.5f, confidence));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(22)]
    public void DetectedHand_RequiresExactlyTwentyOneLandmarks(int count)
    {
        Assert.Throws<ArgumentException>(() =>
            new DetectedHand(0.8f, Enumerable.Repeat(ValidLandmark(), count)));
    }

    [Theory]
    [InlineData(float.NaN, 0.2f, 0.3f)]
    [InlineData(0.1f, float.NegativeInfinity, 0.3f)]
    [InlineData(0.1f, 0.2f, float.PositiveInfinity)]
    public void Landmark_RejectsNonFiniteCoordinates(float x, float y, float z)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HandLandmark(x, y, z));
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void DetectedHand_RejectsInvalidConfidence(float confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DetectedHand(confidence, ValidLandmarks()));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    public void Result_AllowsMoreThanTwoHandsWithoutBusinessCap(int handCount)
    {
        var result = new HandDetectionResult(
            Enumerable.Range(0, handCount).Select(_ => ValidHand()));

        Assert.Equal(handCount, result.Hands.Count);
    }

    [Fact]
    public void HandAndResult_SnapshotMutableInputCollections()
    {
        var landmarkSource = ValidLandmarks().ToArray();
        var hand = new DetectedHand(0.8f, landmarkSource);
        landmarkSource[0] = new HandLandmark(0.9f, 0.9f, 0.9f);

        var handSource = new[] { hand };
        var result = new HandDetectionResult(handSource);
        handSource[0] = ValidHand(0.1f);

        Assert.Equal(ValidLandmark(), hand.Landmarks[0]);
        Assert.Equal(0.8f, result.Hands[0].Confidence);
        Assert.IsAssignableFrom<IReadOnlyList<HandLandmark>>(hand.Landmarks);
        Assert.IsAssignableFrom<IReadOnlyList<DetectedHand>>(result.Hands);
    }

    [Fact]
    public void RgbFrameView_ValidatesPointerDimensionsAndRowCapacity()
    {
        var frame = new RgbFrameView((nint)1, width: 4, height: 3, strideBytes: 16);

        Assert.Equal((nint)1, frame.Data);
        Assert.Equal(4, frame.Width);
        Assert.Equal(3, frame.Height);
        Assert.Equal(16, frame.StrideBytes);
        Assert.Throws<ArgumentException>(() => new RgbFrameView(0, 4, 3, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbFrameView(1, 0, 3, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbFrameView(1, 4, 0, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbFrameView(1, 4, 3, 11));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RgbFrameView(1, int.MaxValue, 1, int.MaxValue));
    }

    private static HandDetectionOptions Options(int maxHands) =>
        new("model.task", maxHands, 0.5f, 0.5f);

    private static DetectedHand ValidHand(float confidence = 0.8f) =>
        new(confidence, ValidLandmarks());

    private static IEnumerable<HandLandmark> ValidLandmarks() =>
        Enumerable.Range(0, 21).Select(_ => ValidLandmark());

    private static HandLandmark ValidLandmark() => new(0.1f, 0.2f, -0.3f);
}
