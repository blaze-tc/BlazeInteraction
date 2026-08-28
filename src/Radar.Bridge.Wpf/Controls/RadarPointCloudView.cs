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
    private static readonly Brush EdgeDeadZoneBrush = Frozen(Color.FromArgb(92, 255, 176, 32));
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
    private readonly DispatcherTimer _interactionTimer;
    private readonly RadarRenderScheduler _renderScheduler;
    private readonly RadarDisplayExceptionBoundary _renderExceptionBoundary = new(RadarDisplayDiagnostics.Report);
    private string? _snapshotSensorId;
    private int _draggedVertex = -1;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panOrigin;
    private bool _displayInteractionActive;
    private bool _displayResourcesDisposed;

    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(RadarSensorRuntimeSnapshot), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(null, OnSnapshotChanged));
    public static readonly DependencyProperty MaximumRangeMetersProperty = DependencyProperty.Register(
        nameof(MaximumRangeMeters), typeof(float), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(5f, OnInteractiveVisualPropertyChanged));
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
    public static readonly DependencyProperty LeftEdgeDeadZoneMetersProperty = DependencyProperty.Register(
        nameof(LeftEdgeDeadZoneMeters), typeof(float), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(0f, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RightEdgeDeadZoneMetersProperty = DependencyProperty.Register(
        nameof(RightEdgeDeadZoneMeters), typeof(float), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(0f, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TopEdgeDeadZoneMetersProperty = DependencyProperty.Register(
        nameof(TopEdgeDeadZoneMeters), typeof(float), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(0f, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BottomEdgeDeadZoneMetersProperty = DependencyProperty.Register(
        nameof(BottomEdgeDeadZoneMeters), typeof(float), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(0f, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsRegionEditableProperty = DependencyProperty.Register(
        nameof(IsRegionEditable), typeof(bool), typeof(RadarPointCloudView), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty IsPanEnabledProperty = DependencyProperty.Register(
        nameof(IsPanEnabled), typeof(bool), typeof(RadarPointCloudView), new FrameworkPropertyMetadata(false));
    public static readonly DependencyProperty PanOffsetProperty = DependencyProperty.Register(
        nameof(PanOffset), typeof(Vector), typeof(RadarPointCloudView),
        new FrameworkPropertyMetadata(new Vector(), OnInteractiveVisualPropertyChanged));

    public RadarPointCloudView()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        _renderScheduler = new RadarRenderScheduler(
            new DispatcherSynchronizationContext(Dispatcher),
            InvalidateVisual);
        _persistenceTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
        _persistenceTimer.Tick += (_, _) =>
        {
            var budget = CurrentDisplayBudget();
            if (_rawPointFrames.GetLayers(DateTimeOffset.UtcNow, budget).Count > 0 || _validPointFrames.GetLayers(DateTimeOffset.UtcNow, budget).Count > 0)
                _renderScheduler.RequestRender();
        };
        _interactionTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(150) };
        _interactionTimer.Tick += (_, _) => EndDisplayInteraction();
        SizeChanged += (_, _) => _renderScheduler.RequestRender();
        Loaded += (_, _) =>
        {
            _persistenceTimer.Start();
            _renderScheduler.RequestRender();
        };
        Unloaded += (_, _) =>
        {
            _persistenceTimer.Stop();
            _interactionTimer.Stop();
            _displayInteractionActive = false;
            _renderScheduler.EndInteraction();
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
    public float LeftEdgeDeadZoneMeters { get => (float)GetValue(LeftEdgeDeadZoneMetersProperty); set => SetValue(LeftEdgeDeadZoneMetersProperty, value); }
    public float RightEdgeDeadZoneMeters { get => (float)GetValue(RightEdgeDeadZoneMetersProperty); set => SetValue(RightEdgeDeadZoneMetersProperty, value); }
    public float TopEdgeDeadZoneMeters { get => (float)GetValue(TopEdgeDeadZoneMetersProperty); set => SetValue(TopEdgeDeadZoneMetersProperty, value); }
    public float BottomEdgeDeadZoneMeters { get => (float)GetValue(BottomEdgeDeadZoneMetersProperty); set => SetValue(BottomEdgeDeadZoneMetersProperty, value); }
    public bool IsRegionEditable { get => (bool)GetValue(IsRegionEditableProperty); set => SetValue(IsRegionEditableProperty, value); }
    public bool IsPanEnabled { get => (bool)GetValue(IsPanEnabledProperty); set => SetValue(IsPanEnabledProperty, value); }
    public Vector PanOffset { get => (Vector)GetValue(PanOffsetProperty); set => SetValue(PanOffsetProperty, value); }
    public event EventHandler<RegionVertexMovedEventArgs>? RegionVertexMoved;
    internal bool DisplayResourcesDisposed => _displayResourcesDisposed;

    internal void DisposeDisplayResources()
    {
        if (_displayResourcesDisposed) return;
        _displayResourcesDisposed = true;
        _persistenceTimer.Stop();
        _interactionTimer.Stop();
        if (RegionVertices is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnRegionCollectionChanged;
        }
        _renderScheduler.Dispose();
        ClearPersistence();
        _snapshotSensorId = null;
    }

    protected override void OnRender(DrawingContext context)
    {
        if (!_renderExceptionBoundary.TryRender(() => RenderDisplay(context), CreateDisplayMetrics()))
        {
            ClearPersistence();
        }
    }

    private void RenderDisplay(DrawingContext context)
    {
        context.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        if (ActualWidth <= 0d || ActualHeight <= 0d) return;
        DrawGrid(context);
        if (ShowFilterOverlay) { DrawEdgeDeadZones(context); DrawRegion(context); DrawMasks(context); }
        var snapshot = Snapshot;
        if (snapshot is null) { DrawText(context, "等待选中雷达数据", new Point(18d, 18d), Frozen(Color.FromRgb(147, 168, 188)), 12d); return; }
        var budget = CurrentDisplayBudget();
        if (ShowRawPoints) DrawPointLayers(context, _rawPointFrames.GetLayers(DateTimeOffset.UtcNow, budget), RawPointBrush, 1.2d);
        if (ShowValidPoints) DrawPointLayers(context, _validPointFrames.GetLayers(DateTimeOffset.UtcNow, budget), ValidPointBrush, 1.8d);
        if (ShowClusters) DrawClusters(context, snapshot);
    }

    private RadarDisplayMetrics CreateDisplayMetrics()
    {
        var now = DateTimeOffset.UtcNow;
        var budget = CurrentDisplayBudget();
        var rawLayers = ShowRawPoints ? _rawPointFrames.GetLayers(now, budget) : [];
        var validLayers = ShowValidPoints ? _validPointFrames.GetLayers(now, budget) : [];
        return new RadarDisplayMetrics(
            Snapshot?.SensorId ?? _snapshotSensorId ?? string.Empty,
            Snapshot?.RawPoints.Count ?? 0,
            rawLayers.Sum(layer => layer.Points.Count) + validLayers.Sum(layer => layer.Points.Count),
            Math.Max(rawLayers.Count, validLayers.Count),
            _renderScheduler.RenderedCount,
            _renderScheduler.CoalescedRequestCount);
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
                BeginDisplayInteraction();
                CaptureMouse();
                args.Handled = true;
                return;
            }
        }

        if (!IsPanEnabled) return;
        _isPanning = true;
        BeginDisplayInteraction();
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
            BeginDisplayInteraction();
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
    private void DrawPointLayers(DrawingContext context, IReadOnlyList<RadarDisplayLayer> layers, Brush brush, double radius)
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
    private RadarDisplayBudget CurrentDisplayBudget() => RadarDisplayBudget.ForViewport(
        ActualWidth,
        ActualHeight,
        _displayInteractionActive);
    private void EndPointerInteraction()
    {
        _draggedVertex = -1;
        _isPanning = false;
        Cursor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        BeginDisplayInteraction();
    }
    private void DrawEdgeDeadZones(DrawingContext context)
    {
        var vertices = RegionVertices;
        if (vertices is null || vertices.Count < 3) return;
        var scale = RadarViewportTransform.CalculateScale(ActualWidth, ActualHeight, Math.Max(.1f, MaximumRangeMeters));
        var clip = new StreamGeometry();
        using (var stream = clip.Open())
        {
            stream.BeginFigure(ToScreen(vertices[0]), true, true);
            for (var index = 1; index < vertices.Count; index++) stream.LineTo(ToScreen(vertices[index]), true, false);
        }
        clip.Freeze();
        context.PushClip(clip);
        foreach (var edge in RadarRegionEdges.Classify(vertices))
        {
            var widthMeters = edge.Side switch
            {
                RadarRegionEdgeSide.Left => LeftEdgeDeadZoneMeters,
                RadarRegionEdgeSide.Right => RightEdgeDeadZoneMeters,
                RadarRegionEdgeSide.Top => TopEdgeDeadZoneMeters,
                RadarRegionEdgeSide.Bottom => BottomEdgeDeadZoneMeters,
                _ => 0f
            };
            if (!float.IsFinite(widthMeters) || widthMeters <= 0f) continue;
            var pen = new Pen(EdgeDeadZoneBrush, Math.Max(1d, widthMeters * scale * 2d));
            pen.Freeze();
            context.DrawLine(pen, ToScreen(edge.Start), ToScreen(edge.End));
        }
        context.Pop();
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
        control._renderScheduler.RequestRender();
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
        control.BeginDisplayInteraction();
    }
    private void OnRegionCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => BeginDisplayInteraction();

    private static void OnInteractiveVisualPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args) =>
        ((RadarPointCloudView)dependencyObject).BeginDisplayInteraction();

    private void BeginDisplayInteraction()
    {
        if (_displayResourcesDisposed) return;
        _displayInteractionActive = true;
        _renderScheduler.BeginInteraction();
        _interactionTimer.Stop();
        _interactionTimer.Start();
        _renderScheduler.RequestRender();
    }

    private void EndDisplayInteraction()
    {
        _interactionTimer.Stop();
        if (_displayResourcesDisposed) return;
        if (!_displayInteractionActive) return;
        _displayInteractionActive = false;
        _renderScheduler.EndInteraction();
        _renderScheduler.RequestRender();
    }
}
