using System.Buffers.Binary;
using System.Text;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Ipc;

namespace Yuexin.Radar.Ipc.Tests;

public sealed class IpcFrameCodecTests
{
    [Fact]
    public void LegacyPointerFramePayload_IsNotPubliclyExposedByTheV2ContractAssembly()
    {
        var payloadType = typeof(IpcEnvelope).Assembly.GetType(
            "Yuexin.Radar.Contracts.PointerFramePayload",
            throwOnError: true);

        Assert.False(payloadType!.IsPublic);
    }

    [Fact]
    public void EncodeAndAppend_RoundTripsMultiScreenPointerBatch()
    {
        var batch = new PointerBatchPayload([
            new RadarScreenPointerFrame(
                new RadarScreenInfo("front", "正面", 4096, 1536, true, 1),
                7,
                1000,
                [new RadarScreenPointer(3, RadarPointerPhase.Move, 0.25f, 0.75f, 1024f, 1152f, 0.9f, 1000)])
        ]);

        var bytes = IpcFrameCodec.Encode(IpcEnvelope.Create(IpcMessageType.PointerBatch, 7, batch, 1000));
        var json = Encoding.UTF8.GetString(bytes, sizeof(int), bytes.Length - sizeof(int));
        var decoded = Assert.Single(new IpcFrameDecoder().Append(bytes));
        var result = decoded.DeserializePayload<PointerBatchPayload>();

        Assert.Contains("\"protocolVersion\":2", json);
        Assert.Contains("\"messageType\":\"PointerBatch\"", json);
        Assert.Contains("\"screenId\":\"front\"", json);
        Assert.Equal(2, decoded.ProtocolVersion);
        Assert.Equal("front", Assert.Single(result.Screens).Screen.ScreenId);
        Assert.Equal(1024f, Assert.Single(result.Screens[0].Pointers).PixelX);
    }

    [Fact]
    public void ProtocolVersion_RejectsVersionOneWithExplicitMessage()
    {
        var envelope = IpcEnvelope.Create(IpcMessageType.Hello, 1, new { }, protocolVersion: 1);

        var result = IpcProtocolVersion.Validate(envelope);

        Assert.False(result.IsCompatible);
        Assert.Contains("version 1", result.Error);
        Assert.Contains("version 2", result.Error);
    }

    [Fact]
    public void EncodeAndAppend_RoundTripsEnvelopeAcrossHalfPackets()
    {
        var envelope = IpcEnvelope.Create(
            IpcMessageType.Hello,
            sequence: 7,
            new HelloPayload(1234, "2021.3.45f1", [new RadarScreenDefinitionPayload("main", "Main", 1920, 1080, true, 0)]),
            timestampUnixMilliseconds: 1000);
        var bytes = IpcFrameCodec.Encode(envelope);
        var decoder = new IpcFrameDecoder();

        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Empty(decoder.Append(bytes.AsSpan(0, 3)));
        var decoded = Assert.Single(decoder.Append(bytes.AsSpan(3)));
        var hello = decoded.DeserializePayload<HelloPayload>();

        Assert.Equal(IpcProtocolVersion.Current, decoded.ProtocolVersion);
        Assert.Equal(IpcMessageType.Hello, decoded.MessageType);
        Assert.Equal(7, decoded.Sequence);
        Assert.Equal(1234, hello.UnityProcessId);
        Assert.Equal("main", Assert.Single(hello.Screens).ScreenId);
        Assert.Equal(0, decoder.BufferedByteCount);
    }

    [Fact]
    public void Append_DecodesMultipleStickyMessages()
    {
        var first = IpcFrameCodec.Encode(IpcEnvelope.Create(IpcMessageType.Ping, 1, new PingPayload(10)));
        var second = IpcFrameCodec.Encode(IpcEnvelope.Create(IpcMessageType.Pong, 2, new PongPayload(10)));
        var decoder = new IpcFrameDecoder();

        var decoded = decoder.Append(first.Concat(second).ToArray());

        Assert.Equal([IpcMessageType.Ping, IpcMessageType.Pong], decoded.Select(message => message.MessageType));
    }

    [Fact]
    public void Append_RejectsInvalidLengthBeforeAllocatingPayload()
    {
        var decoder = new IpcFrameDecoder(maximumPayloadLength: 1024);
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, 1025);

        Assert.Throws<InvalidDataException>(() => decoder.Append(length));
    }

    [Fact]
    public void Append_RejectsMalformedJson()
    {
        var decoder = new IpcFrameDecoder();
        var bytes = new byte[7];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 3);
        bytes[4] = (byte)'n';
        bytes[5] = (byte)'o';
        bytes[6] = (byte)'!';

        Assert.Throws<InvalidDataException>(() => decoder.Append(bytes));
    }

    [Fact]
    public void ProtocolVersion_RejectsIncompatiblePeer()
    {
        var envelope = IpcEnvelope.Create(IpcMessageType.Hello, 1, new { }, protocolVersion: 999);

        var result = IpcProtocolVersion.Validate(envelope);

        Assert.False(result.IsCompatible);
        Assert.Contains("999", result.Error);
    }
}
