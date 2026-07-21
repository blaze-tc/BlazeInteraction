using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Ipc;

public static class IpcFrameCodec
{
    public const int LengthPrefixSize = sizeof(int);

    public static byte[] Encode(IpcEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.ProtocolVersion >= 2 && envelope.MessageType == IpcMessageType.PointerFrame)
        {
            throw new InvalidOperationException("PointerFrame is a protocol v1 legacy payload and cannot be published on protocol v2.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, IpcJson.Options);
        var frame = new byte[LengthPrefixSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(LengthPrefixSize));
        return frame;
    }

    public static IpcEnvelope DecodePayload(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<IpcEnvelope>(payload, IpcJson.Options)
                ?? throw new InvalidDataException("IPC payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("IPC payload is not valid JSON.", exception);
        }
    }
}

public sealed class IpcFrameDecoder
{
    private readonly List<byte> _buffer = [];
    private readonly int _maximumPayloadLength;

    public IpcFrameDecoder(int maximumPayloadLength = 4 * 1024 * 1024)
    {
        if (maximumPayloadLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadLength));
        }

        _maximumPayloadLength = maximumPayloadLength;
    }

    public int BufferedByteCount => _buffer.Count;

    public IReadOnlyList<IpcEnvelope> Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            _buffer.Add(value);
        }

        var decoded = new List<IpcEnvelope>();
        while (_buffer.Count >= IpcFrameCodec.LengthPrefixSize)
        {
            var lengthBytes = _buffer.GetRange(0, IpcFrameCodec.LengthPrefixSize).ToArray();
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is <= 0 || length > _maximumPayloadLength)
            {
                _buffer.Clear();
                throw new InvalidDataException($"IPC payload length {length} is outside the allowed range.");
            }

            var frameLength = IpcFrameCodec.LengthPrefixSize + length;
            if (_buffer.Count < frameLength)
            {
                break;
            }

            var payload = _buffer.GetRange(IpcFrameCodec.LengthPrefixSize, length).ToArray();
            _buffer.RemoveRange(0, frameLength);
            decoded.Add(IpcFrameCodec.DecodePayload(payload));
        }

        return decoded;
    }

    public void Reset() => _buffer.Clear();
}

internal static class IpcJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
