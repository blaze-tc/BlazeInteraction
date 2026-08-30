using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Processing.Tests;

public sealed class HomographyCalibrationTests
{
    [Fact]
    public void Create_MapsFourCornersAndCenterExactly()
    {
        var corners = new[]
        {
            new Point2(-1f, 1f),
            new Point2(1f, 1f),
            new Point2(1f, -1f),
            new Point2(-1f, -1f)
        };

        var created = HomographyCalibration.TryCreate(corners, out var calibration, out var error);

        Assert.True(created, error);
        Assert.NotNull(calibration);
        Assert.True(calibration.IsValid);
        AssertMap(calibration, corners[0], 0f, 1f);
        AssertMap(calibration, corners[1], 1f, 1f);
        AssertMap(calibration, corners[2], 1f, 0f);
        AssertMap(calibration, corners[3], 0f, 0f);
        AssertMap(calibration, new Point2(0f, 0f), 0.5f, 0.5f);
        Assert.InRange(calibration.MaximumCornerError, 0d, 1e-6);
    }

    [Fact]
    public void Create_MapsTiltedQuadrilateralDiagonalIntersectionToCenter()
    {
        var corners = new[]
        {
            new Point2(-1f, 1f),
            new Point2(2f, 1f),
            new Point2(3f, -1f),
            new Point2(-2f, -1f)
        };

        var created = HomographyCalibration.TryCreate(corners, out var calibration, out var error);

        Assert.True(created, error);
        AssertMap(calibration!, new Point2(0.5f, 0.25f), 0.5f, 0.5f);
    }

    [Theory]
    [MemberData(nameof(InvalidCorners))]
    public void Create_RejectsDegenerateOrMisorderedCorners(Point2[] corners)
    {
        var created = HomographyCalibration.TryCreate(corners, out var calibration, out var error);

        Assert.False(created);
        Assert.Null(calibration);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    public static IEnumerable<object[]> InvalidCorners()
    {
        yield return [new[] { new Point2(0, 0), new Point2(1, 0), new Point2(2, 0), new Point2(3, 0) }];
        yield return [new[] { new Point2(0, 1), new Point2(1, 0), new Point2(1, 1), new Point2(0, 0) }];
        yield return [new[] { new Point2(0, 1), new Point2(0, 1), new Point2(1, 0), new Point2(0, 0) }];
    }

    private static void AssertMap(HomographyCalibration calibration, Point2 input, float expectedX, float expectedY)
    {
        Assert.True(calibration.TryMap(input.X, input.Y, out var actualX, out var actualY));
        Assert.Equal(expectedX, actualX, 5);
        Assert.Equal(expectedY, actualY, 5);
    }
}
