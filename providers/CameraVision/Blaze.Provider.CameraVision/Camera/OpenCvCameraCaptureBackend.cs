using OpenCvSharp;

namespace Blaze.Provider.CameraVision;

public sealed class OpenCvCameraCaptureBackendFactory : ICameraCaptureBackendFactory
{
    public ICameraCaptureBackend Create() => new OpenCvCameraCaptureBackend();
}

public sealed class OpenCvCameraCaptureBackend : ICameraCaptureBackend
{
    private VideoCapture? _capture;
    private long _sequence;
    private int _disposed;

    public bool IsOpen => _capture?.IsOpened() == true;

    public bool TryOpen(CameraCaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Close();

        var capture = new VideoCapture();
        try
        {
            if (!capture.Open(options.DeviceIndex, VideoCaptureAPIs.DSHOW))
            {
                capture.Release();
                if (!capture.Open(options.DeviceIndex, VideoCaptureAPIs.MSMF))
                {
                    capture.Dispose();
                    return false;
                }
            }

            _ = capture.Set(VideoCaptureProperties.FrameWidth, options.Width);
            _ = capture.Set(VideoCaptureProperties.FrameHeight, options.Height);
            _ = capture.Set(VideoCaptureProperties.Fps, options.FramesPerSecond);
            _capture = capture;
            return true;
        }
        catch (OpenCVException)
        {
            capture.Dispose();
            return false;
        }
    }

    public bool TryRead(out CameraFrame? frame)
    {
        frame = null;
        var capture = _capture;
        if (capture is null || !capture.IsOpened())
        {
            return false;
        }

        var image = new Mat();
        try
        {
            if (!capture.Read(image) || image.Empty())
            {
                image.Dispose();
                return false;
            }

            frame = new CameraFrame(
                Interlocked.Increment(ref _sequence),
                DateTimeOffset.UtcNow,
                image);
            return true;
        }
        catch (OpenCVException)
        {
            image.Dispose();
            return false;
        }
    }

    public bool TryGetActiveMode(out CameraCaptureMode? mode)
    {
        mode = null;
        var capture = _capture;
        if (capture is null || !capture.IsOpened())
        {
            return false;
        }

        try
        {
            var width = (int)Math.Round(capture.Get(VideoCaptureProperties.FrameWidth));
            var height = (int)Math.Round(capture.Get(VideoCaptureProperties.FrameHeight));
            var framesPerSecond = capture.Get(VideoCaptureProperties.Fps);
            if (width <= 0 || height <= 0 ||
                !double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
            {
                return false;
            }

            mode = new CameraCaptureMode(width, height, framesPerSecond);
            return true;
        }
        catch (OpenCVException)
        {
            return false;
        }
    }

    public void Close()
    {
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is null)
        {
            return;
        }

        capture.Release();
        capture.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Close();
        }

        return ValueTask.CompletedTask;
    }
}
