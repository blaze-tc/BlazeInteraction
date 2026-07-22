using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;
using System.Text.RegularExpressions;
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
    public void Show_DoesNotCreateTwoWayBindingsForReadOnlyMetrics()
    {
        ExceptionDispatchInfo? capturedException = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = new App();
                application.InitializeComponent();
                var runtime = new TestRuntime();
                var viewModel = new MainViewModel(RadarAppConfiguration.CreateDefault(), runtime);
                var window = new MainWindow(viewModel, runtime);

                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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
        public event Action<string>? LogReceived { add { } remove { } }
        public event Action<UnityClientStatus>? UnityStatusChanged { add { } remove { } }

        public UnityClientStatus UnityStatus { get; } = UnityClientStatus.Disconnected;

        public Task StartInfrastructureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task StartSimulationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopSimulationAsync() => Task.CompletedTask;
        public Task StartRecordingAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopRecordingAsync() => Task.CompletedTask;
        public Task ReplayAsync(string path, double speed, bool loop, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void PauseReplay() { }
        public void ResumeReplay() { }
        public void StepReplay() { }
        public Task StopReplayAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
