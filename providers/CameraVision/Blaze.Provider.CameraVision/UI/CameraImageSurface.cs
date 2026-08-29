using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Blaze.Provider.CameraVision;

internal sealed class CameraImageSurface : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Colors.Black));
    private static readonly Brush JointBrush = Frozen(new SolidColorBrush(Color.FromRgb(0, 212, 255)));
    private static readonly Brush CenterBrush = Frozen(new SolidColorBrush(Color.FromRgb(255, 176, 32)));
    private static readonly Pen CalibrationPen = Frozen(new Pen(
        new SolidColorBrush(Color.FromRgb(0, 212, 255)), 2));
    private static readonly Pen OutlinePen = Frozen(new Pen(
        new SolidColorBrush(Color.FromArgb(190, 0, 212, 255)), 1));
    private static readonly Pen BonePen = Frozen(new Pen(
        new SolidColorBrush(Color.FromArgb(220, 0, 212, 255)), 1.5));
    private WriteableBitmap? _bitmap;
    private CameraRawPreviewModel? _rawModel;
    private CameraCalibrationPreviewModel? _calibrationModel;

    internal WriteableBitmap? Bitmap => _bitmap;
    internal int BitmapReplacementCount { get; private set; }

    internal void UpdateFrame(CameraPreviewSnapshot frame, CameraRawPreviewModel model)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(model);
        WriteFrame(frame);
        _rawModel = model;
        _calibrationModel = null;
        InvalidateVisual();
    }

    internal void UpdateFrame(CameraPreviewSnapshot frame, CameraCalibrationPreviewModel model)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(model);
        WriteFrame(frame);
        _calibrationModel = model;
        _rawModel = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        var bitmap = _bitmap;
        if (bitmap is null) return;

        var transform = _rawModel?.Transform ?? _calibrationModel?.Transform;
        if (transform is null) return;
        var content = new Rect(
            transform.Value.OffsetX,
            transform.Value.OffsetY,
            transform.Value.ContentWidth,
            transform.Value.ContentHeight);
        drawingContext.DrawImage(bitmap, content);

        if (_rawModel is not null)
        {
            DrawClosedPath(drawingContext, _rawModel.CalibrationVertices, CalibrationPen);
            foreach (var outline in _rawModel.Outlines)
                DrawClosedPath(drawingContext, outline.Points, OutlinePen);
            foreach (var bone in _rawModel.Bones)
                drawingContext.DrawLine(BonePen, Point(bone.From), Point(bone.To));
            foreach (var joint in _rawModel.Joints)
                drawingContext.DrawEllipse(JointBrush, null, Point(joint.Position), 2.5, 2.5);
            foreach (var tracking in _rawModel.TrackingPoints)
                drawingContext.DrawEllipse(CenterBrush, null, Point(tracking.Position), 5, 5);
        }
        else if (_calibrationModel is not null)
        {
            DrawClosedPath(
                drawingContext,
                _calibrationModel.EffectiveRegionVertices,
                CalibrationPen);
        }
    }

    private void WriteFrame(CameraPreviewSnapshot frame)
    {
        if (_bitmap is null ||
            _bitmap.PixelWidth != frame.Width ||
            _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(
                frame.Width,
                frame.Height,
                96,
                96,
                PixelFormats.Bgr24,
                null);
            BitmapReplacementCount++;
        }

        _bitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Bgr24Buffer,
            frame.StrideBytes,
            0);
    }

    private static void DrawClosedPath(
        DrawingContext drawingContext,
        IReadOnlyList<Blaze.Interaction.Contracts.Vector2Data> points,
        Pen pen)
    {
        if (points.Count < 2) return;
        for (var index = 0; index < points.Count; index++)
        {
            drawingContext.DrawLine(
                pen,
                Point(points[index]),
                Point(points[(index + 1) % points.Count]));
        }
    }

    private static Point Point(Blaze.Interaction.Contracts.Vector2Data point) =>
        new(point.X, point.Y);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
