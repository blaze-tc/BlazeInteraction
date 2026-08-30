using System.Windows;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;

namespace Yuexin.Radar.Bridge.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<string, RadarRegionEditorWindow> _regionEditors = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(MainViewModel viewModel, IRadarBridgeRuntime runtime)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(runtime);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnRegionVertexMoved(object sender, RegionVertexMovedEventArgs eventArgs) =>
        _viewModel.UpdateRegionVertex(eventArgs.Index, eventArgs.WorldPosition);

    private void OnOpenRegionEditor(object sender, RoutedEventArgs eventArgs)
    {
        var sensor = _viewModel.SelectedSensor;
        var screen = _viewModel.SelectedScreen;
        if (sensor is null || screen is null) return;
        var editorKey = string.Concat(screen.ScreenId, "\u001F", sensor.SensorId);

        if (_regionEditors.TryGetValue(editorKey, out var editor) && editor.IsVisible)
        {
            editor.Activate();
            return;
        }

        var newEditor = new RadarRegionEditorWindow(sensor) { Owner = this };
        _regionEditors[editorKey] = newEditor;
        newEditor.Closed += (_, _) =>
        {
            if (_regionEditors.TryGetValue(editorKey, out var current) && ReferenceEquals(current, newEditor))
            {
                _regionEditors.Remove(editorKey);
            }
        };
        newEditor.Show();
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        foreach (var editor in _regionEditors.Values.ToArray())
        {
            editor.Close();
        }
        _regionEditors.Clear();
        base.OnClosed(eventArgs);
    }
}
