using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraPreviewOverlayBuilderTests
{
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

    private static CameraVisionStatusSnapshot Status(IEnumerable<CameraHandSnapshot> hands) => new(
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
        null);

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
