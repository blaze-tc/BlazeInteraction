using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraVisionSettingsViewFactoryTests
{
    [Fact]
    public void PluginExposesFactoryAndFactoryRejectsWrongProvider()
    {
        var factory = Assert.IsAssignableFrom<IProviderSettingsViewFactory>(
            new CameraVisionPlugin().SettingsViewFactory);

        Assert.Throws<ArgumentException>(() =>
            factory.CreateView(new WrongProvider(), EmptySettingsContext.Instance));
    }

    [Fact]
    public async Task FactoryReturnsLoadableWindowAndDisposesViewModelOnClose()
    {
        using var services = new Services();
        var provider = (CameraVisionProvider)new CameraVisionPlugin().CreateProvider(
            new ProviderCreateContext(ProviderDirectory(), services));
        await provider.InitializeAsync(Initialization(services), CancellationToken.None);
        var factory = Assert.IsAssignableFrom<IProviderSettingsViewFactory>(
            new CameraVisionPlugin().SettingsViewFactory);
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var window = Assert.IsType<CameraVisionSettingsWindow>(
                    factory.CreateView(provider, EmptySettingsContext.Instance));
                var viewModel = Assert.IsType<CameraVisionSettingsViewModel>(window.DataContext);
                Assert.False(viewModel.IsDisposed);
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(
                    window.ActualHeight <= SystemParameters.WorkArea.Height,
                    $"Camera window height {window.ActualHeight} exceeds work area height {SystemParameters.WorkArea.Height}.");
                window.Close();
                Assert.True(viewModel.IsDisposed);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Camera settings window timed out.");
        failure?.Throw();
        await provider.DisposeAsync();
    }

    [Fact]
    public void XamlContainsRequiredControlsAndNoHandednessUi()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "providers", "CameraVision", "Blaze.Provider.CameraVision", "UI",
            "CameraVisionSettingsWindow.xaml"));

        foreach (var name in new[]
                 {
                     "RawCameraPreview", "CalibrationPreview", "UnityPointPreview",
                     "DeviceCombo", "ResolutionCombo", "FrameRateCombo",
                     "MirrorCheck", "RotationCombo", "MaxHandsBox", "TrackingCombo",
                     "DetectionConfidenceBox", "TrackingConfidenceBox", "SmoothingBox",
                     "CalibrationP1", "CalibrationP2", "CalibrationP3", "CalibrationP4",
                     "ResetCalibrationButton", "RefreshDevicesButton", "ApplyButton", "ReconnectButton",
                     "UnityStatusText", "CameraStatusText", "CameraFpsText", "InferenceFpsText",
                     "OutputFpsText", "LatencyText", "DetectedHandsText", "DroppedFramesText"
                 })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("handedness", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("left hand", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("right hand", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Command=\"{Binding RefreshDevicesCommand}\"", xaml, StringComparison.Ordinal);
    }

    private static ProviderInitializationContext Initialization(IServiceProvider services) => new(
        [new InteractionSurface
        {
            SurfaceId = "main", Name = "Main", LogicalWidth = 1920, LogicalHeight = 1080,
            IsPrimary = true, Order = 0
        }], services);

    private static string ProviderDirectory() =>
        Path.GetDirectoryName(typeof(CameraVisionPlugin).Assembly.Location)!;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlazeInteraction.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class Services : IServiceProvider, IProviderStorageContext, ICameraCaptureBackendFactory, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CameraFactoryTests", Guid.NewGuid().ToString("N"));
        public Services() => Directory.CreateDirectory(_root);
        public string DataRoot => _root;
        public string? ProfilePath => null;
        public object? GetService(Type type)
        {
            if (type == typeof(IProviderStorageContext)) return this;
            if (type == typeof(ICameraCaptureBackendFactory)) return this;
            if (type == typeof(IHandDetectionBackend)) return new ReadyHandBackend();
            return null;
        }
        public string GetProviderDataDirectory(string providerId) => Path.Combine(_root, "Providers", providerId);
        public ICameraCaptureBackend Create() => new UnavailableCameraBackend();
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class ReadyHandBackend : IHandDetectionBackend
    {
        public HandBackendStatus Status => HandBackendStatus.Ready;
        public Task InitializeAsync(HandDetectionOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask<HandDetectionResult> DetectAsync(RgbFrameView frame, long timestampUnixMs, CancellationToken cancellationToken) => ValueTask.FromResult(HandDetectionResult.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnavailableCameraBackend : ICameraCaptureBackend
    {
        public bool IsOpen => false;
        public bool TryOpen(CameraCaptureOptions options) => false;
        public bool TryRead(out CameraFrame? frame) { frame = null; return false; }
        public void Close() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WrongProvider : IInteractionProvider
    {
        public string ProviderInstanceId => "wrong";
        public ProviderRuntimeStatus Status => ProviderRuntimeStatus.Created;
        public event EventHandler<InteractionFrameEventArgs>? FrameReceived { add { } remove { } }
        public event EventHandler<ProviderStatusChangedEventArgs>? StatusChanged { add { } remove { } }
        public Task InitializeAsync(ProviderInitializationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptySettingsContext : IProviderSettingsContext
    {
        public static EmptySettingsContext Instance { get; } = new();
        public ValueTask<string?> GetValueAsync(string key, CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask SetValueAsync(string key, string? value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
