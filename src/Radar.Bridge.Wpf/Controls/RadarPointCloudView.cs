using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Controls;

public sealed class RegionVertexMovedEventArgs(int index, Point2 worldPosition) : EventArgs
{
    public int Index { get; } = index;
    public Point2 WorldPosition { get; } = worldPosition;
}

/// <summary>Stable physical-space view for exactly one selected radar sensor.</summary>
public sealed class RadarPointCloudView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(8, 15, 25));
    private static readonly Brush GridBrush = Frozen(Color.FromArgb(72, 67, 91, 116));
    private static readonly Brush RawPointBrush = Frozen(Color.FromArgb(210, 119, 159, 187));
    private static readonly Brush ValidPointBrush = Frozen(Color.FromRgb(56, 211, 214));
    private static readonly Brush RegionBrush = Frozen(Color.FromRgb(126, 231, 135));
    private static readonly Brush MaskBrush = Frozen(Color.FromArgb(48, 245, 93, 91));
    private static readonly Brush ClusterBrush = Frozen(Color.FromRgb(192, 132, 252));
    private static readonly Brush MaskBorderBrush = Frozen(Color.FromArgb(190, 245, 93, 91));
    private static readonly Pen GridPen = FrozenPen(GridBrush, 1d);
    private static readonly Pen ClusterPen = FrozenPen(ClusterBrush, 1.2d);
    private static readonly Pen RegionPen = FrozenPen(RegionBrush, 1.5d, DashStyles.Dash);
    private static readonly Pen RegionVertexPen = FrozenPen(RegionBrush, 2d);
    private static readonly Pen MaskPen = FrozenPen(MaskBorderBrush, 1.2d);
    private static readonly TimeSpan PointPersistenceDuration = TimeSpan.FromMilliseconds(220);
    private readonly RadarPointPersistenceBuffer _rawPointFrames = new(PointPersistenceDuration, 6);
    private readonly RadarPointPersistenceBuffer _validPointFrames = new(PointPersistenceDuration, 6);
    private readonly DispatcherTimer _persistenceTimer;
    private string? _snapshotSensorId;
    private int _draggedVertex = -1;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panOrigin;

    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(RadarSensorRuntimeSnapshot), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(null, OnSnapshotChanged));
    public static readonly DependencyProperty MaximumRangeMetersProperty = DependencyProperty.Register(
        nameof(MaximumRangeMeters), typeof(float), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(5f, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RegionVerticesProperty = DependencyProperty.Register(
        nameof(RegionVertices), typeof(IReadOnlyList<Point2>), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(null, OnRegionVerticesChanged));
    public static readonly DependencyProperty MaskedRegionsProperty = DependencyProperty.Register(
        nameof(MaskedRegions), typeof(IReadOnlyList<IReadOnlyList<Point2>>), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowRawPointsProperty = DependencyProperty.Register(
        nameof(ShowRawPoints), typeof(bool), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowValidPointsProperty = DependencyProperty.Register(
        nameof(ShowValidPoints), typeof(bool), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowClustersProperty = DependencyProperty.Register(
        nameof(ShowClusters), typeof(bool), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowFilterOverlayProperty = DependencyProperty.Register(
        nameof(ShowFilterOverlay), typeof(bool), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowBlindZoneProperty = DependencyProperty.Register(
        nameof(ShowBlindZone), typeof(bool), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsRegionEditableProperty = DependencyProperty.Register(
        nameof(IsRegionEditable), typeof(bool), typeof(RadarPointCloudView), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IsPanEnabledProperty = DependencyProperty.Register(
        nameof(IsPanEnabled), typeof(bool), typeof(RadarPointCloudView), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty PanOffsetProperty = DependencyProperty.Register(
        nameof(PanOffset), typeof(Vector), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(new Vector(), FrameworkPropertyMetadataOptions.AffectsRender));

    public RadarPointCloudView()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        _persistenceTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
        _persistenceTimer.Tick += (_, _) =>
        {
            if (_rawPointFrames.GetLayers(DateTimeOffset.UtcNow).Count > 0 || _validPointFrames.GetLayers(DateTimeOffset.UtcNow).Count > 0)
                InvalidateVisual();
        };
        Loaded += (_, _) => _persistenceTimer.Start();
        Unloaded += (_, _) =>
        {
            _persistenceTimer.Stop();
            ClearPersistence();
        };
    }

    public RadarSensorRuntimeSnapshot? Snapshot { get => (RadarSensorRuntimeSnapshot?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public float MaximumRangeMeters { get => (float)GetValue(MaximumRangeMetersProperty); set => SetValue(MaximumRangeMetersProperty, value); }
    public IReadOnlyList<Point2>? RegionVertices { get => (IReadOnlyList<Point2>?)GetValue(RegionVerticesProperty); set => SetValue(RegionVerticesProperty, value); }
    public IReadOnlyList<IReadOnlyList<Point2>>? MaskedRegions { get => (IReadOnlyList<IReadOnlyList<Point2>>?)GetValue(MaskedRegionsProperty); set => SetValue(MaskedRegionsProperty, value); }
    public bool ShowRawPoints { get => (bool)GetValue(ShowRawPointsProperty); set => SetValue(ShowRawPointsProperty, value); }
    public bool ShowValidPoints { get => (bool)GetValue(ShowValidPointsProperty); set => SetValue(ShowValidPointsProperty, value); }
    public bool ShowClusters { get => (bool)GetValue(ShowClustersProperty); set => SetValue(ShowClustersProperty, value); }
    public bool ShowFilterOverlay { get => (bool)GetValue(ShowFilterOverlayProperty); set => SetValue(ShowFilterOverlayProperty, value); }
    public bool ShowBlindZone { get => (bool)GetValue(ShowBlindZoneProperty); set => SetValue(ShowBlindZoneProperty, value); }
    public bool IsRegionEditable { get => (bool)GetValue(IsRegionEditableProperty); set => SetValue(IsRegionEditableProperty, value); }
    public bool IsPanEnabled { get => (bool)GetValue(IsPanEnabledProperty); set => SetValue(IsPanEnabledProperty, value); }
    public Vector PanOffset { get => (Vector)GetValue(PanOffsetProperty); set => SetValue(PanOffsetProperty, value); }
    public event EventHandler<RegionVertexMovedEventArgs>? RegionVertexMoved;

    protected override void OnRender(DrawingContext context)
    {
        context.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (ActualWidth <= 0d || ActualHeight <= 0d) return;
        DrawGrid(context);
        if (ShowFilterOverlay) { DrawRegion(context); DrawMasks(context); }
        var snapshot = Snapshot;
        if (snapshot is null) { DrawText(context, "等待选中雷达数据", new Point(18d, 18d), Frozen(Color.FromRgb(147, 168, 188)), 12d); return; }
        if (ShowRawPoints) DrawPointLayers(context, _rawPointFrames.GetLayers(DateTimeOffset.UtcNow), RawPointBrush, 1.2d);
        if (ShowValidPoints) DrawPointLayers(context, _validPointFrames.GetLayers(DateTimeOffset.UtcNow), ValidPointBrush, 1.8d);
        if (ShowClusters) DrawClusters(context, snapshot);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        if (!IsRegionEditable && !IsPanEnabled) return;
        Focus();
        var mouse = args.GetPosition(this);
        if (IsRegionEditable && RegionVertices is not null)
        {
            for (var index = 0; index < RegionVertices.Count; index++)
            {
                if ((ToScreen(RegionVertices[index]) - mouse).Length > 14d) continue;
                _draggedVertex = index;
                CaptureMouse();
                args.Handled = true;
                return;
            }
        }

        if (!IsPanEnabled) return;
        _isPanning = true;
        _panStart = mouse;
        _panOrigin = PanOffset;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        args.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (args.LeftButton != MouseButtonState.Pressed)
        {
            if (_draggedVertex >= 0 || _isPanning) EndPointerInteraction();
            return;
        }

        var mouse = args.GetPosition(this);
        if (_draggedVertex >= 0)
        {
            var world = RadarViewportTransform.ScreenToWorld(mouse, ActualWidth, ActualHeight, Math.Max(.1f, MaximumRangeMeters), PanOffset);
            RegionVertexMoved?.Invoke(this, new RegionVertexMovedEventArgs(_draggedVertex, world));
            InvalidateVisual();
            args.Handled = true;
            return;
        }

        if (!_isPanning) return;
        PanOffset = _panOrigin + (mouse - _panStart);
        args.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonUp(args);
        if (_draggedVertex < 0 && !_isPanning) return;
        EndPointerInteraction();
        args.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs args)
    {
        base.OnLostMouseCapture(args);
        _draggedVertex = -1;
        _isPanning = false;
        Cursor = null;
    }

    private void DrawGrid(DrawingContext context)
    {
        var center = new Point(ActualWidth / 2d + PanOffset.X, ActualHeight / 2d + PanOffset.Y);
        var maximum = Math.Max(.1f, MaximumRangeMeters);
        var scale = RadarViewportTransform.CalculateScale(ActualWidth, ActualHeight, maximum);
        var pen = GridPen;
        for (var radius = maximum <= 10f ? 1f : 5f; radius <= maximum + .001f; radius += maximum <= 10f ? 1f : 5f)
            context.DrawEllipse(null, pen, center, radius * scale, radius * scale);
        context.DrawLine(pen, new Point(0d, center.Y), new Point(ActualWidth, center.Y));
        context.DrawLine(pen, new Point(center.X, 0d), new Point(center.X, ActualHeight));
        DrawText(context, $"{maximum:0.#} m", new Point(center.X + 8d, 12d), Frozen(Color.FromRgb(147, 168, 188)), 10d);
    }
    private void DrawClusters(DrawingContext context, RadarSensorRuntimeSnapshot snapshot)
    {
        var scale = RadarViewportTransform.CalculateScale(ActualWidth, ActualHeight, Math.Max(.1f, MaximumRangeMeters));
        foreach (var cluster in snapshot.Clusters)
        {
            var center = ToScreen(new Point2(cluster.CenterX, cluster.CenterY));
            var size = Math.Max(10d, cluster.WidthMeters * scale);
            context.DrawRectangle(null, ClusterPen, new Rect(center.X - size / 2d, center.Y - size / 2d, size, size));
        }
    }
    private void DrawPointLayers(DrawingContext context, IReadOnlyList<RadarPointPersistenceLayer> layers, Brush brush, double radius)
    {
        foreach (var layer in layers)
        {
            context.PushOpacity(layer.Opacity);
            foreach (var point in layer.Points) context.DrawEllipse(brush, null, ToScreen(new Point2(point.X, point.Y)), radius, radius);
            context.Pop();
        }
    }
    private void DrawRegion(DrawingContext context)
    {
        var vertices = RegionVertices;
        if (vertices is null || vertices.Count < 3) return;
        var pen = RegionPen;
        for (var index = 0; index < vertices.Count; index++)
        {
            var current = ToScreen(vertices[index]);
            context.DrawLine(pen, current, ToScreen(vertices[(index + 1) % vertices.Count]));
            context.DrawEllipse(BackgroundBrush, RegionVertexPen, current, 6d, 6d);
        }
    }
    private void DrawMasks(DrawingContext context)
    {
        if (MaskedRegions is null) return;
        var pen = MaskPen;
        foreach (var polygon in MaskedRegions.Where(region => region.Count >= 3))
        {
            var geometry = new StreamGeometry();
            using (var stream = geometry.Open())
            {
                stream.BeginFigure(ToScreen(polygon[0]), true, true);
                for (var index = 1; index < polygon.Count; index++) stream.LineTo(ToScreen(polygon[index]), true, false);
            }
            geometry.Freeze();
            context.DrawGeometry(MaskBrush, pen, geometry);
        }
    }
    private Point ToScreen(Point2 point) => RadarViewportTransform.WorldToScreen(point, ActualWidth, ActualHeight, Math.Max(.1f, MaximumRangeMeters), PanOffset);
    private void EndPointerInteraction()
    {
        _draggedVertex = -1;
        _isPanning = false;
        Cursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }
    private void DrawText(DrawingContext context, string text, Point origin, Brush brush, double size) =>
        context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), origin);
    private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Brush brush, double thickness, DashStyle? dashStyle = null)
    {
        var pen = new Pen(brush, thickness);
        if (dashStyle is not null) pen.DashStyle = dashStyle;
        pen.Freeze();
        return pen;
    }
    private static void OnSnapshotChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (RadarPointCloudView)dependencyObject;
        if (args.NewValue is RadarSensorRuntimeSnapshot snapshot)
        {
            if (!string.Equals(control._snapshotSensorId, snapshot.SensorId, StringComparison.OrdinalIgnoreCase))
            {
                control.ClearPersistence();
                control._snapshotSensorId = snapshot.SensorId;
            }
            var at = DateTimeOffset.UtcNow;
            if (control.ShowRawPoints) control._rawPointFrames.Add(snapshot.Sequence, at, snapshot.RawPoints);
            if (control.ShowValidPoints) control._validPointFrames.Add(snapshot.Sequence, at, snapshot.ValidPoints);
        }
        else
        {
            control.ClearPersistence();
            control._snapshotSensorId = null;
        }
        control.InvalidateVisual();
    }
    private void ClearPersistence()
    {
        _rawPointFrames.Clear();
        _validPointFrames.Clear();
    }
    private static void OnRegionVerticesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (RadarPointCloudView)dependencyObject;
        if (args.OldValue is INotifyCollectionChanged oldCollection) oldCollection.CollectionChanged -= control.OnRegionCollectionChanged;
        if (args.NewValue is INotifyCollectionChanged newCollection) newCollection.CollectionChanged += control.OnRegionCollectionChanged;
        control.InvalidateVisual();
    }
    private void OnRegionCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();
}
