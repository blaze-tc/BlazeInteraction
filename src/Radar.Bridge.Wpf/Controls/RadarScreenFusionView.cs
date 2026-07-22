using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;
using RadarPixelRect = Yuexin.Radar.Configuration.RadarPixelRect;

namespace Yuexin.Radar.Bridge.Wpf.Controls;

/// <summary>Renders the selected screen's detector, fusion, and pointer output in pixel space.</summary>
public sealed class RadarScreenFusionView : FrameworkElement
{
    private const double MarkerRadius = 2.5;
    private static readonly Brush BackgroundBrush = Frozen(Color.FromRgb(8, 15, 25));
    private static readonly Brush GridBrush = Frozen(Color.FromRgb(38, 59, 82));
    private static readonly Brush TargetBrush = Frozen(Color.FromRgb(251, 168, 76));
    private static readonly Brush PointerDownBrush = Frozen(Color.FromRgb(56, 211, 214));
    private static readonly Brush PointerUpBrush = Frozen(Color.FromRgb(245, 93, 91));
    private static readonly Brush PointerMoveBrush = Frozen(Color.FromRgb(192, 132, 252));
    private static readonly Pen GridPen = FrozenPen(GridBrush, 1d);
    private static readonly Pen TargetPen = FrozenPen(TargetBrush, 2d);
    private static readonly Pen PointerDownPen = FrozenPen(PointerDownBrush, 2d);
    private static readonly Pen PointerUpPen = FrozenPen(PointerUpBrush, 2d);
    private static readonly Pen PointerMovePen = FrozenPen(PointerMoveBrush, 2d);
    private readonly Dictionary<string, Brush> _sensorBrushes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Pen> _sensorPens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FormattedText> _labelCache = [];
    private INotifyCollectionChanged? _sensorCollection;
    private readonly HashSet<SensorItemViewModel> _observedSensors = [];

    public static readonly DependencyProperty SnapshotProperty = DependencyProperty.Register(
        nameof(Snapshot), typeof(RadarScreenRuntimeSnapshot), typeof(RadarScreenFusionView),
        new FrameworkPropertyMetadata(null, OnSensorsChanged));

