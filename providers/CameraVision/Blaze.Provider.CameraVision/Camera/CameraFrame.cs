using OpenCvSharp;

namespace Blaze.Provider.CameraVision;

public sealed class CameraFrame : IDisposable
{
    private Mat? _image;

    public CameraFrame(long sequence, DateTimeOffset timestamp, Mat image)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        ArgumentNullException.ThrowIfNull(image);
        if (image.Empty())
        {
            throw new ArgumentException("Camera frame image cannot be empty.", nameof(image));
        }

        Sequence = sequence;
        Timestamp = timestamp;
        _image = image;
    }

    public long Sequence { get; }
    public DateTimeOffset Timestamp { get; }
    public Mat Image => Volatile.Read(ref _image) ?? throw new ObjectDisposedException(nameof(CameraFrame));
    public int Width => Image.Cols;
    public int Height => Image.Rows;

    public CameraFrame Clone() => new(Sequence, Timestamp, Image.Clone());

    public void Dispose()
    {
        Interlocked.Exchange(ref _image, null)?.Dispose();
    }
}

public static class CameraFrameTransformer
{
    public static CameraFrame Transform(
        CameraFrame source,
        bool mirrorX,
        CameraRotation rotation)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(rotation))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation));
        }

        using var oriented = new Mat();
        if (mirrorX)
        {
            Cv2.Flip(source.Image, oriented, FlipMode.Y);
        }
        else
        {
            source.Image.CopyTo(oriented);
        }

        if (rotation == CameraRotation.Rotate0)
        {
            return new CameraFrame(source.Sequence, source.Timestamp, oriented.Clone());
        }

        var rotated = new Mat();
        try
        {
            Cv2.Rotate(oriented, rotated, rotation switch
            {
                CameraRotation.Rotate90 => RotateFlags.Rotate90Clockwise,
                CameraRotation.Rotate180 => RotateFlags.Rotate180,
                CameraRotation.Rotate270 => RotateFlags.Rotate90Counterclockwise,
                _ => throw new ArgumentOutOfRangeException(nameof(rotation))
            });
            return new CameraFrame(source.Sequence, source.Timestamp, rotated);
        }
        catch
        {
            rotated.Dispose();
            throw;
        }
    }
}
