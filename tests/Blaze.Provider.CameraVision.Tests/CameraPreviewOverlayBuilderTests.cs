using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraPreviewOverlayBuilderTests
{
    [Fact]
    public void WorkspaceBuilderProducesOnlyRawAndUnityModelsWithoutWarpedPreview()
    {
        var surface = new InteractionSurface
        {
            SurfaceId = "main",
            Name = "Main",
            LogicalWidth = 1920,
            LogicalHeight = 1080,
            IsPrimary = true,
            Order = 0
        };

        var workspace = CameraPreviewModelBuilder.BuildWorkspace(
            Status([Hand(1, 0.25f)]), surface,
            800d, 450d, 800d, 250d);

        var properties = workspace.GetType().GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.Equal(new[] { "Raw", "Unity" }, properties);
        Assert.Equal(21, workspace.Raw.Joints.Count);
        Assert.Equal(21, workspace.Raw.Bones.Count);
        Assert.All(workspace.Raw.Bones, bone =>
        {
            Assert.InRange(bone.From.X, 0, 800);
            Assert.InRange(bone.From.Y, 0, 450);
            Assert.InRange(bone.To.X, 0, 800);
            Assert.InRange(bone.To.Y, 0, 450);
        });
    }

    [Fact]
    public void PreviewModelsShareTimestampAndUnityUsesExactOutputPointsWithoutBones()
    {
        var hands = new[] { Hand(1, 0.25f), Hand(2, 0.75f) };
        var outputPoints = hands.Select(hand => OutputPoint(hand.TrackId, hand.NormalizedPosition))
            .ToArray();
        var status = Status(
            hands,
            timestampUnixMs: 1234,
            outputPoints: outputPoints,
            calibrationPoints:
            [
                new Vector2Data(0, 0),
                new Vector2Data(100, 0),
                new Vector2Data(100, 50),
                new Vector2Data(0, 50)
            ]);
        var surface = new InteractionSurface
        {
            SurfaceId = "main",
            Name = "Main",
            LogicalWidth = 1920,
            LogicalHeight = 1080,
            IsPrimary = true,
            Order = 0
        };

        var models = CameraPreviewModelBuilder.Build(
            status,
            surface,
            rawWidth: 200,
            rawHeight: 200,
            calibrationWidth: 200,
            calibrationHeight: 100,
            unityWidth: 400,
            unityHeight: 400);

        Assert.Equal(1234, models.Raw.TimestampUnixMs);
        Assert.Equal(1234, models.Calibration.TimestampUnixMs);
        Assert.Equal(1234, models.Unity.TimestampUnixMs);
        Assert.Equal(50, models.Raw.Transform.OffsetY, 3);
        Assert.Equal(new Vector2Data(0, 50), models.Raw.CalibrationVertices[0]);
        Assert.Equal(100, models.Calibration.Preview.Width);
        Assert.Equal(50, models.Calibration.Preview.Height);
        Assert.Equal(2, models.Unity.Points.Count);
        Assert.All(models.Unity.Points, point => Assert.Equal(21, point.Fp.Count));
        Assert.Equal(outputPoints[0].PixelPosition, models.Unity.Points[0].Source.PixelPosition);
        Assert.Equal(outputPoints[0].Fp, models.Unity.Points[0].Source.Fp);
        Assert.DoesNotContain(
            typeof(CameraUnityPreviewModel).GetProperties(),
            property => property.Name.Contains("Bone", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryHandProducesTwentyOneJointsTwentyOneBonesTrackingPointAndOutline()
    {
        var overlay = CameraPreviewOverlayBuilder.Build(Status([Hand(1, 0.25f)]), 200, 100);

        Assert.Equal(21, overlay.Joints.Count);
        Assert.Equal(21, overlay.Bones.Count);
        Assert.Single(overlay.TrackingPoints);
        Assert.Single(overlay.Outlines);
    }

    [Fact]
    public void EightHandsAreRetainedAndTrackColorsAreDeterministic()
    {
        var hands = Enumerable.Range(1, 8).Select(index => Hand(index, index / 10f)).ToArray();

        var first = CameraPreviewOverlayBuilder.Build(Status(hands), 100, 100);
        var second = CameraPreviewOverlayBuilder.Build(Status(hands.Reverse()), 100, 100);

        Assert.Equal(8 * 21, first.Joints.Count);
        Assert.Equal(8 * 21, first.Bones.Count);
        Assert.Equal(
            first.Joints.GroupBy(joint => joint.TrackId).ToDictionary(group => group.Key, group => group.First().Color),
            second.Joints.GroupBy(joint => joint.TrackId).ToDictionary(group => group.Key, group => group.First().Color));
    }

    [Fact]
    public void GeometryScalesFromCameraPixelsToViewport()
    {
        var overlay = CameraPreviewOverlayBuilder.Build(Status([Hand(1, 0.5f)]), 200, 200);
        var joint = overlay.Joints.Single(item => item.LandmarkIndex == 0);

        Assert.Equal(100f, joint.Position.X, 3);
        Assert.Equal(100f, joint.Position.Y, 3);
    }

    [Fact]
    public void MalformedHandExtensionsNeverCrashAndProduceNoGeometry()
    {
        var point = new InteractionPoint
        {
            Id = 1,
            SurfaceId = "main",
            ProviderId = CameraVisionPlugin.ProviderId,
            ProviderInstanceId = "camera-main",
            SourceId = "hand-track-1",
            Phase = InteractionPhase.Hover,
            NormalizedPosition = new Vector2Data(0.5f, 0.5f),
            PixelPosition = new Vector2Data(50, 50),
            Confidence = 1,
            TimestampUnixMs = 1,
            Extensions = new InteractionExtensions(new Dictionary<string, JsonElement>
            {
                ["hand"] = JsonSerializer.SerializeToElement(new { schemaVersion = 99 })
            })
        };

        var overlay = CameraPreviewOverlayBuilder.BuildFromInteractionPoints([point], 100, 100);

        Assert.Empty(overlay.Joints);
        Assert.Empty(overlay.Bones);
    }

    private static CameraVisionStatusSnapshot Status(
        IEnumerable<CameraHandSnapshot> hands,
        long timestampUnixMs = 0,
        IEnumerable<InteractionPoint>? outputPoints = null,
        IEnumerable<Vector2Data>? calibrationPoints = null) => new(
        ProviderRuntimeStatus.Running,
        CameraCaptureStatus.Connected,
        30,
        30,
        30,
        4,
        hands.Count(),
        hands.Select(hand => hand.NormalizedPosition),
        0,
        true,
        new CameraPreviewSnapshot(100, 50, 300, new byte[15000]),
        hands,
        null,
        timestampUnixMs,
        100,
        50,
        outputPoints,
        calibrationPoints);

    private static InteractionPoint OutputPoint(long trackId, Vector2Data normalized)
    {
        var center = new Vector2Data(normalized.X * 1920, normalized.Y * 1080);
        return new InteractionPoint
        {
            Id = trackId,
            SurfaceId = "main",
            ProviderId = CameraVisionPlugin.ProviderId,
            ProviderInstanceId = "camera-main",
            SourceId = $"hand-track-{trackId}",
            Phase = InteractionPhase.Hover,
            NormalizedPosition = normalized,
            PixelPosition = center,
            Confidence = 0.9f,
            TimestampUnixMs = 1234,
            Fp = Enumerable.Range(0, 21)
                .Select(index => new Vector2Data(center.X + index, center.Y + index))
                .ToArray()
        };
    }

    private static CameraHandSnapshot Hand(long trackId, float x)
    {
        var normalized = new Vector2Data(x, 0.5f);
        return new CameraHandSnapshot(
            trackId,
            0.9f,
            new Vector2Data(x * 100, 25),
            normalized,
            Enumerable.Range(0, 21).Select(index => new CameraMappedLandmark(
                index,
                new Vector2Data(x * 100 + (index % 4), 25 + (index / 4)),
                normalized,
                -index / 100f)));
    }
}