    public static readonly DependencyProperty SensorsProperty = DependencyProperty.Register(
        nameof(Sensors), typeof(IEnumerable), typeof(RadarScreenFusionView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedSensorIdProperty = DependencyProperty.Register(
        nameof(SelectedSensorId), typeof(string), typeof(RadarScreenFusionView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public RadarScreenFusionView()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
        Loaded += (_, _) => AttachSensors();
        Unloaded += (_, _) => DetachSensors();
    }

    public RadarScreenRuntimeSnapshot? Snapshot
    {
        get => (RadarScreenRuntimeSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public IEnumerable? Sensors
    {
        get => (IEnumerable?)GetValue(SensorsProperty);
        set => SetValue(SensorsProperty, value);
    }

    public string? SelectedSensorId
    {
        get => (string?)GetValue(SelectedSensorIdProperty);
        set => SetValue(SelectedSensorIdProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(RenderSize));
        var snapshot = Snapshot;
        if (snapshot is null || ActualWidth <= 0d || ActualHeight <= 0d)
        {
            DrawLabel(drawingContext, "等待屏幕融合数据", new Point(18d, 18d), Frozen(Color.FromRgb(147, 168, 188)));
            return;
        }

        DrawGrid(drawingContext, snapshot.Screen);
        foreach (var sensor in snapshot.Sensors)
        {
            var configuration = FindSensor(sensor.SensorId);
            var brush = SensorBrush(sensor.SensorId);
            if (configuration is not null) DrawOutputRect(drawingContext, configuration.OutputRectPixels, brush);
            foreach (var detection in sensor.Detections)
            {
                var point = ToView(detection.PixelX, detection.PixelY);
                var radius = string.Equals(sensor.SensorId, SelectedSensorId, StringComparison.OrdinalIgnoreCase) ? MarkerRadius + .7d : MarkerRadius;
                drawingContext.DrawEllipse(brush, null, point, radius, radius);
            }
        }

        foreach (var target in snapshot.Targets)
        {
            var point = ToView(target.PixelX, target.PixelY);
            drawingContext.DrawEllipse(null, TargetPen, point, 6d, 6d);
            DrawLabel(drawingContext, $"T{target.TrackId}", point + new Vector(8d, -15d), TargetBrush);
        }

        foreach (var pointer in snapshot.Pointers)
        {
            var point = ToView(pointer.PixelX, pointer.PixelY);
            var (brush, pen) = pointer.Phase switch
            {
                RadarPointerPhase.Down => (PointerDownBrush, PointerDownPen),
                RadarPointerPhase.Up => (PointerUpBrush, PointerUpPen),
                _ => (PointerMoveBrush, PointerMovePen)
            };
            drawingContext.DrawEllipse(null, pen, point, 8d, 8d);
            DrawLabel(drawingContext, $"P{pointer.PointerId}", point + new Vector(10d, 5d), brush);
        }
    }

    private void DrawGrid(DrawingContext drawingContext, RadarScreenInfo screen)
    {
        var pen = GridPen;
        var viewport = ScreenViewport();
        drawingContext.DrawRectangle(null, pen, viewport);
        for (var step = 1; step < 4; step++)
        {
            var x = viewport.Left + viewport.Width * step / 4d;
            var y = viewport.Top + viewport.Height * step / 4d;
            drawingContext.DrawLine(pen, new Point(x, viewport.Top), new Point(x, viewport.Bottom));
            drawingContext.DrawLine(pen, new Point(viewport.Left, y), new Point(viewport.Right, y));
        }
        DrawLabel(drawingContext, $"{screen.WidthPixels} × {screen.HeightPixels}", new Point(viewport.Left + 8d, viewport.Bottom - 20d), Frozen(Color.FromRgb(147, 168, 188)));
    }

    private void DrawOutputRect(DrawingContext drawingContext, RadarPixelRect outputRect, Brush brush)
    {
        var topLeft = ToView(outputRect.X, outputRect.Y + outputRect.Height);
        var viewport = ScreenViewport();
        var width = outputRect.Width / Math.Max(1d, Snapshot!.Screen.WidthPixels) * viewport.Width;
        var height = outputRect.Height / Math.Max(1d, Snapshot.Screen.HeightPixels) * viewport.Height;
        drawingContext.DrawRectangle(null, SensorPen(brush), new Rect(topLeft.X, topLeft.Y, width, height));
    }

    private Point ToView(float pixelX, float pixelY)
    {
        var viewport = ScreenViewport();
        var x = viewport.Left + pixelX / Math.Max(1, Snapshot!.Screen.WidthPixels) * viewport.Width;
        var y = viewport.Bottom - pixelY / Math.Max(1, Snapshot.Screen.HeightPixels) * viewport.Height;
        return new Point(x, y);
    }

    private Rect ScreenViewport()
    {
        var screen = Snapshot?.Screen;
        if (screen is null || ActualWidth <= 0d || ActualHeight <= 0d) return new Rect();
        var scale = Math.Min(ActualWidth / Math.Max(1d, screen.WidthPixels), ActualHeight / Math.Max(1d, screen.HeightPixels));
        var width = screen.WidthPixels * scale;
        var height = screen.HeightPixels * scale;
        return new Rect((ActualWidth - width) / 2d, (ActualHeight - height) / 2d, width, height);
    }

    private static void OnSensorsChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (RadarScreenFusionView)dependencyObject;
        control.DetachSensors();
        if (control.IsLoaded) control.AttachSensors();
        control.InvalidateVisual();
    }

    private void AttachSensors()
    {
        if (Sensors is INotifyCollectionChanged collection)
        {
            _sensorCollection = collection;
            _sensorCollection.CollectionChanged += OnSensorsCollectionChanged;
        }
        foreach (var sensor in Sensors?.Cast<object>().OfType<SensorItemViewModel>() ?? [])
        {
            if (_observedSensors.Add(sensor)) sensor.PropertyChanged += OnSensorPropertyChanged;
        }
    }

    private void DetachSensors()
    {
        if (_sensorCollection is not null) _sensorCollection.CollectionChanged -= OnSensorsCollectionChanged;
        _sensorCollection = null;
        foreach (var sensor in _observedSensors) sensor.PropertyChanged -= OnSensorPropertyChanged;
        _observedSensors.Clear();
    }

    private void OnSensorsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        DetachSensors();
        AttachSensors();
        InvalidateVisual();
    }

    private void OnSensorPropertyChanged(object? sender, PropertyChangedEventArgs args) => InvalidateVisual();

    private SensorItemViewModel? FindSensor(string sensorId) => Sensors?.Cast<object>()
        .OfType<SensorItemViewModel>()
        .FirstOrDefault(sensor => string.Equals(sensor.SensorId, sensorId, StringComparison.OrdinalIgnoreCase));

    private void DrawLabel(DrawingContext drawingContext, string text, Point origin, Brush brush)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var key = $"{text}|{brush}|{pixelsPerDip:0.###}";
        if (!_labelCache.TryGetValue(key, out var formatted))
        {
            if (_labelCache.Count >= 128) _labelCache.Clear();
            formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11d, brush, pixelsPerDip);
            _labelCache[key] = formatted;
        }
        drawingContext.DrawText(formatted, origin);
    }

    private Brush SensorBrush(string sensorId)
    {
        if (_sensorBrushes.TryGetValue(sensorId, out var existing)) return existing;
        var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(sensorId);
        var hue = (hash & 0x7fffffff) % 360;
        var color = Hsv(hue, .68d, .95d);
        var brush = Frozen(color);
        _sensorBrushes[sensorId] = brush;
        return brush;
    }
    private Pen SensorPen(Brush brush)
    {
        var key = brush.ToString();
        if (_sensorPens.TryGetValue(key, out var pen)) return pen;
        pen = FrozenPen(brush, 1.4d);
        _sensorPens[key] = pen;
        return pen;
    }

    private static Color Hsv(int hue, double saturation, double value)
    {
        var c = value * saturation;
        var x = c * (1d - Math.Abs((hue / 60d % 2d) - 1d));
        var m = value - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)((r + m) * 255d), (byte)((g + m) * 255d), (byte)((b + m) * 255d));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
