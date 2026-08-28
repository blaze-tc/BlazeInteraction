using System.Windows;
using System.Windows.Input;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Bridge.Wpf.ViewModels;

namespace Yuexin.Radar.Bridge.Wpf;

public partial class RadarRegionEditorWindow : Window
{
    private const float ZoomFactor = 1.25f;
    private const float MinimumEditorRangeMeters = 0.1f;
    private const float MaximumEditorRangeMeters = 100f;
    private readonly SensorItemViewModel _sensor;

    public static readonly DependencyProperty EditorRangeMetersProperty = DependencyProperty.Register(
        nameof(EditorRangeMeters), typeof(float), typeof(RadarRegionEditorWindow),
        new FrameworkPropertyMetadata(4f));

    public RadarRegionEditorWindow(SensorItemViewModel sensor)
    {
        _sensor = sensor ?? throw new ArgumentNullException(nameof(sensor));
        InitializeComponent();
        DataContext = sensor;
        EditorRangeMeters = RadarViewportTransform.CalculateFittedRange(sensor.VisualizationRangeMeters, sensor.RegionVertices);
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        RegionEditorView.DisposeDisplayResources();
        DataContext = null;
        base.OnClosed(eventArgs);
    }

    public float EditorRangeMeters
    {
        get => (float)GetValue(EditorRangeMetersProperty);
        private set => SetValue(EditorRangeMetersProperty, Math.Clamp(value, MinimumEditorRangeMeters, MaximumEditorRangeMeters));
    }

    private void OnZoomIn(object sender, RoutedEventArgs eventArgs) => EditorRangeMeters /= ZoomFactor;
    private void OnZoomOut(object sender, RoutedEventArgs eventArgs) => EditorRangeMeters *= ZoomFactor;
    private void OnFitRegion(object sender, RoutedEventArgs eventArgs)
    {
        EditorRangeMeters = RadarViewportTransform.CalculateFittedRange(_sensor.VisualizationRangeMeters, _sensor.RegionVertices);
        RegionEditorView.PanOffset = new Vector();
    }
    private void OnUseConfiguredRange(object sender, RoutedEventArgs eventArgs)
    {
        EditorRangeMeters = _sensor.VisualizationRangeMeters;
        RegionEditorView.PanOffset = new Vector();
    }
    private void OnCenterViewport(object sender, RoutedEventArgs eventArgs) => RegionEditorView.PanOffset = new Vector();
    private void OnClose(object sender, RoutedEventArgs eventArgs) => Close();

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        EditorRangeMeters = eventArgs.Delta > 0
            ? EditorRangeMeters / ZoomFactor
            : EditorRangeMeters * ZoomFactor;
        eventArgs.Handled = true;
    }

    private void OnRegionVertexMoved(object sender, RegionVertexMovedEventArgs eventArgs) =>
        _sensor.UpdateRegionVertex(eventArgs.Index, eventArgs.WorldPosition);
}
