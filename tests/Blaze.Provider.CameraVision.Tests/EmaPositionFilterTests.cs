using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class EmaPositionFilterTests
{
    [Fact]
    public void Update_ReturnsFirstSampleUnchanged()
    {
        var filter = new EmaPositionFilter(0.35f);
        var sample = new Vector2Data(0.2f, 0.8f);

        Assert.Equal(sample, filter.Update(1, sample));
    }

    [Fact]
    public void Update_AppliesConfiguredFactorToPreviousPosition()
    {
        var filter = new EmaPositionFilter(0.35f);
        filter.Update(1, new Vector2Data(0.2f, 0.4f));

        var result = filter.Update(1, new Vector2Data(0.6f, 0.8f));

        AssertClose(0.34f, result.X);
        AssertClose(0.54f, result.Y);
    }

    [Fact]
    public void Update_KeepsTrackHistoriesIndependent()
    {
        var filter = new EmaPositionFilter(0.5f);
        filter.Update(1, new Vector2Data(0f, 0f));
        filter.Update(2, new Vector2Data(1f, 1f));

        Assert.Equal(new Vector2Data(0.5f, 0.5f),
            filter.Update(1, new Vector2Data(1f, 1f)));
        Assert.Equal(new Vector2Data(0.5f, 0.5f),
            filter.Update(2, new Vector2Data(0f, 0f)));
    }

    [Fact]
    public void Remove_MakesReentryStartWithCurrentSample()
    {
        var filter = new EmaPositionFilter(0.35f);
        filter.Update(7, new Vector2Data(0f, 0f));

        Assert.True(filter.Remove(7));
        Assert.False(filter.Remove(7));
        Assert.Equal(new Vector2Data(1f, 1f),
            filter.Update(7, new Vector2Data(1f, 1f)));
    }

    [Fact]
    public void Reset_ClearsEveryTrackHistory()
    {
        var filter = new EmaPositionFilter(0.35f);
        filter.Update(1, new Vector2Data(0f, 0f));
        filter.Update(2, new Vector2Data(1f, 1f));

        filter.Reset();

        Assert.Equal(new Vector2Data(1f, 1f),
            filter.Update(1, new Vector2Data(1f, 1f)));
        Assert.Equal(new Vector2Data(0f, 0f),
            filter.Update(2, new Vector2Data(0f, 0f)));
    }

    [Theory]
    [InlineData(0f, 0.2f)]
    [InlineData(1f, 0.8f)]
    public void BoundaryFactors_BehaveExactly(float factor, float expected)
    {
        var filter = new EmaPositionFilter(factor);
        filter.Update(1, new Vector2Data(0.2f, 0.2f));

        var result = filter.Update(1, new Vector2Data(0.8f, 0.8f));

        Assert.Equal(new Vector2Data(expected, expected), result);
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Constructor_RejectsNonFiniteOrOutOfRangeFactor(float factor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmaPositionFilter(factor));
    }

    [Fact]
    public void Update_RejectsNonPositiveTrackId()
    {
        var filter = new EmaPositionFilter(0.35f);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            filter.Update(0, new Vector2Data(0.5f, 0.5f)));
    }

    private static void AssertClose(float expected, float actual) =>
        Assert.InRange(actual, expected - 0.00001f, expected + 0.00001f);
}
