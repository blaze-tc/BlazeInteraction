namespace Yuexin.Radar.Processing;

public sealed class HomographyCalibration : ICalibrationMapper
{
    private const double Epsilon = 1e-10;
    private static readonly Point2[] NormalizedCorners =
    [
        new(0f, 1f),
        new(1f, 1f),
        new(1f, 0f),
        new(0f, 0f)
    ];

    private readonly double[] _matrix;

    private HomographyCalibration(double[] matrix, double maximumCornerError)
    {
        _matrix = matrix;
        MaximumCornerError = maximumCornerError;
    }

    public bool IsValid => true;
    public IReadOnlyList<double> Matrix => _matrix.ToArray();
    public double MaximumCornerError { get; }

    public static bool TryCreate(
        IReadOnlyList<Point2> physicalCorners,
        out HomographyCalibration? calibration,
        out string? error)
    {
        calibration = null;
        error = null;
        if (physicalCorners is null || physicalCorners.Count != 4)
        {
            error = "Exactly four physical corners are required in TL, TR, BR, BL order.";
            return false;
        }

        if (!IsStrictConvexQuadrilateral(physicalCorners))
        {
            error = "Calibration corners must be unique and form a non-self-intersecting convex quadrilateral.";
            return false;
        }

        var augmented = new double[8, 9];
        for (var index = 0; index < 4; index++)
        {
            var source = physicalCorners[index];
            var target = NormalizedCorners[index];
            var row = index * 2;

            augmented[row, 0] = source.X;
            augmented[row, 1] = source.Y;
            augmented[row, 2] = 1d;
            augmented[row, 6] = -target.X * source.X;
            augmented[row, 7] = -target.X * source.Y;
            augmented[row, 8] = target.X;

            augmented[row + 1, 3] = source.X;
            augmented[row + 1, 4] = source.Y;
            augmented[row + 1, 5] = 1d;
            augmented[row + 1, 6] = -target.Y * source.X;
            augmented[row + 1, 7] = -target.Y * source.Y;
            augmented[row + 1, 8] = target.Y;
        }

        if (!TrySolve(augmented, out var solution))
        {
            error = "Calibration homography is singular.";
            return false;
        }

        var matrix = new[]
        {
            solution[0], solution[1], solution[2],
            solution[3], solution[4], solution[5],
            solution[6], solution[7], 1d
        };
        var candidate = new HomographyCalibration(matrix, 0d);
        var maximumError = 0d;
        for (var index = 0; index < 4; index++)
        {
            if (!candidate.TryMap(
                    physicalCorners[index].X,
                    physicalCorners[index].Y,
                    out var mappedX,
                    out var mappedY))
            {
                error = "Calibration produced an invalid projective denominator.";
                return false;
            }

            var x = mappedX - NormalizedCorners[index].X;
            var y = mappedY - NormalizedCorners[index].Y;
            maximumError = Math.Max(maximumError, Math.Sqrt(x * x + y * y));
        }

        calibration = new HomographyCalibration(matrix, maximumError);
        return true;
    }

    public bool TryMap(
        float physicalX,
        float physicalY,
        out float normalizedX,
        out float normalizedY)
    {
        var denominator = _matrix[6] * physicalX + _matrix[7] * physicalY + _matrix[8];
        if (Math.Abs(denominator) < Epsilon)
        {
            normalizedX = 0f;
            normalizedY = 0f;
            return false;
        }

        normalizedX = (float)((_matrix[0] * physicalX + _matrix[1] * physicalY + _matrix[2]) / denominator);
        normalizedY = (float)((_matrix[3] * physicalX + _matrix[4] * physicalY + _matrix[5]) / denominator);
        return float.IsFinite(normalizedX) && float.IsFinite(normalizedY);
    }

    private static bool IsStrictConvexQuadrilateral(IReadOnlyList<Point2> corners)
    {
        float? sign = null;
        for (var index = 0; index < corners.Count; index++)
        {
            var a = corners[index];
            var b = corners[(index + 1) % corners.Count];
            var c = corners[(index + 2) % corners.Count];
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (MathF.Abs(cross) < 1e-6f)
            {
                return false;
            }

            var currentSign = MathF.Sign(cross);
            if (sign.HasValue && currentSign != sign.Value)
            {
                return false;
            }

            sign = currentSign;
        }

        return true;
    }

    private static bool TrySolve(double[,] matrix, out double[] solution)
    {
        const int size = 8;
        for (var pivot = 0; pivot < size; pivot++)
        {
            var bestRow = pivot;
            var bestValue = Math.Abs(matrix[pivot, pivot]);
            for (var row = pivot + 1; row < size; row++)
            {
                var value = Math.Abs(matrix[row, pivot]);
                if (value > bestValue)
                {
                    bestValue = value;
                    bestRow = row;
                }
            }

            if (bestValue < Epsilon)
            {
                solution = [];
                return false;
            }

            if (bestRow != pivot)
            {
                for (var column = pivot; column <= size; column++)
                {
                    (matrix[pivot, column], matrix[bestRow, column]) =
                        (matrix[bestRow, column], matrix[pivot, column]);
                }
            }

            var pivotValue = matrix[pivot, pivot];
            for (var column = pivot; column <= size; column++)
            {
                matrix[pivot, column] /= pivotValue;
            }

            for (var row = 0; row < size; row++)
            {
                if (row == pivot)
                {
                    continue;
                }

                var factor = matrix[row, pivot];
                for (var column = pivot; column <= size; column++)
                {
                    matrix[row, column] -= factor * matrix[pivot, column];
                }
            }
        }

        solution = new double[size];
        for (var row = 0; row < size; row++)
        {
            solution[row] = matrix[row, size];
        }

        return true;
    }
}
