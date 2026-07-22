using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    public void BindingDiagnostics_ReportsDeliberatelyBrokenBinding()
    {
        WpfTestHost.Instance.Invoke(() =>
        {
            var listener = new BindingTraceListener();
            var source = PresentationTraceSources.DataBindingSource;
            var previousLevel = source.Switch.Level;
            source.Listeners.Add(listener);
            try
            {
                source.Switch.Level = SourceLevels.All;
                PresentationTraceSources.Refresh();
                source.Switch.Level = SourceLevels.All;
                var text = new TextBlock();
                var broken = new System.Windows.Data.Binding("MissingProperty") { Source = new object() };
                PresentationTraceSources.SetTraceLevel(broken, PresentationTraceLevel.High);
                BindingOperations.SetBinding(text, TextBlock.TextProperty, broken);
                var window = new Window { Content = text, Width = 100d, Height = 30d };
                window.Show();
                var expression = BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)!;
                PresentationTraceSources.SetTraceLevel(expression, PresentationTraceLevel.High);
                expression.UpdateTarget();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                source.Flush();
                Assert.Contains(listener.Messages, message => message.Contains("MissingProperty", StringComparison.Ordinal));
                window.Close();
            }
            finally
            {
                source.Listeners.Remove(listener);
                source.Switch.Level = previousLevel;
            }
        });
    }
    [Fact]
    public void Show_BindsSelectedSensorAndScreenSnapshotsToTheirSeparateViews()
    {
        WpfTestHost.Instance.Invoke(() =>
        {
            var listener = new BindingTraceListener();
            var bindingSource = PresentationTraceSources.DataBindingSource;
            var previousLevel = bindingSource.Switch.Level;
            bindingSource.Listeners.Add(listener);
            try
            {
                bindingSource.Switch.Level = SourceLevels.Error;
                PresentationTraceSources.Refresh();
                bindingSource.Switch.Level = SourceLevels.Error;
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
                Assert.True(listener.Messages.Count == 0, string.Join(Environment.NewLine, listener.Messages));
                window.Close();
            }
            finally
            {
                bindingSource.Listeners.Remove(listener);
                bindingSource.Switch.Level = previousLevel;
            }
        });
    }

    private sealed class WpfTestHost
    {
        private Dispatcher _dispatcher = null!;

        private WpfTestHost()
        {
            var initialized = new ManualResetEventSlim();
            ExceptionDispatchInfo? initializationException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var application = new App();
                    application.InitializeComponent();
                    _dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception exception)
                {
                    initializationException = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    initialized.Set();
                }

                if (initializationException is null)
                {
                    Dispatcher.Run();
                }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(initialized.Wait(TimeSpan.FromSeconds(10)), "WPF test host startup timed out.");
            initializationException?.Throw();
        }

        public static WpfTestHost Instance { get; } = new();

        public void Invoke(Action action) => _dispatcher.Invoke(action);
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

    private sealed class BindingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void Fail(string? message, string? detailMessage)
        {
            if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message);
            if (!string.IsNullOrWhiteSpace(detailMessage)) Messages.Add(detailMessage);
        }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message);
        }
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            var message = args is { Length: > 0 }
                ? string.Format(format ?? string.Empty, args)
                : format;
            if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message);
        }
        public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, object? data)
        {
            if (data is not null) Messages.Add(data.ToString()!);
        }
        public override void TraceData(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, params object?[]? data)
        {
            foreach (var item in data ?? [])
            {
                if (item is not null) Messages.Add(item.ToString()!);
            }
        }
    }
}
