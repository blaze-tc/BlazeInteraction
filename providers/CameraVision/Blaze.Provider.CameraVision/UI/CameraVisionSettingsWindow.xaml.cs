using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision;

public partial class CameraVisionSettingsWindow : Window
{
    private CameraVisionSettingsViewModel? _viewModel;
    private int? _armedCalibrationPoint;

    public CameraVisionSettingsWindow()
    {
        InitializeComponent();
        RotationCombo.ItemsSource = Enum.GetValues<CameraRotation>();
        TrackingCombo.ItemsSource = Enum.GetValues<HandTrackingPoint>();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
        PreviewHost.SizeChanged += (_, _) => RenderPreview();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = args.NewValue as CameraVisionSettingsViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RenderPreview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(CameraVisionSettingsViewModel.Preview) or
            nameof(CameraVisionSettingsViewModel.DetectedHandCount)) RenderPreview();
    }

    private void RenderPreview()
    {
        if (_viewModel?.Preview is not { } preview || PreviewHost.ActualWidth <= 0 || PreviewHost.ActualHeight <= 0) return;
        PreviewImage.Source = BitmapSource.Create(preview.Width, preview.Height, 96, 96,
            PixelFormats.Bgr24, null, preview.Bgr24.ToArray(), preview.StrideBytes);
        OverlayCanvas.Children.Clear();
        var overlay = CameraPreviewOverlayBuilder.Build(
            _viewModel.CurrentStatus, (float)PreviewHost.ActualWidth, (float)PreviewHost.ActualHeight);
        foreach (var bone in overlay.Bones)
            OverlayCanvas.Children.Add(new Line { X1 = bone.From.X, Y1 = bone.From.Y, X2 = bone.To.X, Y2 = bone.To.Y, Stroke = Brush(bone.Color), StrokeThickness = 2 });
        foreach (var outline in overlay.Outlines)
            OverlayCanvas.Children.Add(new Polyline { Points = new PointCollection(outline.Points.Select(point => new Point(point.X, point.Y))), Stroke = Brush(outline.Color), StrokeThickness = 1.5, Opacity = 0.8 });
        foreach (var joint in overlay.Joints) AddDot(joint.Position, Brush(joint.Color), 6);
        foreach (var tracking in overlay.TrackingPoints) AddDot(tracking.Position, Brush(tracking.Color), 14);
    }

    private void AddDot(Vector2Data point, Brush brush, double size)
    {
        var ellipse = new Ellipse { Width = size, Height = size, Fill = brush };
        Canvas.SetLeft(ellipse, point.X - size / 2); Canvas.SetTop(ellipse, point.Y - size / 2);
        OverlayCanvas.Children.Add(ellipse);
    }

    private static Brush Brush(string color) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    private void CalibrationPoint_Click(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out var index))
            _armedCalibrationPoint = index;
    }

    private async void PreviewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (_armedCalibrationPoint is not { } index || _viewModel is null) return;
        var position = args.GetPosition(PreviewHost);
        _armedCalibrationPoint = null;
        try
        {
            await _viewModel.SetCalibrationPointAsync(index,
                new Vector2Data((float)position.X, (float)position.Y),
                new Vector2Data((float)PreviewHost.ActualWidth, (float)PreviewHost.ActualHeight),
                CancellationToken.None);
        }
        catch
        {
            // The view model/provider status exposes actionable calibration failures.
        }
    }

    private void OnClosed(object? sender, EventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        }
    }
}
