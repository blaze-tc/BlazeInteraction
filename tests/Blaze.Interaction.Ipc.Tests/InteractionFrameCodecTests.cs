using System.Buffers.Binary;
using System.Text;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Ipc;

namespace Blaze.Interaction.Ipc.Tests;

public sealed class InteractionFrameCodecTests
{
    [Fact]
    public void Protocol_UsesVersionOneAndInteractionPipeName()
    {
        Assert.Equal(1, InteractionIpcProtocol.CurrentVersion);
        Assert.Equal("Blaze.InteractionBridge", InteractionIpcProtocol.PipeName);
    }

    [Fact]
    public void Encode_PrefixesUtf8JsonWithFourByteLittleEndianLength()
    {
        var envelope = InteractionEnvelope.Create(
            InteractionMessageType.Ping,
            7,
            new PingPayload(1234));

        var frame = InteractionFrameCodec.Encode(envelope);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4));
        var json = Encoding.UTF8.GetString(frame, 4, payloadLength);

        Assert.Equal(frame.Length - 4, payloadLength);
        Assert.StartsWith("{\"messageType\":\"Ping\",\"protocolVersion\":1,\"sequence\":7,\"payload\":", json);
    }

    [Fact]
    public void Encode_IsDeterministicForEquivalentMessages()
    {
        var first = InteractionEnvelope.Create(InteractionMessageType.Status, 3, new StatusPayload(
            "ready", "Ready", "Provider is ready.", null, 44));
        var second = InteractionEnvelope.Create(InteractionMessageType.Status, 3, new StatusPayload(
            "ready", "Ready", "Provider is ready.", null, 44));

        Assert.Equal(InteractionFrameCodec.Encode(first), InteractionFrameCodec.Encode(second));
    }

    [Fact]
    public void Decoder_AccumulatesPartialPrefixAndPayloadReads()
    {
        var expected = InteractionEnvelope.Create(InteractionMessageType.Pong, 11, new PongPayload(5678));
        var bytes = InteractionFrameCodec.Encode(expected);
        var decoder = new InteractionFrameDecoder();

        Assert.Empty(decoder.Append(bytes.AsSpan(0, 2)));
        Assert.Empty(decoder.Append(bytes.AsSpan(2, 3)));
        var decoded = Assert.Single(decoder.Append(bytes.AsSpan(5)));

        Assert.Equal(InteractionMessageType.Pong, decoded.MessageType);
        Assert.Equal(5678, decoded.DeserializePayload<PongPayload>().TimestampUnixMs);
        Assert.Equal(0, decoder.BufferedByteCount);
    }

    [Fact]
    public void Decoder_DecodesMultipleFramesFromOneRead()
    {
        var first = InteractionFrameCodec.Encode(InteractionEnvelope.Create(
            InteractionMessageType.Ping, 1, new PingPayload(1)));
        var second = InteractionFrameCodec.Encode(InteractionEnvelope.Create(
            InteractionMessageType.Pong, 2, new PongPayload(2)));
        var bytes = first.Concat(second).ToArray();

        var decoded = new InteractionFrameDecoder().Append(bytes);

        Assert.Collection(
            decoded,
            message => Assert.Equal(InteractionMessageType.Ping, message.MessageType),
            message => Assert.Equal(InteractionMessageType.Pong, message.MessageType));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(17)]
    public void Decoder_RejectsInvalidOrOversizedLengthAndResets(int length)
    {
        var decoder = new InteractionFrameDecoder(maximumPayloadLength: 16);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);

        Assert.Throws<InvalidDataException>(() => decoder.Append(prefix));
        Assert.Equal(0, decoder.BufferedByteCount);
    }

    [Fact]
    public void DecodePayload_RejectsMalformedJson()
    {
        Assert.Throws<InvalidDataException>(() =>
            InteractionFrameCodec.DecodePayload("not-json"u8));
    }

    [Fact]
    public void DecodePayload_RejectsUnknownMessageTypeDeterministically()
    {
        var json = """
            {"messageType":"RadarFrame","protocolVersion":1,"sequence":1,"payload":{}}
            """;

        var error = Assert.Throws<InvalidDataException>(() =>
            InteractionFrameCodec.DecodePayload(Encoding.UTF8.GetBytes(json)));

        Assert.Equal("Interaction IPC payload is not valid protocol JSON.", error.Message);
    }

    [Fact]
    public async Task StreamRead_HandlesPartialReads()
    {
        var expected = InteractionEnvelope.Create(InteractionMessageType.Error, 5,
            new ErrorPayload("bad_request", "Invalid request."));
        await using var stream = new ChunkedReadStream(InteractionFrameCodec.Encode(expected), 1);

        var actual = await InteractionIpcStream.ReadAsync(stream);

        Assert.Equal(InteractionMessageType.Error, actual.MessageType);
        Assert.Equal("bad_request", actual.DeserializePayload<ErrorPayload>().Code);
    }

    [Fact]
    public async Task StreamRead_ThrowsEndOfStreamForPartialPayload()
    {
        var bytes = InteractionFrameCodec.Encode(InteractionEnvelope.Create(
            InteractionMessageType.Ping, 1, new PingPayload(1)));
        await using var stream = new MemoryStream(bytes[..^1]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await InteractionIpcStream.ReadAsync(stream));
    }

    [Fact]
    public async Task StreamRead_ObservesCancellation()
    {
        await using var stream = new NeverCompletingReadStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await InteractionIpcStream.ReadAsync(stream, cancellation.Token));
    }

    [Fact]
    public void AllProtocolMessages_RoundTripWithTypedPayloads()
    {
        var provider = new ProviderReferencePayload("blaze.radar.f10f20", "radar-main");
        var surface = Surface();
        var frame = Frame(9);
        var cases = new (InteractionMessageType Type, object Payload, Type PayloadType)[]
        {
            (InteractionMessageType.Hello, new HelloPayload(42, "2021.3.45f1", "1.0.0", [surface]), typeof(HelloPayload)),
            (InteractionMessageType.HelloAck, new HelloAckPayload("1.0.0", provider, ["multi-surface", "provider-extensions"]), typeof(HelloAckPayload)),
            (InteractionMessageType.InteractionFrame, frame, typeof(InteractionFrame)),
            (InteractionMessageType.Status, new StatusPayload("running", "Running", "Provider running.", provider, 100), typeof(StatusPayload)),
            (InteractionMessageType.ProviderChanged, new ProviderChangedPayload(null, provider, 101), typeof(ProviderChangedPayload)),
            (InteractionMessageType.Ping, new PingPayload(102), typeof(PingPayload)),
            (InteractionMessageType.Pong, new PongPayload(103), typeof(PongPayload)),
            (InteractionMessageType.Shutdown, new ShutdownPayload("Unity exiting."), typeof(ShutdownPayload)),
            (InteractionMessageType.Error, new ErrorPayload("invalid_message", "Invalid message."), typeof(ErrorPayload))
        };

        foreach (var item in cases)
        {
            var encoded = InteractionFrameCodec.Encode(InteractionEnvelope.Create(item.Type, 1, item.Payload));
            var decoded = InteractionFrameCodec.DecodePayload(encoded.AsSpan(4));

            Assert.Equal(item.Type, decoded.MessageType);
            Assert.NotNull(decoded.DeserializePayload(item.PayloadType));
        }
    }

    [Fact]
    public void CameraVisionFrame_UsesTheStandardInteractionFrameMessageWithoutCameraSpecificIpc()
    {
        var frame = new InteractionFrame
        {
            ProviderId = "blaze.camera.vision",
            ProviderInstanceId = "camera-vision-main",
            SurfaceId = "main",
            Sequence = 7,
            TimestampUnixMs = 1234,
            Points =
            [
                new InteractionPoint
                {
                    Id = 1,
                    SurfaceId = "main",
                    ProviderId = "blaze.camera.vision",
                    ProviderInstanceId = "camera-vision-main",
                    SourceId = "fake-visual-detector",
                    Phase = InteractionPhase.Hover,
                    NormalizedPosition = new Vector2Data(0.25f, 0.75f),
                    PixelPosition = new Vector2Data(480f, 270f),
                    Confidence = 1f,
                    TimestampUnixMs = 1234,
                    Fp = [new Vector2Data(10f, 20f), new Vector2Data(30f, 40f), new Vector2Data(10f, 20f)]
                }
            ]
        };

        var encoded = InteractionFrameCodec.Encode(InteractionEnvelope.Create(
            InteractionMessageType.InteractionFrame,
            frame.Sequence,
            frame));
        var decoded = InteractionFrameCodec.DecodePayload(encoded.AsSpan(4));
        var payload = decoded.DeserializePayload<InteractionFrame>();

        Assert.Equal(InteractionMessageType.InteractionFrame, decoded.MessageType);
        Assert.Equal("blaze.camera.vision", payload.ProviderId);
        Assert.Equal("camera-vision-main", payload.ProviderInstanceId);
        Assert.Equal(InteractionPhase.Hover, Assert.Single(payload.Points).Phase);
        Assert.Equal(
            [new Vector2Data(10f, 20f), new Vector2Data(30f, 40f), new Vector2Data(10f, 20f)],
            Assert.Single(payload.Points).Fp);
        Assert.DoesNotContain(
            Enum.GetNames<InteractionMessageType>(),
            name => name.Contains("Camera", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HelloJson_ContainsUnitySdkAndSurfaceTopology()
    {
        var hello = InteractionEnvelope.Create(InteractionMessageType.Hello, 1,
            new HelloPayload(42, "2021.3.45f1", "1.0.0", [Surface()]));

        var json = Encoding.UTF8.GetString(InteractionFrameCodec.Encode(hello).AsSpan(4));

        Assert.Contains("\"unityPid\":42", json);
        Assert.Contains("\"unityVersion\":\"2021.3.45f1\"", json);
        Assert.Contains("\"sdkVersion\":\"1.0.0\"", json);
        Assert.Contains("\"surfaceId\":\"FRONT\"", json);
    }

    [Fact]
    public void HelloAckJson_ContainsActiveProviderAndCapabilities()
    {
        var ack = InteractionEnvelope.Create(InteractionMessageType.HelloAck, 1,
            new HelloAckPayload("1.0.0", new ProviderReferencePayload(
                "blaze.radar.f10f20", "radar-main"), ["multi-surface", "provider-extensions"]));

        var json = Encoding.UTF8.GetString(InteractionFrameCodec.Encode(ack).AsSpan(4));

        Assert.Contains("\"activeProvider\":{\"id\":\"blaze.radar.f10f20\",\"instanceId\":\"radar-main\"}", json);
        Assert.Contains("\"capabilities\":[\"multi-surface\",\"provider-extensions\"]", json);
    }

    private static InteractionSurface Surface() => new()
    {
        SurfaceId = "FRONT",
        Name = "Front",
        LogicalWidth = 1920,
        LogicalHeight = 1080,
        IsPrimary = true,
        Order = 0
    };

    private static InteractionFrame Frame(long sequence) => new()
    {
        ProviderId = "blaze.radar.f10f20",
        ProviderInstanceId = "radar-main",
        SurfaceId = "FRONT",
        Sequence = sequence,
        TimestampUnixMs = 1000 + sequence,
        Points = []
    };

    private sealed class ChunkedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(chunkSize, buffer.Length)], cancellationToken);
        }
    }

    private sealed class NeverCompletingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
