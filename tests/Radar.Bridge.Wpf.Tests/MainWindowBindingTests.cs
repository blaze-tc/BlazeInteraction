using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Yuexin.Radar.Bridge.Wpf.Converters;
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

    [Fact]
    public void NarrowSensorParameters_KeepsEveryCalibrationActionInsideViewport()
    {
        WpfTestHost.Instance.Invoke(() =>
        {
            var runtime = new TestRuntime();
            using var viewModel = new MainViewModel(RadarAppConfiguration.CreateDefault(), runtime);
            var window = new MainWindow(viewModel, runtime);
            window.Show();
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            var sensorTab = FindVisualChildren<TabItem>(window)
                .Single(item => string.Equals(item.Header as string, "雷达参数", StringComparison.Ordinal));
            sensorTab.IsSelected = true;
            window.UpdateLayout();

            var scroll = Assert.IsType<ScrollViewer>(window.FindName("SensorParameterScroll"));
            var expected = new[] { "重置区域", "添加屏蔽", "删除屏蔽", "开始标定", "采集点", "撤销", "保存", "清除" };
            var buttons = FindVisualChildren<Button>(scroll)
                .Where(button => button.Content is string text && expected.Contains(text, StringComparer.Ordinal))
                .ToArray();

            Assert.Equal(expected.Length, buttons.Length);
            var visibleRight = scroll.ActualWidth - SystemParameters.VerticalScrollBarWidth;
            foreach (var button in buttons)
            {
                var rightEdge = button.TransformToAncestor(scroll).Transform(new Point(button.ActualWidth, 0d)).X;
                Assert.True(rightEdge <= visibleRight + 0.5d,
                    $"'{button.Content}' extends to {rightEdge:0.0}px beyond the {visibleRight:0.0}px viewport.");
            }

            window.Close();
        });
    }

    [Fact]
    public void Show_OffersMatchingStartAndStopSimulationActions()
    {
        WpfTestHost.Instance.Invoke(() =>
        {
            var runtime = new TestRuntime();
            using var viewModel = new MainViewModel(RadarAppConfiguration.CreateDefault(), runtime);
            var window = new MainWindow(viewModel, runtime);
            window.Show();
            window.UpdateLayout();

            var labels = FindVisualChildren<Button>(window)
                .Select(button => button.Content as string)
                .Where(label => label is not null)
                .ToArray();
            Assert.Contains("一键模拟", labels);
            Assert.Contains("停止模拟", labels);

            window.Close();
        });
    }

    [Fact]
    public void FloatingPointEditor_AcceptsCommaDecimalAndKeepsTrailingSeparatorWhileTyping()
    {
        WpfTestHost.Instance.Invoke(() =>
        {
            var configuration = RadarAppConfiguration.CreateDefault();
            var runtime = new TestRuntime();
            using var viewModel = new MainViewModel(configuration, runtime);
            var window = new MainWindow(viewModel, runtime);
            window.Show();
            var sensorTab = FindVisualChildren<TabItem>(window)
                .Single(item => string.Equals(item.Header as string, "雷达参数", StringComparison.Ordinal));
            sensorTab.IsSelected = true;
            window.UpdateLayout();
            var editor = FindVisualChildren<TextBox>(window).Single(textBox =>
                string.Equals(BindingOperations.GetBinding(textBox, TextBox.TextProperty)?.Path?.Path,
                    "SelectedSensor.LeftEdgeDeadZoneMeters", StringComparison.Ordinal));

            editor.Text = "0,";
            editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal("0,", editor.Text);

            editor.Text = "0,125";
            editor.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.Equal(0.125f, configuration.Screens[0].Sensors[0].Range.EdgeDeadZones.LeftMeters);
            window.Close();
        });
    }

    [Fact]
    public void EveryVisibleFloatingPointParameter_UsesTheFlexibleNumericConverter()
    {
        WpfTestHost.Instance.Invoke(() =>
        {
            var configuration = RadarAppConfiguration.CreateDefault();
            var runtime = new TestRuntime();
            using var viewModel = new MainViewModel(configuration, runtime);
            var window = new MainWindow(viewModel, runtime);
            window.Show();

            AssertFloatingPointBindings(window, "屏幕参数", "SelectedScreen.", typeof(ScreenItemViewModel), 3);
            AssertFloatingPointBindings(window, "雷达参数", "SelectedSensor.", typeof(SensorItemViewModel), 12);

            window.Close();
        });
    }

    [Fact]
    public void RegionEditor_OpensAFittedLargePreviewAndZoomDoesNotChangeRadarRange()
    {
        WpfTestHost.Instance.Invoke(() =>
        {
            var configuration = RadarAppConfiguration.CreateDefault();
            configuration.Screens[0].Sensors[0].Range.VisualizationRangeMeters = 2f;
            var runtime = new TestRuntime();
            using var viewModel = new MainViewModel(configuration, runtime);
            var window = new MainWindow(viewModel, runtime);
            window.Show();
            window.UpdateLayout();

            var openButton = FindVisualChildren<Button>(window)
                .SingleOrDefault(button => string.Equals(button.Content as string, "放大编辑", StringComparison.Ordinal));
            Assert.NotNull(openButton);
            openButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var editor = Application.Current.Windows.Cast<Window>()
                .SingleOrDefault(candidate => candidate.Owner == window && candidate != window);
            Assert.NotNull(editor);
            var rangeProperty = editor.GetType().GetProperty("EditorRangeMeters");
            Assert.NotNull(rangeProperty);
            var fittedRange = Assert.IsType<float>(rangeProperty.GetValue(editor));
            Assert.Equal(2.75f, fittedRange, 3);
            Assert.True(editor.ActualWidth >= 760d);
            Assert.True(editor.ActualHeight >= 520d);

            var editorView = Assert.Single(FindVisualChildren<RadarPointCloudView>(editor));
            var panEnabledProperty = typeof(RadarPointCloudView).GetProperty("IsPanEnabled");
            var panOffsetProperty = typeof(RadarPointCloudView).GetProperty("PanOffset");
            Assert.NotNull(panEnabledProperty);
            Assert.NotNull(panOffsetProperty);
            Assert.True(Assert.IsType<bool>(panEnabledProperty.GetValue(editorView)));
            panOffsetProperty.SetValue(editorView, new Vector(40d, -25d));

            var center = FindVisualChildren<Button>(editor)
                .Single(button => string.Equals(button.Content as string, "居中", StringComparison.Ordinal));
            center.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new Vector(), Assert.IsType<Vector>(panOffsetProperty.GetValue(editorView)));

            var zoomIn = FindVisualChildren<Button>(editor)
                .Single(button => string.Equals(button.Content as string, "放大", StringComparison.Ordinal));
            zoomIn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            Assert.True(Assert.IsType<float>(rangeProperty.GetValue(editor)) < fittedRange);
            Assert.Equal(2f, configuration.Screens[0].Sensors[0].Range.VisualizationRangeMeters);

            editor.Close();
            window.Close();
        });
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void AssertFloatingPointBindings(
        MainWindow window,
        string tabHeader,
        string bindingPrefix,
        Type viewModelType,
        int expectedCount)
    {
        var tab = FindVisualChildren<TabItem>(window)
            .Single(item => string.Equals(item.Header as string, tabHeader, StringComparison.Ordinal));
        tab.IsSelected = true;
        window.UpdateLayout();

        var bindings = FindVisualChildren<TextBox>(window)
            .Select(textBox => BindingOperations.GetBinding(textBox, TextBox.TextProperty))
            .Where(binding => binding?.Path?.Path?.StartsWith(bindingPrefix, StringComparison.Ordinal) == true)
            .Where(binding =>
            {
                var propertyName = binding!.Path.Path[bindingPrefix.Length..];
                var propertyType = Nullable.GetUnderlyingType(viewModelType.GetProperty(propertyName)!.PropertyType)
                    ?? viewModelType.GetProperty(propertyName)!.PropertyType;
                return propertyType == typeof(float) || propertyType == typeof(double) || propertyType == typeof(decimal);
            })
            .ToList();

        Assert.Equal(expectedCount, bindings.Count);
        Assert.All(bindings, binding => Assert.IsType<FlexibleNumericTextConverter>(binding!.Converter));
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
                    application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
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
