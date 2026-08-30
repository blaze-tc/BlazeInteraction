using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision;

public partial class CameraVisionSettingsWindow : Window
{
    private CameraVisionSettingsViewModel? _viewModel;
    private int? _draggingCalibrationPoint;
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
            UnityPointPreview.ActualWidth <= 0 || UnityPointPreview.ActualHeight <= 0)
        {
            return;
        }

        try
        {
            var models = CameraPreviewModelBuilder.BuildWorkspace(
                snapshot,
                viewModel.OutputSurface,
                RawCameraPreview.ActualWidth,
                RawCameraPreview.ActualHeight,
                UnityPointPreview.ActualWidth,
                UnityPointPreview.ActualHeight);
            _rawTransform = models.Raw.Transform;
            RawCameraPreview.UpdateFrame(snapshot.Preview, models.Raw);
            if (_draggingCalibrationPoint is null)
            {
                UpdateCalibrationOverlay(models.Raw.CalibrationVertices);
            }
            UnityPointPreview.Update(models.Unity);
        }
        catch (ArgumentException)
        {
            // A partially edited quadrilateral is ignored until the next valid status snapshot.
        }
    }

    private void UpdateCalibrationOverlay(IReadOnlyList<Vector2Data> vertices)
    {
        if (vertices.Count != 4)
        {
            return;
        }

        var handles = CalibrationHandles();
        for (var index = 0; index < handles.Length; index++)
        {
            MoveCalibrationHandle(handles[index], vertices[index].X, vertices[index].Y);
        }
        UpdateCalibrationPolygon(handles);
    }

    private void CalibrationHandle_DragDelta(object sender, DragDeltaEventArgs args)
    {
        if (sender is not Thumb handle ||
            handle.Tag is not string tag ||
            !int.TryParse(tag, out var index) ||
            _rawTransform is not { } transform)
        {
            return;
        }

        _draggingCalibrationPoint = index;
        var center = CalibrationHandleCenter(handle);
        var x = Math.Clamp(
            center.X + args.HorizontalChange,
            transform.OffsetX,
            transform.OffsetX + transform.ContentWidth);
        var y = Math.Clamp(
            center.Y + args.VerticalChange,
            transform.OffsetY,
            transform.OffsetY + transform.ContentHeight);
        MoveCalibrationHandle(handle, x, y);
        UpdateCalibrationPolygon(CalibrationHandles());
    }

    private async void CalibrationHandle_DragCompleted(
        object sender,
        DragCompletedEventArgs args)
    {
        if (sender is not Thumb handle ||
            handle.Tag is not string tag ||
            !int.TryParse(tag, out var index) ||
            _viewModel is null ||
            _viewModel.VisualSnapshot?.Preview is not { } preview ||
            _rawTransform is not { } transform)
        {
            _draggingCalibrationPoint = null;
            return;
        }

        var viewportPosition = CalibrationHandleCenter(handle);
        if (!transform.TryViewportToSource(
                new Vector2Data((float)viewportPosition.X, (float)viewportPosition.Y),
                out var sourcePosition))
        {
            _draggingCalibrationPoint = null;
            return;
        }

        _draggingCalibrationPoint = null;
        var saved = await _viewModel.SetCalibrationPointAsync(
            index,
            sourcePosition,
            new Vector2Data(preview.Width, preview.Height),
            CancellationToken.None);
        if (!saved)
        {
            ScheduleRender();
        }
    }

    private Thumb[] CalibrationHandles() =>
    [
        CalibrationHandleP1,
        CalibrationHandleP2,
        CalibrationHandleP3,
        CalibrationHandleP4
    ];

    private static void MoveCalibrationHandle(Thumb handle, double centerX, double centerY)
    {
        Canvas.SetLeft(handle, centerX - handle.Width / 2d);
        Canvas.SetTop(handle, centerY - handle.Height / 2d);
    }

    private static Point CalibrationHandleCenter(Thumb handle) =>
        new(
            Canvas.GetLeft(handle) + handle.Width / 2d,
            Canvas.GetTop(handle) + handle.Height / 2d);

    private void UpdateCalibrationPolygon(IReadOnlyList<Thumb> handles)
    {
        CalibrationPolygon.Points = new PointCollection(
            handles.Select(CalibrationHandleCenter));
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        RawCameraPreview.SizeChanged -= PreviewSurface_SizeChanged;
        UnityPointPreview.SizeChanged -= PreviewSurface_SizeChanged;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        }
    }
}
