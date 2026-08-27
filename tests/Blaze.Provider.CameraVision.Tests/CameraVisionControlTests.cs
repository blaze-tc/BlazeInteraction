using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraVisionControlTests
{
    [Fact]
    public async Task GetCapabilities_UsesIndependentBackendsWithoutRestartingActiveCamera()
    {
        using var services = new ControlServices(
            cameraBackendFactory: static () => new SteadyCameraBackend());
        await services.SaveAsync(Configuration(maxHands: 8));
        var provider = await CreateInitializedAsync(services);
        var control = (ICameraVisionControl)provider;
        await provider.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() =>
            control.CurrentStatus.CameraStatus == CameraCaptureStatus.Connected);
        var activeStatus = provider.Status;
        var completedRuns = provider.CompletedRunCount;

        var capabilities = await control.GetCapabilitiesAsync(0, CancellationToken.None);

        Assert.Equal(0, capabilities.Device.Index);
        Assert.NotEmpty(capabilities.Modes);
        Assert.Equal(activeStatus, provider.Status);
        Assert.Equal(CameraCaptureStatus.Connected, control.CurrentStatus.CameraStatus);
        Assert.Equal(completedRuns, provider.CompletedRunCount);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Apply_ValidatesBeforeSaving()
    {
        using var services = new ControlServices();
        await services.SaveAsync(Configuration(maxHands: 8));
        var provider = await CreateInitializedAsync(services);
        var control = Assert.IsAssignableFrom<ICameraVisionControl>(provider);
        var originalBytes = await File.ReadAllBytesAsync(services.ConfigurationPath);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            control.ApplyAsync(null!, CancellationToken.None));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(services.ConfigurationPath));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Apply_HandSettingsSavesThenRestartsOnlyProcessing()
    {
        using var services = new ControlServices(
            cameraBackendFactory: static () => new SteadyCameraBackend());
        await services.SaveAsync(Configuration(maxHands: 8));
        var provider = await CreateInitializedAsync(services);
        var control = (ICameraVisionControl)provider;
        await provider.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() =>
            control.CurrentStatus.CameraStatus == CameraCaptureStatus.Connected);
        Assert.Equal(0, provider.CompletedRunCount);
        var cameraCreates = services.CameraBackendCreates;
        var handCreates = services.HandBackendCreates;
        var next = Configuration(maxHands: 32);
        Assert.Equal(next.Capture.DeviceIndex, control.CurrentConfiguration!.Capture.DeviceIndex);
        Assert.Equal(next.Capture.Width, control.CurrentConfiguration.Capture.Width);
        Assert.Equal(next.Capture.Height, control.CurrentConfiguration.Capture.Height);
        Assert.Equal(next.Capture.FramesPerSecond, control.CurrentConfiguration.Capture.FramesPerSecond);
        Assert.Equal(next.Capture.MirrorX, control.CurrentConfiguration.Capture.MirrorX);
        Assert.Equal(next.Capture.Rotation, control.CurrentConfiguration.Capture.Rotation);
        Assert.Equal(next.Capture.ReconnectDelay, control.CurrentConfiguration.Capture.ReconnectDelay);

        await control.ApplyAsync(next, CancellationToken.None);

        Assert.Equal(cameraCreates, services.CameraBackendCreates);
        Assert.Equal(handCreates + 1, services.HandBackendCreates);
        Assert.Equal(0, provider.CompletedRunCount);
        Assert.Equal(32, control.CurrentConfiguration!.MaxHands);
        Assert.Equal(
            32,
            (await services.Store.LoadOrCreateAsync(CancellationToken.None))
            .Configuration!.MaxHands);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Apply_FailedProcessingRestartKeepsSavedConfigurationAndReportsError()
    {
        using var services = new ControlServices();
        services.HandBackends.Enqueue(() => new ReadyHandBackend());
        services.HandBackends.Enqueue(() => new FailingHandBackend("restart failed"));
        await services.SaveAsync(Configuration(maxHands: 8));
        var provider = await CreateInitializedAsync(services);
        var control = (ICameraVisionControl)provider;
        await provider.StartAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.ApplyAsync(Configuration(maxHands: 32), CancellationToken.None));

        Assert.Contains("restart failed", error.Message, StringComparison.Ordinal);
        Assert.Equal(32, control.CurrentConfiguration!.MaxHands);
        Assert.Equal(
            32,
            (await services.Store.LoadOrCreateAsync(CancellationToken.None))
            .Configuration!.MaxHands);
        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
        Assert.Contains("restart failed", control.CurrentStatus.Error!, StringComparison.Ordinal);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CalibrationClick_ConvertsPreviewCoordinatesToCameraPixelsAndSavesImmediately()
    {
        using var services = new ControlServices();
        await services.SaveAsync(Configuration(maxHands: 8));
        var provider = await CreateInitializedAsync(services);
        var control = (ICameraVisionControl)provider;

        await control.SetCalibrationPointAsync(
            "main",
            pointIndex: 0,
            previewPosition: new Vector2Data(50, 25),
            previewSize: new Vector2Data(200, 100),
            CancellationToken.None);

        var loaded = (await services.Store.LoadOrCreateAsync(CancellationToken.None)).Configuration!;
        Assert.Equal(25f, loaded.Calibrations["main"].P1.X, 3);
        Assert.Equal(25f, loaded.Calibrations["main"].P1.Y, 3);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task ResetCalibration_RemovesOnlySelectedSurface()
    {
        using var services = new ControlServices();
        await services.SaveAsync(Configuration(
            maxHands: 8,
            calibrations: new Dictionary<string, CameraCalibration>
            {
                ["main"] = Square(100),
                ["other"] = Square(50)
            }));
        var provider = await CreateInitializedAsync(services);
        var control = (ICameraVisionControl)provider;

        await control.ResetCalibrationAsync("main", CancellationToken.None);

        var loaded = (await services.Store.LoadOrCreateAsync(CancellationToken.None)).Configuration!;
        Assert.False(loaded.Calibrations.ContainsKey("main"));
        Assert.True(loaded.Calibrations.ContainsKey("other"));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task StatusSnapshotContainsCameraInferenceOutputAndUnityState()
    {
        using var services = new ControlServices(unityConnected: true);
        await services.SaveAsync(Configuration(maxHands: 8));
        var provider = await CreateInitializedAsync(services);
        var control = (ICameraVisionControl)provider;
        await provider.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() =>
            control.CurrentStatus.CameraStatus == CameraCaptureStatus.Disconnected);
        var published = new TaskCompletionSource<CameraVisionStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        control.StatusChanged += snapshot =>
        {
            if (snapshot.DetectedHandCount == 1)
            {
                published.TrySetResult(snapshot);
            }
        };

        provider.PublishHandFrame(HandFrame());
        var status = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(status.UnityConnected);
        Assert.Equal(CameraCaptureStatus.Disconnected, status.CameraStatus);
        Assert.True(status.CameraFramesPerSecond >= 0);
        Assert.True(status.InferenceFramesPerSecond >= 0);
        Assert.True(status.OutputFramesPerSecond >= 0);
        Assert.Equal(4.25, status.InferenceLatencyMilliseconds, 3);
        Assert.Equal(1, status.DetectedHandCount);
        Assert.Equal(new Vector2Data(0.4f, 0.6f), Assert.Single(status.TrackingCoordinates));
        Assert.Equal(3, status.DroppedFrames);
        Assert.NotNull(status.Preview);
        await provider.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private static async Task<CameraVisionProvider> CreateInitializedAsync(ControlServices services)
    {
        var provider = (CameraVisionProvider)new CameraVisionPlugin().CreateProvider(
            new ProviderCreateContext(ProviderDirectory(), services));
        await provider.InitializeAsync(
            new ProviderInitializationContext(
                [new InteractionSurface
                {
                    SurfaceId = "main",
                    Name = "Main",
                    LogicalWidth = 1920,
                    LogicalHeight = 1080,
                    IsPrimary = true,
                    Order = 0
                }],
                services),
            CancellationToken.None);
        return provider;
    }

    private static CameraVisionConfiguration Configuration(
        int maxHands,
        IReadOnlyDictionary<string, CameraCalibration>? calibrations = null) =>
        new(
            1,
            new CameraCaptureOptions
            {
                Width = 100,
                Height = 100,
                FramesPerSecond = 30,
                ReconnectDelay = TimeSpan.FromMilliseconds(20)
            },
            maxHands,
            0.5f,
            0.5f,
            HandTrackingPoint.IndexTip,
            0.35f,
            0.2f,
            2,
            calibrations ?? new Dictionary<string, CameraCalibration>
            {
                ["main"] = Square(100)
            });

    private static CameraCalibration Square(float size) => new(
        new Vector2Data(0, 0),
        new Vector2Data(size, 0),
        new Vector2Data(size, size),
        new Vector2Data(0, size));

    private static CameraHandFrame HandFrame()
    {
        var position = new Vector2Data(0.4f, 0.6f);
        var hand = new CameraHandSnapshot(
            1,
            0.9f,
            new Vector2Data(40, 60),
            position,
            Enumerable.Range(0, 21).Select(index => new CameraMappedLandmark(
                index,
                new Vector2Data(40, 60),
                position,
                -index / 100f)));
        return new CameraHandFrame(
            1,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [hand],
            [],
            new CameraPreviewSnapshot(2, 2, 6, new byte[12]),
            4.25,
            1,
            3,
            1,
            0);
    }

    private static string ProviderDirectory() =>
        Path.GetDirectoryName(typeof(CameraVisionPlugin).Assembly.Location)!;

    private sealed class ControlServices : IServiceProvider, IProviderStorageContext,
        ICameraCaptureBackendFactory, IDisposable
    {
        private readonly TemporaryDirectory _data = new();
        private readonly TestHostStatus _hostStatus;
        private readonly Func<ICameraCaptureBackend> _cameraBackendFactory;

        internal ControlServices(
            bool unityConnected = false,
            Func<ICameraCaptureBackend>? cameraBackendFactory = null)
        {
            _hostStatus = new TestHostStatus(unityConnected);
            _cameraBackendFactory = cameraBackendFactory
                ?? (static () => new UnavailableCameraBackend());
            Store = new CameraVisionConfigurationStore(this);
        }

        public string DataRoot => _data.Path;
        public string? ProfilePath => null;
        public Queue<Func<IHandDetectionBackend>> HandBackends { get; } = new();
        public int CameraBackendCreates { get; private set; }
        public int HandBackendCreates { get; private set; }
        public CameraVisionConfigurationStore Store { get; }
        public string ConfigurationPath => Store.ConfigurationPath;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IProviderStorageContext)) return this;
            if (serviceType == typeof(ICameraCaptureBackendFactory)) return this;
            if (serviceType == typeof(IInteractionHostStatus)) return _hostStatus;
            if (serviceType == typeof(IHandDetectionBackend))
            {
                HandBackendCreates++;
                return HandBackends.Count > 0
                    ? HandBackends.Dequeue()()
                    : new ReadyHandBackend();
            }

            return null;
        }

        public ICameraCaptureBackend Create()
        {
            CameraBackendCreates++;
            return _cameraBackendFactory();
        }

        public string GetProviderDataDirectory(string providerId) =>
            Path.Combine(DataRoot, "Providers", providerId);

        internal Task SaveAsync(CameraVisionConfiguration configuration) =>
            Store.SaveAsync(configuration, CancellationToken.None);

        public void Dispose() => _data.Dispose();
    }

    private sealed class TestHostStatus(bool connected) : IInteractionHostStatus
    {
        public InteractionHostStatus Current { get; } = connected
            ? new InteractionHostStatus(true, 42, "Unity Test", Array.Empty<InteractionSurface>())
            : InteractionHostStatus.Disconnected;
        public event Action<InteractionHostStatus>? Changed
        {
            add { }
            remove { }
        }
        public IInteractionHostStatusSubscription Subscribe(Action<InteractionHostStatus> changed) =>
            new Subscription(Current);

        private sealed class Subscription(InteractionHostStatus current) : IInteractionHostStatusSubscription
        {
            public InteractionHostStatus Current { get; } = current;
            public void Dispose() { }
        }
    }

    private sealed class UnavailableCameraBackend : ICameraCaptureBackend
    {
        public bool IsOpen => false;
        public bool TryOpen(CameraCaptureOptions options) => false;
        public bool TryRead(out CameraFrame? frame) { frame = null; return false; }
        public void Close() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SteadyCameraBackend : ICameraCaptureBackend
    {
        private long _sequence;
        public bool IsOpen { get; private set; }

        public bool TryOpen(CameraCaptureOptions options)
        {
            IsOpen = true;
            return true;
        }

        public bool TryRead(out CameraFrame? frame)
        {
            if (!IsOpen)
            {
                frame = null;
                return false;
            }

            Thread.Sleep(2);
            frame = new CameraFrame(
                Interlocked.Increment(ref _sequence),
                DateTimeOffset.UtcNow,
                new OpenCvSharp.Mat(
                    100,
                    100,
                    OpenCvSharp.MatType.CV_8UC3,
                    OpenCvSharp.Scalar.All(0)));
            return true;
        }

        public void Close() => IsOpen = false;

        public ValueTask DisposeAsync()
        {
            Close();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReadyHandBackend : IHandDetectionBackend
    {
        public HandBackendStatus Status { get; private set; }
        public Task InitializeAsync(HandDetectionOptions options, CancellationToken cancellationToken)
        {
            Status = HandBackendStatus.Ready;
            return Task.CompletedTask;
        }
        public ValueTask<HandDetectionResult> DetectAsync(
            RgbFrameView frame, long timestampUnixMs, CancellationToken cancellationToken) =>
            ValueTask.FromResult(HandDetectionResult.Empty);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingHandBackend(string message) : IHandDetectionBackend
    {
        public HandBackendStatus Status => HandBackendStatus.Faulted;
        public Task InitializeAsync(HandDetectionOptions options, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(message));
        public ValueTask<HandDetectionResult> DetectAsync(
            RgbFrameView frame, long timestampUnixMs, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BlazeCameraVisionControlTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
