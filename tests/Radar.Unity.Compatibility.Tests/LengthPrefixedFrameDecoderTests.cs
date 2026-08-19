using System.Buffers.Binary;
using System.Text;
using Blaze.Radar.Internal;

namespace Blaze.Radar.Compatibility.Tests;

public sealed class LengthPrefixedFrameDecoderTests
{
    [Fact]
    public void Append_HoldsHalfPacketAndDecodesStickyPackets()
    {
        var first = Frame("{\"messageType\":\"HelloAck\"}");
        var second = Frame("{\"messageType\":\"PointerFrame\"}");
        var decoder = new LengthPrefixedFrameDecoder();

        Assert.Empty(decoder.Append(first, 0, 3));
        var remainder = first.Skip(3).Concat(second).ToArray();
        var payloads = decoder.Append(remainder, 0, remainder.Length);

        Assert.Equal(2, payloads.Count);
        Assert.Contains("HelloAck", Encoding.UTF8.GetString(payloads[0]));
        Assert.Contains("PointerFrame", Encoding.UTF8.GetString(payloads[1]));
        Assert.Equal(0, decoder.BufferedByteCount);
    }

    [Fact]
    public void Append_RejectsNonPositiveAndOversizedLengths()
    {
        var decoder = new LengthPrefixedFrameDecoder(64);
        var zero = new byte[4];
        var oversized = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(oversized, 65);

        Assert.Throws<InvalidDataException>(() => decoder.Append(zero, 0, zero.Length));
        decoder.Reset();
        Assert.Throws<InvalidDataException>(() => decoder.Append(oversized, 0, oversized.Length));
    }

    private static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }
}
