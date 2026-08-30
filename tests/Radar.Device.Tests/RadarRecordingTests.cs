using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;
using System.Buffers.Binary;
using System.Text.Json;

namespace Yuexin.Radar.Device.Tests;

public sealed class RadarRecordingTests
{
    [Fact]
    public async Task RecordingHeader_KeepsTheExistingCamelCaseStringEnumJsonShape()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new RadarRecordingWriter(stream, leaveOpen: true))
        {
            await writer.InitializeAsync(new RadarRecordingHeader(
                RadarModel.F20,
                "{\"sensorId\":\"f20\"}",
                "v2",
                DateTimeOffset.FromUnixTimeMilliseconds(1_000)));
        }

        var bytes = stream.ToArray();
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4));
        using var document = JsonDocument.Parse(bytes.AsMemory(12, headerLength));
        var root = document.RootElement;

        Assert.Equal("F20", root.GetProperty("deviceModel").GetString());
        Assert.Equal("{\"sensorId\":\"f20\"}", root.GetProperty("configurationSnapshotJson").GetString());
        Assert.Equal("v2", root.GetProperty("firmwareVersion").GetString());
        Assert.True(root.TryGetProperty("createdAt", out _));
        Assert.False(root.TryGetProperty("DeviceModel", out _));
    }

    [Fact]
    public async Task Recording_RoundTripsHeaderRawBytesAndConnectionState()
    {
        await using var stream = new MemoryStream();
        var header = new RadarRecordingHeader(
            RadarModel.F10,
            "{\"deviceModel\":\"F10\"}",
            "test-firmware",
            DateTimeOffset.FromUnixTimeMilliseconds(1000));

        await using (var writer = new RadarRecordingWriter(stream, leaveOpen: true))
        {
            await writer.InitializeAsync(header);
            await writer.WriteDataAsync(new byte[] { 0x30, 0x14 }, DateTimeOffset.FromUnixTimeMilliseconds(1010));
            await writer.WriteConnectionStateAsync(RadarConnectionState.Connected, DateTimeOffset.FromUnixTimeMilliseconds(1020));
        }

        stream.Position = 0;
        var reader = new RadarRecordingReader(stream, leaveOpen: true);
        var loadedHeader = await reader.ReadHeaderAsync();
        var entries = new List<RadarRecordingEntry>();
        await foreach (var entry in reader.ReadEntriesAsync())
        {
            entries.Add(entry);
        }

        Assert.Equal(RadarModel.F10, loadedHeader.DeviceModel);
        Assert.Equal("test-firmware", loadedHeader.FirmwareVersion);
        Assert.Equal(2, entries.Count);
        Assert.Equal(RadarRecordingEntryType.RawBytes, entries[0].EntryType);
        Assert.Equal([0x30, 0x14], entries[0].Payload);
        Assert.Equal(RadarRecordingEntryType.ConnectionState, entries[1].EntryType);
        Assert.Equal([(byte)RadarConnectionState.Connected], entries[1].Payload);
    }

    [Fact]
    public async Task Reader_RejectsInvalidMagic()
    {
        await using var stream = new MemoryStream(new byte[32]);
        var reader = new RadarRecordingReader(stream, leaveOpen: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadHeaderAsync().AsTask());
    }
}
