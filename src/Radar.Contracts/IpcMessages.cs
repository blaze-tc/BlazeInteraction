using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yuexin.Radar.Contracts;

public enum IpcMessageType
{
    Hello = 1,
    HelloAck = 2,
    Status = 3,
    PointerFrame = 4,
    RawScanFrame = 5,
    ConfigurationChanged = 6,
    Error = 7,
    Ping = 8,
    Pong = 9,
    Shutdown = 10,
    PointerBatch = 11
}

public sealed record IpcEnvelope(
    int ProtocolVersion,
    IpcMessageType MessageType,
    long Sequence,
    long TimestampUnixMilliseconds,
    JsonElement Payload)
{
    public static IpcEnvelope Create<TPayload>(
        IpcMessageType messageType,
        long sequence,
        TPayload payload,
        long? timestampUnixMilliseconds = null,
        int protocolVersion = 2)
    {
        return new IpcEnvelope(
            protocolVersion,
            messageType,
            sequence,
            timestampUnixMilliseconds ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            JsonSerializer.SerializeToElement(payload, IpcContractJson.Options));
    }

    public TPayload DeserializePayload<TPayload>()
    {
        return Payload.Deserialize<TPayload>(IpcContractJson.Options)
            ?? throw new InvalidDataException($"The {MessageType} payload is empty or invalid.");
    }
}

[method: JsonConstructor]
public sealed record HelloPayload(
    int UnityProcessId,
    string UnityVersion,
    IReadOnlyList<RadarScreenDefinitionPayload> Screens)
{
    [JsonIgnore]
    public int ScreenWidth => Screens.FirstOrDefault(screen => screen.IsPrimary)?.DefaultWidthPixels ?? 0;

    [JsonIgnore]
    public int ScreenHeight => Screens.FirstOrDefault(screen => screen.IsPrimary)?.DefaultHeightPixels ?? 0;

    [Obsolete("Temporary build bridge; use the multi-screen constructor.")]
    public HelloPayload(int processId, string unityVersion, int screenWidth, int screenHeight)
        : this(processId, unityVersion, [new("main", "Main", screenWidth, screenHeight, true, 0)]) { }
}

[method: JsonConstructor]
public sealed record HelloAckPayload(
    string BridgeVersion,
    int ProtocolVersion,
    bool Connected,
    string Capability,
    IReadOnlyList<RadarScreenInfo> Screens)
{
    [Obsolete("Temporary build bridge; use the IPC v2 constructor.")]
    public HelloAckPayload(string bridgeVersion, string ignoredDeviceModel, bool connected)
        : this(bridgeVersion, 2, connected, "multi-screen", []) { }
}

public sealed record PointerFramePayload(IReadOnlyList<RadarPointer> Pointers);
public sealed record PointerBatchPayload(IReadOnlyList<RadarScreenPointerFrame> Screens);
public sealed record PingPayload(long ClientTimestampUnixMilliseconds);
public sealed record PongPayload(long ClientTimestampUnixMilliseconds);
public sealed record ErrorPayload(string Code, string Message);
public sealed record ConfigurationChangedPayload(int SchemaVersion, RadarModel DeviceModel);

internal static class IpcContractJson
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
