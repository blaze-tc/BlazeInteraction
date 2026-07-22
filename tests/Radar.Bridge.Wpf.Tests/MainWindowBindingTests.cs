using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using System.Text.RegularExpressions;
using Yuexin.Radar.Bridge.Wpf.Controls;
using Yuexin.Radar.Bridge.Wpf.Services;
using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class MainWindowBindingTests
{
    [Fact]
    public void MainWindow_BindingPathsExistOnTheTaskSevenTemporaryAdapter()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Radar.Bridge.Wpf", "MainWindow.xaml"));
        var paths = Regex.Matches(xaml, @"\{Binding\s+([A-Za-z_][A-Za-z0-9_]*)(?:[.,}\s])")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var properties = typeof(MainViewModel).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var missing = paths.Where(path => !properties.Contains(path)).ToArray();

        Assert.Empty(missing);
        Assert.IsAssignableFrom<System.Windows.Input.ICommand>(typeof(MainViewModel).GetProperty("ConnectCommand")!.GetValue(new MainViewModel(RadarAppConfiguration.CreateDefault(), new TestRuntime())));
    }
    [Fact]
    public void Show_BindsSelectedSensorSnapshotToBothPointCloudViews()
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
                var snapshot = new RadarSensorRuntimeSnapshot(
                    "main", "sensor-1", 42, timestamp,
                    [new RadarPoint(100, 10, 1f, 1f, 2f)],
                    [new RadarPoint(90, 9, .9f, .8f, 1.8f)],
                    [new RadarCluster(7, [], 1.2f, 2.3f, .4f, 2.6f)], [],
                    12.5d, 345d, 6, 7, 8);

                window.Show();
                runtime.PublishSensorSnapshot(snapshot);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var latest = Assert.IsType<RadarRuntimeSnapshot>(viewModel.LatestSnapshot);
                Assert.Equal(snapshot.Sequence, latest.Sequence);
                Assert.Equal(snapshot.Timestamp, latest.Timestamp);
                Assert.Same(snapshot.RawPoints, latest.RawPoints);
                Assert.Same(snapshot.ValidPoints, latest.ValidPoints);
                Assert.Same(snapshot.Clusters, latest.Clusters);
                Assert.Equal(snapshot.ScanFrequencyHz, latest.ScanFrequencyHz);
                Assert.Equal(snapshot.ReceivedBytesPerSecond, latest.ReceivedBytesPerSecond);
                Assert.Equal(snapshot.CrcErrorCount, latest.CrcErrorCount);
                Assert.Equal(snapshot.DiscardedByteCount, latest.DiscardedByteCount);
                Assert.Empty(latest.Targets);
                Assert.Empty(latest.Pointers);

                var raw = Assert.IsType<RadarPointCloudView>(window.FindName("RawRadarView"));
                var output = Assert.IsType<RadarPointCloudView>(window.FindName("UnityOutputRadarView"));
                Assert.Same(latest, raw.Snapshot);
                Assert.Same(latest, output.Snapshot);
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
        public event Action<string>? LogReceived { add { } remove { } }
        public event Action<UnityClientStatus>? UnityStatusChanged { add { } remove { } }

        public UnityClientStatus UnityStatus { get; } = UnityClientStatus.Disconnected;

        public Task StartInfrastructureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void PublishSensorSnapshot(RadarSensorRuntimeSnapshot snapshot) => SensorSnapshotUpdated?.Invoke(snapshot);
    }
}
