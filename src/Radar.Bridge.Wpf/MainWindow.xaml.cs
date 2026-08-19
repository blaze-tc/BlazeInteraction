using System.Windows;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;

namespace Yuexin.Radar.Bridge.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private RadarRegionEditorWindow? _regionEditorWindow;

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
        if (sensor is null) return;

        if (_regionEditorWindow is { IsVisible: true } editor && ReferenceEquals(editor.DataContext, sensor))
        {
            editor.Activate();
            return;
        }

        _regionEditorWindow?.Close();
        var newEditor = new RadarRegionEditorWindow(sensor) { Owner = this };
        _regionEditorWindow = newEditor;
        newEditor.Closed += (_, _) =>
        {
            if (ReferenceEquals(_regionEditorWindow, newEditor)) _regionEditorWindow = null;
        };
        newEditor.Show();
    }
}
