using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.Interaction.Runtime;
using OpenCvSharp;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraVisionProviderTests
{
    [Fact]
    public void Manifest_IsProviderApi1AndMatchesPluginDescriptor()
    {
        var manifestPath = Path.Combine(ProviderOutputDirectory(), "provider.json");
        var manifest = JsonSerializer.Deserialize<ProviderManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("CameraVision manifest deserialized to null.");
        var plugin = new CameraVisionPlugin();

        Assert.Equal(1, manifest.ProviderApiVersion);
        Assert.Equal(CameraVisionPlugin.ProviderId, manifest.Id);
        Assert.Equal(plugin.Descriptor.Id, manifest.Id);
        Assert.Equal(plugin.Descriptor.DisplayName, manifest.DisplayName);
        Assert.Equal(plugin.Descriptor.Version, Version.Parse(manifest.Version));
        Assert.Equal(plugin.Descriptor.Category, manifest.Category);
        Assert.Equal(
            plugin.Descriptor.Capabilities.Order(StringComparer.Ordinal),
            manifest.Capabilities.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CatalogAndLoader_DiscoverCameraVisionProvider()
    {
        using var directory = new TemporaryDirectory();
        var providerDirectory = Path.Combine(directory.Path, "CameraVision");
        CopyDirectory(ProviderOutputDirectory(), providerDirectory);

        var entry = Assert.Single(new ProviderCatalog().Discover(directory.Path));
        using var loaded = new ProviderLoader().Load(entry);

        Assert.True(entry.IsAvailable, entry.Error);
        Assert.Equal(CameraVisionPlugin.ProviderId, loaded.Plugin.Descriptor.Id);
    }

    [Fact]
    public async Task RealHandPipeline_PublishesEightStandardPointsWithTwentyOneLandmarks()
    {
        using var services = new TestServices(
            () => new SingleFrameCameraBackend(),
            () => new FakeHandBackend(new HandDetectionResult(
                Enumerable.Range(0, 8)
                    .Select(index => Hand(0.1f + (index * 0.1f), 0.5f)))));
        await services.SeedConfigurationAsync();
        var provider = await InitializedProviderAsync(services);
        var frameReady = NextInteractionFrame(provider);

        await provider.StartAsync(CancellationToken.None);
        var frame = await frameReady.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(8, frame.Points.Count);
        Assert.Equal(8, frame.Points.Select(point => point.Id).Distinct().Count());
        foreach (var point in frame.Points)
        {
            Assert.Equal(InteractionPhase.Hover, point.Phase);
            Assert.Empty(point.Fp);
            Assert.StartsWith("hand-track-", point.SourceId, StringComparison.Ordinal);
            Assert.NotNull(point.Extensions);
            var hand = point.Extensions!["hand"];
            Assert.Equal(1, hand.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("IndexTip", hand.GetProperty("trackingPoint").GetString());
            Assert.False(hand.TryGetProperty("handedness", out _));
            var landmarks = hand.GetProperty("landmarks").EnumerateArray().ToArray();
            Assert.Equal(21, landmarks.Length);
            Assert.Equal(Enumerable.Range(0, 21),
                landmarks.Select(landmark => landmark.GetProperty("index").GetInt32()));
            Assert.InRange(point.NormalizedPosition.X, 0f, 1f);
            Assert.Equal(point.NormalizedPosition.X * 1920f, point.PixelPosition.X, 3);
            Assert.Equal(point.NormalizedPosition.Y * 1080f, point.PixelPosition.Y, 3);
        }

        await provider.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task PublishingReorderedHands_PreservesTrackIdsAndSourceIds()
    {
        using var services = new TestServices(
            () => new UnavailableCameraBackend(),
            () => new FakeHandBackend(HandDetectionResult.Empty));
        await services.SeedConfigurationAsync();
        var provider = await InitializedProviderAsync(services);
        await provider.StartAsync(CancellationToken.None);
        var received = new List<InteractionFrame>();
        provider.FrameReceived += (_, args) => received.Add(args.Frame);

        provider.PublishHandFrame(ProcessingFrame([Snapshot(1, 0.2f), Snapshot(2, 0.8f)], [], 1));
        provider.PublishHandFrame(ProcessingFrame([Snapshot(2, 0.79f), Snapshot(1, 0.21f)], [], 2));

        Assert.Equal(2, received.Count);
        Assert.Equal(new long[] { 1, 2 }, received[0].Points.Select(point => point.Id));
        Assert.Equal(new long[] { 2, 1 }, received[1].Points.Select(point => point.Id));
        Assert.Equal("hand-track-1", received[1].Points[1].SourceId);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task RemovedTrack_PublishesOneCancelAndThenStaysSilent()
    {
        using var services = new TestServices(
            () => new UnavailableCameraBackend(),
            () => new FakeHandBackend(HandDetectionResult.Empty));
        await services.SeedConfigurationAsync();
        var provider = await InitializedProviderAsync(services);
        await provider.StartAsync(CancellationToken.None);
        var received = new List<InteractionFrame>();
        provider.FrameReceived += (_, args) => received.Add(args.Frame);

        provider.PublishHandFrame(ProcessingFrame([Snapshot(7, 0.4f)], [], 1));
        provider.PublishHandFrame(ProcessingFrame([], [7], 2));
        provider.PublishHandFrame(ProcessingFrame([], [7], 3));

        Assert.Equal(2, received.Count);
        Assert.Equal(InteractionPhase.Hover, Assert.Single(received[0].Points).Phase);
        Assert.Equal(InteractionPhase.Cancel, Assert.Single(received[1].Points).Phase);
        Assert.Equal(7, received[1].Points[0].Id);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task UnavailableCamera_PublishesNoFakePointAndReportsDisconnectedCamera()
    {
        using var services = new TestServices(
            () => new UnavailableCameraBackend(),
            () => new FakeHandBackend(HandDetectionResult.Empty));
        await services.SeedConfigurationAsync();
        var provider = await InitializedProviderAsync(services);
        var received = 0;
        provider.FrameReceived += (_, _) => Interlocked.Increment(ref received);

        await provider.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => provider.CameraStatus == CameraCaptureStatus.Disconnected);
        await Task.Delay(100);

        Assert.Equal(ProviderRuntimeStatus.Running, provider.Status);
        Assert.Equal(0, Volatile.Read(ref received));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task BackendInitializationFailure_FaultsProviderWithActionableError()
    {
        using var services = new TestServices(
            () => new UnavailableCameraBackend(),
            () => new FailingHandBackend("model could not be loaded"));
        await services.SeedConfigurationAsync();
        var provider = await InitializedProviderAsync(services);
        Exception? statusError = null;
        provider.StatusChanged += (_, args) => statusError = args.Error ?? statusError;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.StartAsync(CancellationToken.None));

        Assert.Equal(ProviderRuntimeStatus.Faulted, provider.Status);
        Assert.Contains("model could not be loaded", exception.Message, StringComparison.Ordinal);
        Assert.Contains("model could not be loaded", statusError!.Message, StringComparison.Ordinal);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Lifecycle_RepeatedStartStopFiftyTimesReleasesEachBackend()
    {
        using var services = new TestServices(
            () => new UnavailableCameraBackend(),
            () => new FakeHandBackend(HandDetectionResult.Empty));
        await services.SeedConfigurationAsync();
        var provider = await InitializedProviderAsync(services);

        for (var cycle = 0; cycle < 50; cycle++)
        {
            await provider.StartAsync(CancellationToken.None);
            Assert.Equal(ProviderRuntimeStatus.Running, provider.Status);
            await provider.StopAsync(CancellationToken.None);
            Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
        }

        await provider.DisposeAsync();
        await provider.DisposeAsync();
        Assert.Equal(50, provider.CompletedRunCount);
        Assert.Equal(50, services.CreatedBackendCount);
        Assert.Equal(50, services.DisposedBackendCount);
    }

    private static async Task<CameraVisionProvider> InitializedProviderAsync(TestServices services)
    {
        var provider = (CameraVisionProvider)new CameraVisionPlugin().CreateProvider(
            new ProviderCreateContext(ProviderOutputDirectory(), services));
        await provider.InitializeAsync(Initialization(services), CancellationToken.None);
        return provider;
    }

    private static ProviderInitializationContext Initialization(IServiceProvider services) => new(
        [new InteractionSurface
        {
            SurfaceId = "main",
            Name = "Main",
            LogicalWidth = 1920,
            LogicalHeight = 1080,
            IsPrimary = true,
            Order = 0
        }],
        services);

    private static Task<InteractionFrame> NextInteractionFrame(CameraVisionProvider provider)
    {
        var source = new TaskCompletionSource<InteractionFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.FrameReceived += (_, args) => source.TrySetResult(args.Frame);
        return source.Task;
    }

    private static CameraHandFrame ProcessingFrame(
        IReadOnlyList<CameraHandSnapshot> active,
        IReadOnlyList<long> removed,
        long sequence) =>
        new(
            sequence,
            1000 + sequence,
            active,
            removed,
            new CameraPreviewSnapshot(1, 1, 3, new byte[3]),
            inferenceMilliseconds: 1,
            processedFrameCount: sequence,
            droppedFrameCount: 0,
            detectedHandCount: active.Count,
            rejectedHandCount: 0);

    private static CameraHandSnapshot Snapshot(long trackId, float position)
    {
        var normalized = new Vector2Data(position, 0.5f);
        return new CameraHandSnapshot(
            trackId,
            0.9f,
            new Vector2Data(position * 100f, 50f),
            normalized,
            Enumerable.Range(0, 21).Select(index => new CameraMappedLandmark(
                index,
                new Vector2Data(position * 100f, 50f),
                normalized,
                -index / 100f)));
    }

    private static DetectedHand Hand(float x, float y)
    {
        var landmarks = Enumerable.Range(0, 21)
            .Select(index => new HandLandmark(x, y, -index / 100f))
            .ToArray();
        landmarks[8] = new HandLandmark(x, y, -0.08f);
        return new DetectedHand(0.9f, landmarks);
    }

    private static string ProviderOutputDirectory()
    {
        var directory = Path.GetDirectoryName(typeof(CameraVisionPlugin).Assembly.Location)!;
        Assert.True(File.Exists(Path.Combine(directory, "provider.json")),
            $"CameraVision provider manifest was not copied to {directory}.");
        return directory;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class TestServices : IServiceProvider, ICameraCaptureBackendFactory,
        IProviderStorageContext, IDisposable
    {
        private readonly Func<ICameraCaptureBackend> _cameraFactory;
        private readonly Func<IHandDetectionBackend> _handFactory;
        private readonly TemporaryDirectory _data = new();
        private int _createdBackendCount;
        private int _disposedBackendCount;

        public TestServices(
            Func<ICameraCaptureBackend> cameraFactory,
            Func<IHandDetectionBackend> handFactory)
        {
            _cameraFactory = cameraFactory;
            _handFactory = handFactory;
        }

        public string DataRoot => _data.Path;
        public string? ProfilePath => null;
        public int CreatedBackendCount => Volatile.Read(ref _createdBackendCount);
        public int DisposedBackendCount => Volatile.Read(ref _disposedBackendCount);

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ICameraCaptureBackendFactory)) return this;
            if (serviceType == typeof(IProviderStorageContext)) return this;
            if (serviceType == typeof(IHandDetectionBackend))
            {
                Interlocked.Increment(ref _createdBackendCount);
                var backend = _handFactory();
                return new DisposeObservedBackend(
                    backend,
                    () => Interlocked.Increment(ref _disposedBackendCount));
            }

            return null;
        }

        public ICameraCaptureBackend Create() => _cameraFactory();

        public string GetProviderDataDirectory(string providerId) =>
            Path.Combine(DataRoot, "Providers", providerId);

        public Task SeedConfigurationAsync()
        {
            var configuration = new CameraVisionConfiguration(
                schemaVersion: 1,
                capture: new CameraCaptureOptions
                {
                    Width = 100,
                    Height = 100,
                    FramesPerSecond = 30,
                    ReconnectDelay = TimeSpan.FromSeconds(1)
                },
                maxHands: 32,
                minDetectionConfidence: 0.5f,
                minTrackingConfidence: 0.5f,
                trackingPoint: HandTrackingPoint.IndexTip,
                smoothingFactor: 0.35f,
                maximumMatchDistance: 0.2f,
                lostFrameTolerance: 1,
                calibrations: new Dictionary<string, CameraCalibration>
                {
                    ["main"] = new CameraCalibration(
                        new Vector2Data(0, 0),
                        new Vector2Data(100, 0),
                        new Vector2Data(100, 100),
                        new Vector2Data(0, 100))
                });
            return new CameraVisionConfigurationStore(this)
                .SaveAsync(configuration, CancellationToken.None);
        }

        public void Dispose() => _data.Dispose();
    }

    private sealed class SingleFrameCameraBackend : ICameraCaptureBackend
    {
        private int _served;
        public bool IsOpen { get; private set; }
        public bool TryOpen(CameraCaptureOptions options) => IsOpen = true;
        public bool TryRead(out CameraFrame? frame)
        {
            if (Interlocked.Exchange(ref _served, 1) != 0)
            {
                frame = null;
                return false;
            }

            frame = new CameraFrame(
                1,
                DateTimeOffset.UtcNow,
                new Mat(100, 100, MatType.CV_8UC3, new Scalar(1, 2, 3)));
            return true;
        }

        public void Close() => IsOpen = false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnavailableCameraBackend : ICameraCaptureBackend
    {
        public bool IsOpen => false;
        public bool TryOpen(CameraCaptureOptions options) => false;
        public bool TryRead(out CameraFrame? frame)
        {
            frame = null;
            return false;
        }

        public void Close() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHandBackend(HandDetectionResult result) : IHandDetectionBackend
    {
        public HandBackendStatus Status { get; private set; } = HandBackendStatus.Uninitialized;
        public Task InitializeAsync(HandDetectionOptions options, CancellationToken cancellationToken)
        {
            Status = HandBackendStatus.Ready;
            return Task.CompletedTask;
        }

        public ValueTask<HandDetectionResult> DetectAsync(
            RgbFrameView frame,
            long timestampUnixMs,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);

        public ValueTask DisposeAsync()
        {
            Status = HandBackendStatus.Disposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingHandBackend(string error) : IHandDetectionBackend
    {
        public HandBackendStatus Status => HandBackendStatus.Faulted;
        public Task InitializeAsync(HandDetectionOptions options, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException(error));
        public ValueTask<HandDetectionResult> DetectAsync(
            RgbFrameView frame,
            long timestampUnixMs,
            CancellationToken cancellationToken) => throw new InvalidOperationException(error);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DisposeObservedBackend(
        IHandDetectionBackend inner,
        Action disposed) : IHandDetectionBackend
    {
        private int _disposed;
        public HandBackendStatus Status => inner.Status;
        public Task InitializeAsync(HandDetectionOptions options, CancellationToken cancellationToken) =>
            inner.InitializeAsync(options, cancellationToken);
        public ValueTask<HandDetectionResult> DetectAsync(
            RgbFrameView frame,
            long timestampUnixMs,
            CancellationToken cancellationToken) =>
            inner.DetectAsync(frame, timestampUnixMs, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await inner.DisposeAsync();
            disposed();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BlazeCameraVisionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
