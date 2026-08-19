using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Device;

public enum RadarRecordingEntryType : byte
{
    RawBytes = 1,
    ConnectionState = 2
}

public sealed record RadarRecordingHeader(
    RadarModel DeviceModel,
    string ConfigurationSnapshotJson,
    string? FirmwareVersion,
    DateTimeOffset CreatedAt);

public sealed record RadarRecordingEntry(
    RadarRecordingEntryType EntryType,
    DateTimeOffset Timestamp,
    byte[] Payload);

public sealed class RadarRecordingWriter : IAsyncDisposable
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("RADREC01");
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public RadarRecordingWriter(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }

    public async ValueTask InitializeAsync(
        RadarRecordingHeader header,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                throw new InvalidOperationException("The recording is already initialized.");
            }

            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, RadarRecordingJson.Options);
            var length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, headerBytes.Length);
            await _stream.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask WriteDataAsync(
        ReadOnlyMemory<byte> bytes,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        return WriteEntryAsync(RadarRecordingEntryType.RawBytes, bytes, timestamp, cancellationToken);
    }

    public ValueTask WriteConnectionStateAsync(
        RadarConnectionState state,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        return WriteEntryAsync(RadarRecordingEntryType.ConnectionState, new[] { (byte)state }, timestamp, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.FlushAsync().ConfigureAwait(false);
        _writeLock.Dispose();
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask WriteEntryAsync(
        RadarRecordingEntryType type,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("InitializeAsync must be called before writing entries.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entryHeader = new byte[13];
            entryHeader[0] = (byte)type;
            BinaryPrimitives.WriteInt64LittleEndian(entryHeader.AsSpan(1, 8), timestamp.ToUnixTimeMilliseconds());
            BinaryPrimitives.WriteInt32LittleEndian(entryHeader.AsSpan(9, 4), payload.Length);
            await _stream.WriteAsync(entryHeader, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    internal static ReadOnlySpan<byte> FileMagic => Magic;
}

public sealed class RadarRecordingReader : IAsyncDisposable
{
    private const int MaximumHeaderLength = 1024 * 1024;
    private const int MaximumEntryLength = 16 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _headerRead;

    public RadarRecordingReader(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }

    public async ValueTask<RadarRecordingHeader> ReadHeaderAsync(CancellationToken cancellationToken = default)
    {
        if (_headerRead)
        {
            throw new InvalidOperationException("The recording header was already read.");
        }

        var magic = new byte[RadarRecordingWriter.FileMagic.Length];
        await ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (!magic.AsSpan().SequenceEqual(RadarRecordingWriter.FileMagic))
        {
            throw new InvalidDataException("The file is not a RadarControl recording.");
        }

        var lengthBytes = new byte[sizeof(int)];
        await ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length is <= 0 or > MaximumHeaderLength)
        {
            throw new InvalidDataException("The recording header length is invalid.");
        }

        var headerBytes = new byte[length];
        await ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        var header = JsonSerializer.Deserialize<RadarRecordingHeader>(headerBytes, RadarRecordingJson.Options)
            ?? throw new InvalidDataException("The recording header is empty.");
        _headerRead = true;
        return header;
    }

    public async IAsyncEnumerable<RadarRecordingEntry> ReadEntriesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_headerRead)
        {
            throw new InvalidOperationException("ReadHeaderAsync must be called before reading entries.");
        }

        var entryHeader = new byte[13];
        while (await TryReadExactlyAsync(entryHeader, cancellationToken).ConfigureAwait(false))
        {
            var type = (RadarRecordingEntryType)entryHeader[0];
            if (!Enum.IsDefined(type))
            {
                throw new InvalidDataException($"Unknown recording entry type {(byte)type}.");
            }

            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                BinaryPrimitives.ReadInt64LittleEndian(entryHeader.AsSpan(1, 8)));
            var length = BinaryPrimitives.ReadInt32LittleEndian(entryHeader.AsSpan(9, 4));
            if (length is < 0 or > MaximumEntryLength)
            {
                throw new InvalidDataException("The recording entry length is invalid.");
            }

            var payload = new byte[length];
            await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            yield return new RadarRecordingEntry(type, timestamp, payload);
        }
    }

    public ValueTask DisposeAsync()
    {
        return _leaveOpen ? ValueTask.CompletedTask : _stream.DisposeAsync();
    }

    private async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await TryReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The recording ended unexpectedly.");
        }
    }

    private async ValueTask<bool> TryReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException("The recording ended inside an entry.");
            }

            offset += count;
        }

        return true;
    }
}

internal static class RadarRecordingJson
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
