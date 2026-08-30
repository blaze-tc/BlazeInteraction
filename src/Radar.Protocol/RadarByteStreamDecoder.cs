using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Protocol;

public sealed class RadarByteStreamDecoder
{
    private readonly List<byte> _buffer = [];
    private readonly int _maximumBufferedBytes;

    public RadarByteStreamDecoder(int maximumBufferedBytes = 4096)
    {
        if (maximumBufferedBytes < FaseLasePacketParser.PacketLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedBytes));
        }

        _maximumBufferedBytes = maximumBufferedBytes;
    }

    public int BufferedByteCount => _buffer.Count;
    public long DiscardedByteCount { get; private set; }
    public long CrcErrorCount { get; private set; }

    public IReadOnlyList<RadarPoint> Append(ReadOnlySpan<byte> bytes)
    {
        var decoded = new List<RadarPoint>();

        foreach (var value in bytes)
        {
            if (_buffer.Count == _maximumBufferedBytes)
            {
                _buffer.RemoveAt(0);
                DiscardedByteCount++;
            }

            _buffer.Add(value);
            DrainAvailablePackets(decoded);
        }

        return decoded;
    }

    public void Reset(bool resetCounters = false)
    {
        _buffer.Clear();
        if (resetCounters)
        {
            DiscardedByteCount = 0;
            CrcErrorCount = 0;
        }
    }

    private void DrainAvailablePackets(List<RadarPoint> decoded)
    {
        while (_buffer.Count >= FaseLasePacketParser.PacketLength)
        {
            var a = _buffer[0];
            var b = _buffer[1];
            var c = _buffer[2];
            var d = _buffer[3];

            if (!FaseLasePacketParser.HasPacketHighBitPattern(a, b, c, d))
            {
                DiscardOneByte();
                continue;
            }

            if (!FaseLaseCrc.IsValid(a, b, c, d))
            {
                CrcErrorCount++;
                DiscardOneByte();
                continue;
            }

            if (FaseLasePacketParser.TryParse([a, b, c, d], out var point))
            {
                decoded.Add(point);
                _buffer.RemoveRange(0, FaseLasePacketParser.PacketLength);
            }
        }
    }

    private void DiscardOneByte()
    {
        _buffer.RemoveAt(0);
        DiscardedByteCount++;
    }
}
