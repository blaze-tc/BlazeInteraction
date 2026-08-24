using OpenCvSharp;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraCaptureTests
{
    [Fact]
    public async Task Enumerator_ReturnsOnlyAvailableDevicesAndClosesEveryProbe()
    {
        var factory = new ScriptedCaptureFactory(
            new ScriptedCaptureBackend(open: false),
            new ScriptedCaptureBackend(open: true),
            new ScriptedCaptureBackend(open: false));
        var enumerator = new CameraDeviceEnumerator(factory, maximumDeviceCount: 3);

        var devices = await enumerator.EnumerateAsync(CancellationToken.None);

        var device = Assert.Single(devices);
        Assert.Equal(1, device.Index);
        Assert.Equal("Camera 1", device.DisplayName);
        Assert.All(factory.Created, backend => Assert.True(backend.IsDisposed));
    }

    [Fact]
    public async Task Service_UsesRequestedResolutionAndFpsThenReleasesCameraOnStop()
    {
        using var frame = Frame(1);
        var backend = new ScriptedCaptureBackend(open: true, frame.Clone());
        var options = new CameraCaptureOptions
        {
            DeviceIndex = 2,
            Width = 1280,
            Height = 720,
            FramesPerSecond = 60,
            ReconnectDelay = TimeSpan.FromMilliseconds(5)
        };
        await using var service = new CameraCaptureService(
            new ScriptedCaptureFactory(backend),
            options);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => service.Statistics.CapturedFrames == 1);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(options, backend.LastOpenOptions);
        Assert.True(backend.IsDisposed);
        Assert.Equal(CameraCaptureStatus.Stopped, service.Status);
        Assert.Equal(1, service.Statistics.CapturedFrames);
        Assert.Equal(2, service.Statistics.ActualWidth);
        Assert.Equal(2, service.Statistics.ActualHeight);
    }

    [Fact]
    public async Task Service_UnavailableCameraTransitionsDisconnectedAndRetriesWithoutFaulting()
    {
        var unavailable = new ScriptedCaptureBackend(open: false);
        var connected = new ScriptedCaptureBackend(open: true, Frame(2));
        var factory = new ScriptedCaptureFactory(unavailable, connected);
        var statuses = new List<CameraCaptureStatus>();
        await using var service = new CameraCaptureService(
            factory,
            new CameraCaptureOptions { ReconnectDelay = TimeSpan.FromMilliseconds(5) });
        service.StatusChanged += (_, args) => statuses.Add(args.Status);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => service.Statistics.CapturedFrames == 1);
        await service.StopAsync(CancellationToken.None);

        Assert.Contains(CameraCaptureStatus.Disconnected, statuses);
        Assert.Contains(CameraCaptureStatus.Reconnecting, statuses);
        Assert.Contains(CameraCaptureStatus.Connected, statuses);
        Assert.True(service.Statistics.ReconnectAttempts >= 1);
        Assert.True(unavailable.IsDisposed);
        Assert.True(connected.IsDisposed);
    }

    [Fact]
    public async Task Service_ReadDisconnectReconnectsAndDropsReplacedFrames()
    {
        var first = new ScriptedCaptureBackend(open: true, Frame(1), null);
        var second = new ScriptedCaptureBackend(open: true, Frame(2), Frame(3));
        await using var service = new CameraCaptureService(
            new ScriptedCaptureFactory(first, second),
            new CameraCaptureOptions { ReconnectDelay = TimeSpan.FromMilliseconds(5) });

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => service.Statistics.CapturedFrames >= 3);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.Statistics.ReconnectAttempts >= 1);
        Assert.True(service.Statistics.DroppedFrames >= 1);
        Assert.True(first.IsDisposed);
        Assert.True(second.IsDisposed);
    }

    private static CameraFrame Frame(long sequence)
    {
        var image = Mat.Ones(2, 2, MatType.CV_8UC1) * sequence;
        return new CameraFrame(sequence, DateTimeOffset.UtcNow, image);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class ScriptedCaptureFactory(params ScriptedCaptureBackend[] backends)
        : ICameraCaptureBackendFactory
    {
        private readonly Queue<ScriptedCaptureBackend> _backends = new(backends);
        internal List<ScriptedCaptureBackend> Created { get; } = [];

        public ICameraCaptureBackend Create()
        {
            var backend = _backends.Count > 0
                ? _backends.Dequeue()
                : new ScriptedCaptureBackend(open: false);
            Created.Add(backend);
            return backend;
        }
    }

    private sealed class ScriptedCaptureBackend : ICameraCaptureBackend
    {
        private readonly bool _open;
        private readonly Queue<CameraFrame?> _frames;

        internal ScriptedCaptureBackend(bool open, params CameraFrame?[] frames)
        {
            _open = open;
            _frames = new Queue<CameraFrame?>(frames);
        }

        public bool IsOpen { get; private set; }
        public bool IsDisposed { get; private set; }
        public CameraCaptureOptions? LastOpenOptions { get; private set; }

        public bool TryOpen(CameraCaptureOptions options)
        {
            LastOpenOptions = options;
            IsOpen = _open;
            return _open;
        }

        public bool TryRead(out CameraFrame? frame)
        {
            if (_frames.Count == 0)
            {
                frame = null;
                return false;
            }

            frame = _frames.Dequeue();
            return frame is not null;
        }

        public void Close() => IsOpen = false;

        public ValueTask DisposeAsync()
        {
            Close();
            while (_frames.TryDequeue(out var frame))
            {
                frame?.Dispose();
            }

            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
