using OpenCvSharp;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraFrameTests
{
    [Theory]
    [InlineData(CameraRotation.Rotate0, 3, 2, 1, 6)]
    [InlineData(CameraRotation.Rotate90, 2, 3, 4, 3)]
    [InlineData(CameraRotation.Rotate180, 3, 2, 6, 1)]
    [InlineData(CameraRotation.Rotate270, 2, 3, 3, 4)]
    public void Transform_AppliesSupportedRotation(
        CameraRotation rotation,
        int expectedWidth,
        int expectedHeight,
        byte expectedTopLeft,
        byte expectedBottomRight)
    {
        using var source = Frame();

        using var transformed = CameraFrameTransformer.Transform(source, mirrorX: false, rotation);

        Assert.Equal(expectedWidth, transformed.Width);
        Assert.Equal(expectedHeight, transformed.Height);
        Assert.Equal(expectedTopLeft, transformed.Image.At<byte>(0, 0));
        Assert.Equal(expectedBottomRight,
            transformed.Image.At<byte>(transformed.Height - 1, transformed.Width - 1));
    }

    [Fact]
    public void Transform_MirrorXFlipsColumnsBeforeRotation()
    {
        using var source = Frame();

        using var transformed = CameraFrameTransformer.Transform(
            source,
            mirrorX: true,
            CameraRotation.Rotate0);

        Assert.Equal(3, transformed.Image.At<byte>(0, 0));
        Assert.Equal(1, transformed.Image.At<byte>(0, 2));
        Assert.Equal(6, transformed.Image.At<byte>(1, 0));
        Assert.Equal(4, transformed.Image.At<byte>(1, 2));
    }

    private static CameraFrame Frame()
    {
        var image = new Mat(2, 3, MatType.CV_8UC1);
        byte value = 1;
        for (var row = 0; row < image.Rows; row++)
        {
            for (var column = 0; column < image.Cols; column++)
            {
                image.Set(row, column, value++);
            }
        }

        return new CameraFrame(7, DateTimeOffset.FromUnixTimeMilliseconds(1234), image);
    }
}
