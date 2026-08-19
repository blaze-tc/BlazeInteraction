namespace Yuexin.Radar.Device;

public sealed class RadarConnectionMetrics
{
    private long _receivedByteCount;
    private long _parsedPointCount;
    private long _crcErrorCount;
    private long _discardedByteCount;
    private long _successfulConnectionCount;

    public long ReceivedByteCount => Interlocked.Read(ref _receivedByteCount);
    public long ParsedPointCount => Interlocked.Read(ref _parsedPointCount);
    public long CrcErrorCount => Interlocked.Read(ref _crcErrorCount);
    public long DiscardedByteCount => Interlocked.Read(ref _discardedByteCount);
    public long SuccessfulConnectionCount => Interlocked.Read(ref _successfulConnectionCount);

    internal void AddReceivedBytes(int count) => Interlocked.Add(ref _receivedByteCount, count);
    internal void AddParsedPoints(int count) => Interlocked.Add(ref _parsedPointCount, count);
    internal void AddCrcErrors(long count) => Interlocked.Add(ref _crcErrorCount, count);
    internal void AddDiscardedBytes(long count) => Interlocked.Add(ref _discardedByteCount, count);
    internal void ConnectionSucceeded() => Interlocked.Increment(ref _successfulConnectionCount);
}
