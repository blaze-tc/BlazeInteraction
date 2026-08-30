using Yuexin.Radar.Protocol;

namespace Yuexin.Radar.Protocol.Tests;

public sealed class RadarByteStreamDecoderTests
{
    private static readonly byte[] ManualSample = [0x30, 0x14, 0x13, 0xAF];

    [Fact]
    public void HalfPacket_IsHeldUntilCompleted()
    {
        var decoder = new RadarByteStreamDecoder();

        Assert.Empty(decoder.Append(ManualSample.AsSpan(0, 2)));
        var decoded = decoder.Append(ManualSample.AsSpan(2, 2));

        Assert.Single(decoded);
        Assert.Equal(40, decoded[0].DistanceCentimeters);
        Assert.Equal(0, decoder.BufferedByteCount);
    }

    [Fact]
    public void StickyPackets_AreBothDecoded()
    {
        var decoder = new RadarByteStreamDecoder();
        var bytes = ManualSample.Concat(ManualSample).ToArray();

        var decoded = decoder.Append(bytes);

        Assert.Equal(2, decoded.Count);
    }

    [Fact]
    public void Misalignment_IsDiscardedOneByteAtATimeUntilValidPacket()
    {
        var decoder = new RadarByteStreamDecoder();
        var bytes = new byte[] { 0xFF, 0x01, 0x02 }.Concat(ManualSample).ToArray();

        var decoded = decoder.Append(bytes);

        Assert.Single(decoded);
        Assert.Equal(3, decoder.DiscardedByteCount);
    }

    [Fact]
    public void CrcFailure_DoesNotLoseFollowingValidPacket()
    {
        var decoder = new RadarByteStreamDecoder();
        var bytes = new byte[] { 0x20, 0x14, 0x13, 0xAF }.Concat(ManualSample).ToArray();

        var decoded = decoder.Append(bytes);

        Assert.Single(decoded);
        Assert.True(decoder.CrcErrorCount >= 1);
    }

    [Fact]
    public void Reset_ClearsPartialPacketAndCountersWhenRequested()
    {
        var decoder = new RadarByteStreamDecoder();
        decoder.Append(ManualSample.AsSpan(0, 3));

        decoder.Reset(resetCounters: true);

        Assert.Equal(0, decoder.BufferedByteCount);
        Assert.Equal(0, decoder.DiscardedByteCount);
        Assert.Equal(0, decoder.CrcErrorCount);
        Assert.Empty(decoder.Append(ManualSample.AsSpan(3, 1)));
    }

    [Fact]
    public void Buffer_IsBoundedUnderGarbageInput()
    {
        var decoder = new RadarByteStreamDecoder(maximumBufferedBytes: 64);

        decoder.Append(Enumerable.Repeat((byte)0xFF, 10_000).ToArray());

        Assert.True(decoder.BufferedByteCount <= 64);
        Assert.True(decoder.DiscardedByteCount >= 9_936);
    }
}
