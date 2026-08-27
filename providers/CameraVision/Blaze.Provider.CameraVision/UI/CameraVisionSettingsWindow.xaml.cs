using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision;

public partial class CameraVisionSettingsWindow : Window
{
    private CameraVisionSettingsViewModel? _viewModel;
    private int? _armedCalibrationPoint;
    private AspectFitTransform? _rawTransform;
    private bool _renderScheduled;

    public CameraVisionSettingsWindow()
    {
        InitializeComponent();
        RotationCombo.ItemsSource = Enum.GetValues<CameraRotation>();
        TrackingCombo.ItemsSource = Enum.GetValues<HandTrackingPoint>();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        RawCameraPreview.SizeChanged += PreviewSurface_SizeChanged;
        CalibrationPreview.SizeChanged += PreviewSurface_SizeChanged;
        UnityPointPreview.SizeChanged += PreviewSurface_SizeChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = args.NewValue as CameraVisionSettingsViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ScheduleRender();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CameraVisionSettingsViewModel.VisualSnapshot))
            ScheduleRender();
    }

    private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs args) =>
        ScheduleRender();

    private void ScheduleRender()
    {
        if (_renderScheduled || !IsLoaded && _viewModel is null) return;
        _renderScheduled = true;
        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                _renderScheduled = false;
                RenderPreviews();
            }));
    }

    private void RenderPreviews()
    {
        var viewModel = _viewModel;
        var snapshot = viewModel?.VisualSnapshot;
        if (viewModel is null || snapshot?.Preview is null ||
            RawCameraPreview.ActualWidth <= 0 || RawCameraPreview.ActualHeight <= 0 ||
            CalibrationPreview.ActualWidth <= 0 || CalibrationPreview.ActualHeight <= 0 ||
            UnityPointPreview.ActualWidth <= 0 || UnityPointPreview.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            var models = CameraPreviewModelBuilder.Build(
                snapshot,
                viewModel.OutputSurface,
                RawCameraPreview.ActualWidth,
                RawCameraPreview.ActualHeight,
                CalibrationPreview.ActualWidth,
                CalibrationPreview.ActualHeight,
                UnityPointPreview.ActualWidth,
                UnityPointPreview.ActualHeight);
            _rawTransform = models.Raw.Transform;
            RawCameraPreview.UpdateFrame(snapshot.Preview, models.Raw);
            CalibrationPreview.UpdateFrame(models.Calibration.Preview, models.Calibration);
            UnityPointPreview.Update(models.Unity);
        }
        catch (ArgumentException)
        {
            // A partially edited quadrilateral is ignored until the next valid status snapshot.
        }
    }

    private void CalibrationPoint_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
            _armedCalibrationPoint = index;
    }

    private async void RawPreviewHost_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs args)
    {
        if (_armedCalibrationPoint is not { } index ||
            _viewModel is null ||
            _viewModel.VisualSnapshot?.Preview is not { } preview ||
            _rawTransform is not { } transform)
        {
            return;
        }

        var viewportPosition = args.GetPosition(RawCameraPreview);
        if (!transform.TryViewportToSource(
                new Vector2Data((float)viewportPosition.X, (float)viewportPosition.Y),
                out var sourcePosition))
        {
            return;
        }

        _armedCalibrationPoint = null;
        try
        {
            await _viewModel.SetCalibrationPointAsync(
                index,
                sourcePosition,
                new Vector2Data(preview.Width, preview.Height),
                CancellationToken.None);
        }
        catch
        {
            // The provider status exposes actionable calibration failures.
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        RawCameraPreview.SizeChanged -= PreviewSurface_SizeChanged;
        CalibrationPreview.SizeChanged -= PreviewSurface_SizeChanged;
        UnityPointPreview.SizeChanged -= PreviewSurface_SizeChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        }
    }
}
