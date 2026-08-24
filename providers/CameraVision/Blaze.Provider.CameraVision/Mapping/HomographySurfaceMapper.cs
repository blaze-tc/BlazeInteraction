using Blaze.Interaction.Contracts;
using OpenCvSharp;

namespace Blaze.Provider.CameraVision;

internal sealed class HomographySurfaceMapper : IDisposable
{
    private const float UnitIntervalTolerance = 1e-5f;
    private readonly CameraCalibration _calibration;
    private readonly Mat _homography;
    private int _disposed;

    public HomographySurfaceMapper(CameraCalibration calibration)
    {
        _calibration = calibration ?? throw new ArgumentNullException(nameof(calibration));
        var source = calibration.Points
            .Select(static point => new Point2f(point.X, point.Y))
            .ToArray();
        var destination = new[]
        {
            new Point2f(0f, 0f),
            new Point2f(1f, 0f),
            new Point2f(1f, 1f),
            new Point2f(0f, 1f)
        };

        _homography = Cv2.GetPerspectiveTransform(source, destination);
        if (_homography.Empty() || !IsFinite(_homography))
        {
            _homography.Dispose();
            throw new ArgumentException(
                "Camera calibration did not produce a usable homography.",
                nameof(calibration));
        }
    }

    public bool TryMap(Vector2Data cameraPixel, out Vector2Data surfaceNormalized) =>
        TryMapTrackingPoint(cameraPixel, out surfaceNormalized);

    public bool TryMapTrackingPoint(
        Vector2Data cameraPixel,
        out Vector2Data surfaceNormalized)
    {
        ArgumentNullException.ThrowIfNull(cameraPixel);
        ThrowIfDisposed();
        if (!ContainsInclusive(_calibration.Points, cameraPixel))
        {
            surfaceNormalized = null!;
            return false;
        }

        var mapped = Transform(cameraPixel);
        if (mapped.X < -UnitIntervalTolerance || mapped.X > 1f + UnitIntervalTolerance
            || mapped.Y < -UnitIntervalTolerance || mapped.Y > 1f + UnitIntervalTolerance)
        {
            surfaceNormalized = null!;
            return false;
        }

        surfaceNormalized = new Vector2Data(
            Math.Clamp(mapped.X, 0f, 1f),
            Math.Clamp(mapped.Y, 0f, 1f));
        return true;
    }

    public Vector2Data MapLandmark(Vector2Data cameraPixel)
    {
        ArgumentNullException.ThrowIfNull(cameraPixel);
        ThrowIfDisposed();
        var mapped = Transform(cameraPixel);
        return new Vector2Data(mapped.X, mapped.Y);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _homography.Dispose();
        }
    }

    private Point2f Transform(Vector2Data point)
    {
        var transformed = Cv2.PerspectiveTransform(
            new[] { new Point2f(point.X, point.Y) },
            _homography);
        var result = transformed[0];
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y))
        {
            throw new InvalidDataException("Camera point produced a non-finite homography result.");
        }

        return result;
    }

    private static bool ContainsInclusive(
        IReadOnlyList<Vector2Data> polygon,
        Vector2Data point)
    {
        double? direction = null;
        for (var index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Count];
            var cross = ((double)b.X - a.X) * (point.Y - a.Y)
                        - ((double)b.Y - a.Y) * (point.X - a.X);
            if (Math.Abs(cross) <= UnitIntervalTolerance)
            {
                continue;
            }

            var currentDirection = Math.Sign(cross);
            direction ??= currentDirection;
            if (currentDirection != direction)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFinite(Mat matrix)
    {
        for (var row = 0; row < matrix.Rows; row++)
        {
            for (var column = 0; column < matrix.Cols; column++)
            {
                if (!double.IsFinite(matrix.At<double>(row, column)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
