using System.Text.Json;
using Blaze.Interaction.Contracts;

namespace Blaze.Interaction.Ipc;

public enum InteractionMessageType
{
    Hello = 0,
    HelloAck = 1,
    InteractionFrame = 2,
    Status = 3,
    ProviderChanged = 4,
    Ping = 5,
    Pong = 6,
    Shutdown = 7,
    Error = 8
}

public sealed record InteractionEnvelope
{
    public required InteractionMessageType MessageType { get; init; }

    public required int ProtocolVersion { get; init; }

    public required long Sequence { get; init; }

    public required JsonElement Payload { get; init; }

    public static InteractionEnvelope Create<T>(
        InteractionMessageType messageType,
        long sequence,
        T payload,
        int protocolVersion = InteractionIpcProtocol.CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return new InteractionEnvelope
        {
            MessageType = messageType,
            ProtocolVersion = protocolVersion,
            Sequence = sequence,
            Payload = JsonSerializer.SerializeToElement(payload, InteractionIpcJson.Options)
        };
    }

    public T DeserializePayload<T>()
    {
        try
        {
            return Payload.Deserialize<T>(InteractionIpcJson.Options)
                ?? throw new InvalidDataException($"The {typeof(T).Name} payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The payload is not a valid {typeof(T).Name}.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"The payload violates the {typeof(T).Name} contract.", exception);
        }
    }

    public object? DeserializePayload(Type payloadType)
    {
        ArgumentNullException.ThrowIfNull(payloadType);
        try
        {
            return Payload.Deserialize(payloadType, InteractionIpcJson.Options)
                ?? throw new InvalidDataException($"The {payloadType.Name} payload is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The payload is not a valid {payloadType.Name}.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"The payload violates the {payloadType.Name} contract.", exception);
        }
    }
}

public sealed record HelloPayload(
    int UnityPid,
    string UnityVersion,
    string SdkVersion,
    IReadOnlyList<InteractionSurface> Surfaces);

public sealed record ProviderReferencePayload(string Id, string InstanceId);

public sealed record HelloAckPayload(
    string BridgeVersion,
    ProviderReferencePayload? ActiveProvider,
    IReadOnlyList<string> Capabilities);

public sealed record StatusPayload(
    string Code,
    string State,
    string Message,
    ProviderReferencePayload? Provider,
    long TimestampUnixMs);

public sealed record ProviderChangedPayload(
    ProviderReferencePayload? PreviousProvider,
    ProviderReferencePayload? ActiveProvider,
    long TimestampUnixMs);

public sealed record PingPayload(long TimestampUnixMs);

public sealed record PongPayload(long TimestampUnixMs);

public sealed record ShutdownPayload(string Reason);

public sealed record ErrorPayload(string Code, string Message);

internal static class InteractionIpcJson
{
    internal static readonly JsonSerializerOptions Options = new(InteractionJson.Options);
}
