namespace Blaze.Provider.CameraVision;

public sealed class LatestFrameSlot<T> : IDisposable where T : class, IDisposable
{
    private T? _value;
    private long _publishedCount;
    private long _droppedCount;
    private int _disposed;

    public long PublishedCount => Interlocked.Read(ref _publishedCount);
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public void Publish(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Volatile.Read(ref _disposed) != 0)
        {
            value.Dispose();
            throw new ObjectDisposedException(nameof(LatestFrameSlot<T>));
        }

        Interlocked.Increment(ref _publishedCount);
        var replaced = Interlocked.Exchange(ref _value, value);
        if (replaced is not null)
        {
            Interlocked.Increment(ref _droppedCount);
            replaced.Dispose();
        }
    }

    public bool TryTake(out T? value)
    {
        value = Interlocked.Exchange(ref _value, null);
        return value is not null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _value, null)?.Dispose();
    }
}
