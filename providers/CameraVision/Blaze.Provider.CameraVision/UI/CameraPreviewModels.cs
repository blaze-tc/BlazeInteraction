using System.Runtime.InteropServices;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using OpenCvSharp;

namespace Blaze.Provider.CameraVision;

internal sealed record CameraRawPreviewModel(
    long TimestampUnixMs,
    CameraPreviewSnapshot Preview,
    AspectFitTransform Transform,
    IReadOnlyList<Vector2Data> CalibrationVertices,
    IReadOnlyList<CameraOverlayJoint> Joints,
    IReadOnlyList<CameraOverlayTrackingPoint> TrackingPoints,
    IReadOnlyList<CameraOverlayOutline> Outlines);

internal sealed record CameraCalibrationPreviewModel(
    long TimestampUnixMs,
    CameraPreviewSnapshot Preview,
    AspectFitTransform Transform,
    IReadOnlyList<Vector2Data> EffectiveRegionVertices);

internal sealed record CameraUnityPointModel(
    InteractionPoint Source,
    Vector2Data Center,
    IReadOnlyList<Vector2Data> Fp);

internal sealed record CameraUnityPreviewModel(
    long TimestampUnixMs,
    AspectFitTransform Transform,
    IReadOnlyList<CameraUnityPointModel> Points);

internal sealed record CameraPreviewModelSet(
    CameraRawPreviewModel Raw,
    CameraCalibrationPreviewModel Calibration,
    CameraUnityPreviewModel Unity);

internal static class CameraPreviewModelBuilder
{
    internal static CameraPreviewModelSet Build(
        CameraVisionStatusSnapshot snapshot,
        InteractionSurface surface,
        double rawWidth,
        double rawHeight,
        double calibrationWidth,
        double calibrationHeight,
        double unityWidth,
        double unityHeight)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(surface);
        var preview = snapshot.Preview
            ?? throw new InvalidOperationException("Camera preview is not available.");
        var rawTransform = AspectFitTransform.Create(
            preview.Width, preview.Height, rawWidth, rawHeight);
        var calibration = CalibrationVertices(snapshot, preview);
        var rawVertices = calibration
            .Select(rawTransform.SourceToViewport)
            .ToArray();
        var overlay = CameraPreviewOverlayBuilder.Build(
            snapshot,
            (float)rawTransform.ContentWidth,
            (float)rawTransform.ContentHeight);
        var joints = overlay.Joints
            .Select(joint => joint with
            {
                Position = new Vector2Data(
                    joint.Position.X + (float)rawTransform.OffsetX,
                    joint.Position.Y + (float)rawTransform.OffsetY)
            })
            .ToArray();
        var tracking = overlay.TrackingPoints
            .Select(point => point with
            {
                Position = new Vector2Data(
                    point.Position.X + (float)rawTransform.OffsetX,
                    point.Position.Y + (float)rawTransform.OffsetY)
            })
            .ToArray();
        var outlines = overlay.Outlines
            .Select(outline => outline with
            {
                Points = outline.Points.Select(point => new Vector2Data(
                    point.X + (float)rawTransform.OffsetX,
                    point.Y + (float)rawTransform.OffsetY)).ToArray()
            })
            .ToArray();
        var raw = new CameraRawPreviewModel(
            snapshot.TimestampUnixMs,
            preview,
            rawTransform,
            rawVertices,
            joints,
            tracking,
            outlines);

        var warped = Warp(preview, calibration);
        var calibrationTransform = AspectFitTransform.Create(
            warped.Width, warped.Height, calibrationWidth, calibrationHeight);
        var effectiveRegion = new[]
        {
            calibrationTransform.SourceToViewport(new Vector2Data(0, 0)),
            calibrationTransform.SourceToViewport(new Vector2Data(warped.Width, 0)),
            calibrationTransform.SourceToViewport(new Vector2Data(warped.Width, warped.Height)),
            calibrationTransform.SourceToViewport(new Vector2Data(0, warped.Height))
        };
        var corrected = new CameraCalibrationPreviewModel(
            snapshot.TimestampUnixMs,
            warped,
            calibrationTransform,
            effectiveRegion);

        var unityTransform = AspectFitTransform.Create(
            surface.LogicalWidth,
            surface.LogicalHeight,
            unityWidth,
            unityHeight);
        var unityPoints = snapshot.OutputPoints
            .Select(point => new CameraUnityPointModel(
                point,
                unityTransform.SourceToViewport(point.PixelPosition),
                point.Fp.Select(unityTransform.SourceToViewport).ToArray()))
            .ToArray();
        var unity = new CameraUnityPreviewModel(
            snapshot.TimestampUnixMs,
            unityTransform,
            unityPoints);

        return new CameraPreviewModelSet(raw, corrected, unity);
    }

    private static IReadOnlyList<Vector2Data> CalibrationVertices(
        CameraVisionStatusSnapshot snapshot,
        CameraPreviewSnapshot preview) =>
        snapshot.CalibrationPoints.Count == 4
            ? snapshot.CalibrationPoints
            :
            [
                new Vector2Data(0, 0),
                new Vector2Data(preview.Width, 0),
                new Vector2Data(preview.Width, preview.Height),
                new Vector2Data(0, preview.Height)
            ];

    private static CameraPreviewSnapshot Warp(
        CameraPreviewSnapshot preview,
        IReadOnlyList<Vector2Data> calibration)
    {
        var sourceBytes = preview.Bgr24.ToArray();
        using var source = new Mat(preview.Height, preview.Width, MatType.CV_8UC3);
        Marshal.Copy(sourceBytes, 0, source.Data, sourceBytes.Length);
        using var destination = new Mat();
        Point2f[] sourcePoints = calibration
            .Select(point => new Point2f(point.X, point.Y))
            .ToArray();
        Point2f[] destinationPoints =
        [
            new(0, 0),
            new(preview.Width - 1, 0),
            new(preview.Width - 1, preview.Height - 1),
            new(0, preview.Height - 1)
        ];
        using var transform = Cv2.GetPerspectiveTransform(sourcePoints, destinationPoints);
        Cv2.WarpPerspective(
            source,
            destination,
            transform,
            new OpenCvSharp.Size(preview.Width, preview.Height));
        var output = new byte[checked(preview.StrideBytes * preview.Height)];
        Marshal.Copy(destination.Data, output, 0, output.Length);
        return new CameraPreviewSnapshot(
            preview.Width,
            preview.Height,
            preview.StrideBytes,
            output);
    }
}
