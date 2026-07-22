using System.Windows;
using Microsoft.Win32;
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

    private void OnRegionVertexMoved(object sender, RegionVertexMovedEventArgs eventArgs)
    {
        _viewModel.UpdateRegionVertex(eventArgs.Index, eventArgs.WorldPosition);
    }

    private async void OnStartRecordingClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存雷达原始数据录制",
            Filter = "Radar recording (*.radarrec)|*.radarrec",
            DefaultExt = ".radarrec",
            AddExtension = true,
            FileName = $"radar-{DateTime.Now:yyyyMMdd-HHmmss}.radarrec"
        };
        if (dialog.ShowDialog(this) == true)
        {
            await ExecuteUiActionAsync(() => _viewModel.StartRecordingAsync(dialog.FileName));
        }
    }

    private async void OnStopRecordingClick(object sender, RoutedEventArgs eventArgs)
    {
        await ExecuteUiActionAsync(_viewModel.StopRecordingAsync);
    }

    private async void OnReplayClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开雷达原始数据录制",
            Filter = "Radar recording (*.radarrec)|*.radarrec",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await ExecuteUiActionAsync(() => _viewModel.ReplaySelectedSensorAsync(dialog.FileName, 1d, false));
    }

    private void OnPauseReplayClick(object sender, RoutedEventArgs eventArgs) => _viewModel.PauseSelectedReplay();
    private void OnResumeReplayClick(object sender, RoutedEventArgs eventArgs) => _viewModel.ResumeSelectedReplay();
    private void OnStepReplayClick(object sender, RoutedEventArgs eventArgs) => _viewModel.StepSelectedReplay();

    private async void OnStopReplayClick(object sender, RoutedEventArgs eventArgs)
    {
        await ExecuteUiActionAsync(_viewModel.StopSelectedReplayAsync);
    }

    private static async Task ExecuteUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "RadarBridge 操作失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
