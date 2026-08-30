namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraCapabilityEnumeratorTests
{
    [Fact]
    public async Task EnumerateAsync_ReturnsOnlyVerifiedUniqueModesSortedByResolutionAndRate()
    {
        CameraCaptureMode[] accepted =
        [
            new(1280, 720, 60),
            new(640, 480, 30),
            new(1280, 720, 30)
        ];
        var factory = new CapabilityProbeFactory(accepted);
        var subject = new CameraCapabilityEnumerator(
            factory,
            candidates:
            [
                new CameraCaptureMode(1280, 720, 60),
                new CameraCaptureMode(640, 480, 30),
                new CameraCaptureMode(1280, 720, 30),
                new CameraCaptureMode(1280, 720, 30),
                new CameraCaptureMode(1920, 1080, 60)
            ]);

        var result = await subject.EnumerateAsync(
            new CameraDeviceDescriptor(0, "Camera 0"),
            CancellationToken.None);

        Assert.False(result.UsesFallbackPresets);
        Assert.Null(result.Warning);
        Assert.Equal(
            new CameraCaptureMode[]
            {
                new(640, 480, 30),
                new(1280, 720, 30),
                new(1280, 720, 60)
            },
            result.Modes);
        Assert.Equal(factory.CreatedCount, factory.DisposedCount);
    }

    [Fact]
    public async Task EnumerateAsync_WhenNoModeCanBeVerified_ReturnsFallbackWithWarning()
    {
        var factory = new CapabilityProbeFactory(Array.Empty<CameraCaptureMode>());
        var subject = new CameraCapabilityEnumerator(
            factory,
            candidates: [new CameraCaptureMode(1280, 720, 30)]);

        var result = await subject.EnumerateAsync(
            new CameraDeviceDescriptor(2, "Camera 2"),
            CancellationToken.None);

        Assert.True(result.UsesFallbackPresets);
        Assert.NotEmpty(result.Modes);
        Assert.Contains("兼容预设", result.Warning, StringComparison.Ordinal);
        Assert.Equal(2, result.Device.Index);
        Assert.Equal(factory.CreatedCount, factory.DisposedCount);
    }

    [Fact]
    public async Task EnumerateAsync_ReturnsControlBeforeBlockingModeProbeCompletes()
    {
        var subject = new CameraCapabilityEnumerator(
            new DelayedCapabilityFactory(TimeSpan.FromMilliseconds(300)),
            candidates: [new CameraCaptureMode(640, 480, 30)]);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var enumeration = subject.EnumerateAsync(
            new CameraDeviceDescriptor(0, "Camera 0"),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Capability probing blocked its caller for {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
        await enumeration;
    }

    private sealed class CapabilityProbeFactory(IEnumerable<CameraCaptureMode> acceptedModes)
        : ICameraCaptureBackendFactory
    {
        private readonly HashSet<CameraCaptureMode> _acceptedModes = new(acceptedModes);

        public int CreatedCount { get; private set; }
        public int DisposedCount { get; private set; }

        public ICameraCaptureBackend Create()
        {
            CreatedCount++;
            return new CapabilityProbeBackend(
                _acceptedModes,
                () => DisposedCount++);
        }
    }

    private sealed class CapabilityProbeBackend(
        IReadOnlySet<CameraCaptureMode> acceptedModes,
        Action disposed) : ICameraCaptureBackend
    {
        private CameraCaptureMode? _requested;

        public bool IsOpen { get; private set; }

        public bool TryOpen(CameraCaptureOptions options)
        {
            _requested = new CameraCaptureMode(
                options.Width,
                options.Height,
                options.FramesPerSecond);
            IsOpen = acceptedModes.Contains(_requested);
            return IsOpen;
        }

        public bool TryGetActiveMode(out CameraCaptureMode? mode)
        {
            mode = IsOpen ? _requested : null;
            return mode is not null;
        }

        public bool TryRead(out CameraFrame? frame)
        {
            frame = null;
            return false;
        }

        public void Close() => IsOpen = false;

        public ValueTask DisposeAsync()
        {
            Close();
            disposed();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedCapabilityFactory(TimeSpan delay) : ICameraCaptureBackendFactory
    {
        public ICameraCaptureBackend Create() => new DelayedCapabilityBackend(delay);
    }

    private sealed class DelayedCapabilityBackend(TimeSpan delay) : ICameraCaptureBackend
    {
        public bool IsOpen => false;
        public bool TryOpen(CameraCaptureOptions options)
        {
            Thread.Sleep(delay);
            return false;
        }
        public bool TryGetActiveMode(out CameraCaptureMode? mode) { mode = null; return false; }
        public bool TryRead(out CameraFrame? frame) { frame = null; return false; }
        public void Close() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
