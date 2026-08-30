using System.Buffers.Binary;
using System.Text.Json;

namespace Blaze.Interaction.Ipc;

public static class InteractionFrameCodec
{
    public static byte[] Encode(InteractionEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, InteractionIpcJson.Options);
        var frame = new byte[InteractionIpcProtocol.LengthPrefixSize + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(InteractionIpcProtocol.LengthPrefixSize));
        return frame;
    }

    public static InteractionEnvelope DecodePayload(ReadOnlySpan<byte> payload)
    {
        try
        {
            return JsonSerializer.Deserialize<InteractionEnvelope>(payload, InteractionIpcJson.Options)
                ?? throw new InvalidDataException("Interaction IPC payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Interaction IPC payload is not valid protocol JSON.", exception);
        }
    }
}

public sealed class InteractionFrameDecoder
{
    private readonly List<byte> _buffer = [];
    private readonly int _maximumPayloadLength;

    public InteractionFrameDecoder(int maximumPayloadLength = InteractionIpcProtocol.DefaultMaximumPayloadLength)
    {
        if (maximumPayloadLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadLength));
        }

        _maximumPayloadLength = maximumPayloadLength;
    }

    public int BufferedByteCount => _buffer.Count;

    public IReadOnlyList<InteractionEnvelope> Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            _buffer.Add(value);
        }

        var decoded = new List<InteractionEnvelope>();
        while (_buffer.Count >= InteractionIpcProtocol.LengthPrefixSize)
        {
            var lengthBytes = _buffer.GetRange(0, InteractionIpcProtocol.LengthPrefixSize).ToArray();
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length <= 0 || length > _maximumPayloadLength)
            {
                _buffer.Clear();
                throw new InvalidDataException($"Interaction IPC payload length {length} is outside the allowed range.");
            }

            var frameLength = InteractionIpcProtocol.LengthPrefixSize + length;
            if (_buffer.Count < frameLength)
            {
                break;
            }

            var payload = _buffer.GetRange(InteractionIpcProtocol.LengthPrefixSize, length).ToArray();
            _buffer.RemoveRange(0, frameLength);
            decoded.Add(InteractionFrameCodec.DecodePayload(payload));
        }

        return decoded;
    }

    public void Reset() => _buffer.Clear();
}

public static class InteractionIpcStream
{
    public static async ValueTask WriteAsync(
        Stream stream,
        InteractionEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = InteractionFrameCodec.Encode(envelope);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<InteractionEnvelope> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default,
        int maximumPayloadLength = InteractionIpcProtocol.DefaultMaximumPayloadLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (maximumPayloadLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadLength));
        }

        var prefix = new byte[InteractionIpcProtocol.LengthPrefixSize];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length <= 0 || length > maximumPayloadLength)
        {
            throw new InvalidDataException($"Interaction IPC payload length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return InteractionFrameCodec.DecodePayload(payload);
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var count = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("Interaction IPC stream ended before the frame was complete.");
            }

            offset += count;
        }
    }
}
