using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class MainWindowBindingTests
{
    [Fact]
    public void Show_BindsSelectedSensorAndScreenSnapshotsToTheirSeparateViews()
    {
        ExceptionDispatchInfo? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App();
                application.InitializeComponent();
                var runtime = new TestRuntime();
                using var viewModel = new MainViewModel(RadarAppConfiguration.CreateDefault(), runtime);
                var window = new MainWindow(viewModel, runtime);
                var timestamp = DateTimeOffset.UnixEpoch.AddSeconds(42);
                var sensorSnapshot = new RadarSensorRuntimeSnapshot(
                    "main", "sensor-1", 42, timestamp,
                    [new RadarPoint(100, 10, 1f, 1f, 2f)],
                    [new RadarPoint(90, 9, .9f, .8f, 1.8f)],
                    [new RadarCluster(7, [], 1.2f, 2.3f, .4f, 2.6f)],
                    [new SensorDetection(7, 1024f, 512f, 1f)],
                    12.5d, 345d, 6, 7, 8);
                var screenSnapshot = new RadarScreenRuntimeSnapshot(
                    new RadarScreenInfo("main", "Main", 1920, 1080, true, 0),
                    [sensorSnapshot],
                    [new FusedScreenTarget(1, 1024f, 512f, 1f, 1, true)],
                    [new RadarScreenPointer(1, RadarPointerPhase.Move, .5f, .5f, 1024f, 512f, 1f, timestamp.ToUnixTimeMilliseconds())],
                    42, timestamp);

                window.Show();
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                runtime.PublishSensorSnapshot(sensorSnapshot);
                runtime.PublishScreenSnapshot(screenSnapshot);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var raw = Assert.IsType<RadarPointCloudView>(window.FindName("RawRadarView"));
                var fusion = Assert.IsType<RadarScreenFusionView>(window.FindName("ScreenFusionView"));
                Assert.Same(sensorSnapshot, raw.Snapshot);
                Assert.Same(screenSnapshot, fusion.Snapshot);
                Assert.Same(viewModel.SelectedScreen!.Sensors, fusion.Sensors);
                foreach (var name in new[] { "ScreenList", "SensorList", "RawRadarView", "ScreenFusionView", "ScreenParameterScroll" })
                {
                    var element = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
                    Assert.True(double.IsFinite(element.ActualWidth) && element.ActualWidth > 0d);
                    Assert.True(double.IsFinite(element.ActualHeight) && element.ActualHeight > 0d);
                }

                window.WindowState = WindowState.Minimized;
                window.WindowState = WindowState.Normal;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.NotNull(window.FindName("RawRadarView"));
                Assert.NotNull(window.FindName("ScreenFusionView"));
                window.Close();
            }
            catch (Exception exception)
            {
                capturedException = ExceptionDispatchInfo.Capture(exception);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "WPF window startup timed out.");
        capturedException?.Throw();
    }

    private sealed class TestRuntime : IRadarBridgeRuntime
    {
        public event Action<RadarSensorRuntimeSnapshot>? SensorSnapshotUpdated;
        public event Action<RadarScreenRuntimeSnapshot>? ScreenSnapshotUpdated;
        public event Action<RadarSensorRuntimeStateChanged>? SensorStateChanged { add { } remove { } }
        public event Action<string>? LogReceived { add { } remove { } }
        public event Action<UnityClientStatus>? UnityStatusChanged { add { } remove { } }
        public UnityClientStatus UnityStatus { get; } = UnityClientStatus.Disconnected;
        public Task StartInfrastructureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void PublishSensorSnapshot(RadarSensorRuntimeSnapshot snapshot) => SensorSnapshotUpdated?.Invoke(snapshot);
        public void PublishScreenSnapshot(RadarScreenRuntimeSnapshot snapshot) => ScreenSnapshotUpdated?.Invoke(snapshot);
    }
}
