using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class AspectFitTransformTests
{
    [Fact]
    public void Create_WideSourceInSquareViewport_LetterboxesVertically()
    {
        var fit = AspectFitTransform.Create(1920, 1080, 1000, 1000);

        Assert.Equal(1000, fit.ContentWidth, 3);
        Assert.Equal(562.5, fit.ContentHeight, 3);
        Assert.Equal(0, fit.OffsetX, 3);
        Assert.Equal(218.75, fit.OffsetY, 3);
        Assert.False(fit.TryViewportToSource(new Vector2Data(500, 100), out _));
        Assert.True(fit.TryViewportToSource(new Vector2Data(500, 500), out var source));
        Assert.Equal(960, source.X, 2);
        Assert.Equal(540, source.Y, 2);
    }

    [Fact]
    public void SourceToViewport_AndReverseMapping_AreConsistent()
    {
        var fit = AspectFitTransform.Create(640, 480, 1600, 900);
        var viewport = fit.SourceToViewport(new Vector2Data(320, 240));

        Assert.Equal(800, viewport.X, 3);
        Assert.Equal(450, viewport.Y, 3);
        Assert.True(fit.TryViewportToSource(viewport, out var source));
        Assert.Equal(320, source.X, 3);
        Assert.Equal(240, source.Y, 3);
    }

    [Theory]
    [InlineData(0, 1080, 1000, 1000)]
    [InlineData(1920, 1080, double.NaN, 1000)]
    [InlineData(1920, 1080, 1000, -1)]
    public void Create_RejectsInvalidDimensions(
        double sourceWidth,
        double sourceHeight,
        double viewportWidth,
        double viewportHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AspectFitTransform.Create(
                sourceWidth,
                sourceHeight,
                viewportWidth,
                viewportHeight));
    }
}
