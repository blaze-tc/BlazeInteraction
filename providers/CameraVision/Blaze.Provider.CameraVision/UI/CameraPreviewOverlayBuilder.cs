using System.Text.Json;
using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision;

internal sealed record CameraOverlayJoint(long TrackId, int LandmarkIndex, Vector2Data Position, string Color);
internal sealed record CameraOverlayBone(long TrackId, int FromIndex, int ToIndex, Vector2Data From, Vector2Data To, string Color);
internal sealed record CameraOverlayTrackingPoint(long TrackId, Vector2Data Position, string Color);
internal sealed record CameraOverlayOutline(long TrackId, IReadOnlyList<Vector2Data> Points, string Color);
internal sealed record CameraPreviewOverlay(
    IReadOnlyList<CameraOverlayJoint> Joints,
    IReadOnlyList<CameraOverlayBone> Bones,
    IReadOnlyList<CameraOverlayTrackingPoint> TrackingPoints,
    IReadOnlyList<CameraOverlayOutline> Outlines);

internal static class CameraPreviewOverlayBuilder
{
    internal static readonly IReadOnlyList<(int From, int To)> Connections = Array.AsReadOnly(
    [
        (0, 1), (1, 2), (2, 3), (3, 4),
        (0, 5), (5, 6), (6, 7), (7, 8),
        (5, 9), (9, 10), (10, 11), (11, 12),
        (9, 13), (13, 14), (14, 15), (15, 16),
        (13, 17), (0, 17), (17, 18), (18, 19), (19, 20)
    ]);

    internal static CameraPreviewOverlay Build(
        CameraVisionStatusSnapshot snapshot,
        float viewportWidth,
        float viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Preview is null || viewportWidth <= 0 || viewportHeight <= 0)
            return Empty();
        var joints = new List<CameraOverlayJoint>();
        var bones = new List<CameraOverlayBone>();
        var tracking = new List<CameraOverlayTrackingPoint>();
        var outlines = new List<CameraOverlayOutline>();
        foreach (var hand in snapshot.Hands)
        {
            var color = ColorFor(hand.TrackId);
            var positions = hand.Landmarks
                .OrderBy(item => item.Index)
                .Select(item => Scale(item.CameraPixelPosition, snapshot.Preview.Width,
                    snapshot.Preview.Height, viewportWidth, viewportHeight))
                .ToArray();
            if (positions.Length != DetectedHand.LandmarkCount) continue;
            joints.AddRange(positions.Select((position, index) =>
                new CameraOverlayJoint(hand.TrackId, index, position, color)));
            bones.AddRange(Connections.Select(connection => new CameraOverlayBone(
                hand.TrackId, connection.From, connection.To,
                positions[connection.From], positions[connection.To], color)));
            tracking.Add(new CameraOverlayTrackingPoint(
                hand.TrackId,
                Scale(hand.CameraTrackingPoint, snapshot.Preview.Width, snapshot.Preview.Height,
                    viewportWidth, viewportHeight),
                color));
            outlines.Add(new CameraOverlayOutline(hand.TrackId, ConvexHull(positions), color));
        }
        return new CameraPreviewOverlay(
            Array.AsReadOnly(joints.ToArray()), Array.AsReadOnly(bones.ToArray()),
            Array.AsReadOnly(tracking.ToArray()), Array.AsReadOnly(outlines.ToArray()));
    }

    internal static CameraPreviewOverlay BuildFromInteractionPoints(
        IEnumerable<InteractionPoint> points,
        float viewportWidth,
        float viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(points);
        foreach (var point in points)
        {
            try
            {
                if (point.Extensions is null || !point.Extensions.TryGetValue("hand", out var hand) ||
                    hand.ValueKind != JsonValueKind.Object ||
                    !hand.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
                    continue;
            }
            catch
            {
            }
        }
        return Empty();
    }

    private static CameraPreviewOverlay Empty() => new(
        Array.Empty<CameraOverlayJoint>(), Array.Empty<CameraOverlayBone>(),
        Array.Empty<CameraOverlayTrackingPoint>(), Array.Empty<CameraOverlayOutline>());

    private static Vector2Data Scale(Vector2Data point, float sourceWidth, float sourceHeight,
        float viewportWidth, float viewportHeight) =>
        new(point.X / sourceWidth * viewportWidth, point.Y / sourceHeight * viewportHeight);

    private static string ColorFor(long trackId)
    {
        string[] colors = ["#00D4FF", "#FFB020", "#7B61FF", "#24C875", "#FF5C8A", "#F05D23", "#3A86FF", "#B8DE6F"];
        var index = (int)((ulong)trackId % (ulong)colors.Length);
        return colors[index];
    }

    private static IReadOnlyList<Vector2Data> ConvexHull(IReadOnlyList<Vector2Data> points)
    {
        var sorted = points.Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        if (sorted.Length <= 2) return Array.AsReadOnly(sorted);
        var hull = new List<Vector2Data>();
        foreach (var point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        var lowerCount = hull.Count;
        for (var index = sorted.Length - 2; index >= 0; index--)
        {
            var point = sorted[index];
            while (hull.Count > lowerCount && Cross(hull[^2], hull[^1], point) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        hull.RemoveAt(hull.Count - 1);
        return Array.AsReadOnly(hull.ToArray());
    }

    private static double Cross(Vector2Data origin, Vector2Data a, Vector2Data b) =>
        ((double)a.X - origin.X) * (b.Y - origin.Y) -
        ((double)a.Y - origin.Y) * (b.X - origin.X);
}
