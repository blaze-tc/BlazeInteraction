using Blaze.Interaction.Contracts;
using System.Text.Json.Serialization;

namespace Blaze.Provider.CameraVision;

internal sealed class CameraCalibration
{
    private const double DegenerateTolerance = 1e-6;

    [JsonConstructor]
    public CameraCalibration(
        Vector2Data p1,
        Vector2Data p2,
        Vector2Data p3,
        Vector2Data p4)
    {
        ArgumentNullException.ThrowIfNull(p1);
        ArgumentNullException.ThrowIfNull(p2);
        ArgumentNullException.ThrowIfNull(p3);
        ArgumentNullException.ThrowIfNull(p4);

        var points = new[] { p1, p2, p3, p4 };
        ValidateConvexQuadrilateral(points);

        P1 = p1;
        P2 = p2;
        P3 = p3;
        P4 = p4;
        Points = Array.AsReadOnly(points);
    }

    public Vector2Data P1 { get; }
    public Vector2Data P2 { get; }
    public Vector2Data P3 { get; }
    public Vector2Data P4 { get; }
    [JsonIgnore]
    public IReadOnlyList<Vector2Data> Points { get; }

    private static void ValidateConvexQuadrilateral(IReadOnlyList<Vector2Data> points)
    {
        double? direction = null;
        for (var index = 0; index < points.Count; index++)
        {
            var a = points[index];
            var b = points[(index + 1) % points.Count];
            var c = points[(index + 2) % points.Count];
            var cross = ((double)b.X - a.X) * (c.Y - b.Y)
                        - ((double)b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cross) <= DegenerateTolerance)
            {
                throw new ArgumentException(
                    "Camera calibration points must form a non-degenerate quadrilateral.");
            }

            var currentDirection = Math.Sign(cross);
            direction ??= currentDirection;
            if (currentDirection != direction)
            {
                throw new ArgumentException(
                    "Camera calibration points must form a convex quadrilateral in perimeter order.");
            }
        }
    }
}
