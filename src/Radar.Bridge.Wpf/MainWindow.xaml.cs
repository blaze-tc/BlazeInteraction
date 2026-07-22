using System.Windows;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;

namespace Yuexin.Radar.Bridge.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel, IRadarBridgeRuntime runtime)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        ArgumentNullException.ThrowIfNull(runtime);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnRegionVertexMoved(object sender, RegionVertexMovedEventArgs eventArgs) =>
        _viewModel.UpdateRegionVertex(eventArgs.Index, eventArgs.WorldPosition);
}
