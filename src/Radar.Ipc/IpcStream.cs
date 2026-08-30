using System.Buffers.Binary;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Ipc;

public static class IpcStream
{
    public const int DefaultMaximumPayloadLength = 4 * 1024 * 1024;

    public static async ValueTask WriteAsync(
        Stream stream,
        IpcEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = IpcFrameCodec.Encode(envelope);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IpcEnvelope> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default,
        int maximumPayloadLength = DefaultMaximumPayloadLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[IpcFrameCodec.LengthPrefixSize];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 || length > maximumPayloadLength)
        {
            throw new InvalidDataException($"IPC payload length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return IpcFrameCodec.DecodePayload(payload);
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The IPC stream ended inside a frame.");
            }

            offset += count;
        }
    }
}
