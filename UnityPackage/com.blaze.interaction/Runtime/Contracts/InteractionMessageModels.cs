#nullable disable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Blaze.Interaction
{
    [JsonConverter(typeof(StringEnumConverter))]
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

    [JsonConverter(typeof(StringEnumConverter))]
    public enum InteractionPhase
    {
        Hover = 0,
        Down = 1,
        Move = 2,
        Up = 3,
        Cancel = 4
    }

    [Serializable]
    public sealed class InteractionEnvelope
    {
        [JsonProperty("messageType")]
        public InteractionMessageType MessageType { get; set; }

        [JsonProperty("protocolVersion")]
        public int ProtocolVersion { get; set; } = InteractionIpcProtocol.Version;

        [JsonProperty("sequence")]
        public long Sequence { get; set; }

        [JsonProperty("payload")]
        public JToken Payload { get; set; }

        public T DeserializePayload<T>() where T : class
        {
            if (Payload == null || Payload.Type == JTokenType.Null)
            {
                throw new InvalidOperationException("The Interaction IPC payload is empty.");
            }

            var value = Payload.ToObject<T>(JsonSerializer.Create(InteractionIpcProtocol.SerializerSettings));
            if (value == null)
            {
                throw new InvalidOperationException("The Interaction IPC payload is invalid.");
            }

            return value;
        }
    }

    [Serializable]
    public sealed class InteractionSurface
    {
        [JsonProperty("surfaceId")]
        public string SurfaceId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("logicalWidth")]
        public int LogicalWidth { get; set; }

        [JsonProperty("logicalHeight")]
        public int LogicalHeight { get; set; }

        [JsonProperty("isPrimary")]
        public bool IsPrimary { get; set; }

        [JsonProperty("order")]
        public int Order { get; set; }
    }

    [Serializable]
    public sealed class Vector2Data
    {
        [JsonProperty("x")]
        public float X { get; set; }

        [JsonProperty("y")]
        public float Y { get; set; }
    }

    [Serializable]
    public sealed class InteractionPoint
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("surfaceId")]
        public string SurfaceId { get; set; }

        [JsonProperty("providerId")]
        public string ProviderId { get; set; }

        [JsonProperty("providerInstanceId")]
        public string ProviderInstanceId { get; set; }

        [JsonProperty("sourceId")]
        public string SourceId { get; set; }

        [JsonProperty("phase")]
        public InteractionPhase Phase { get; set; }

        [JsonProperty("normalizedPosition")]
        public Vector2Data NormalizedPosition { get; set; }

        [JsonProperty("pixelPosition")]
        public Vector2Data PixelPosition { get; set; }

        [JsonProperty("confidence")]
        public float Confidence { get; set; }

        [JsonProperty("timestampUnixMs")]
        public long TimestampUnixMs { get; set; }

        [JsonProperty("extensions")]
        public JObject Extensions { get; set; }

        public bool TryGetExtension<T>(string key, out T extension) where T : class
        {
            extension = null;
            if (string.IsNullOrWhiteSpace(key) || Extensions == null)
            {
                return false;
            }

            JToken value;
            if (!Extensions.TryGetValue(key, StringComparison.Ordinal, out value))
            {
                return false;
            }

            try
            {
                extension = value.ToObject<T>();
                return extension != null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    [Serializable]
    public sealed class InteractionFrame
    {
        [JsonProperty("providerId")]
        public string ProviderId { get; set; }

        [JsonProperty("providerInstanceId")]
        public string ProviderInstanceId { get; set; }

        [JsonProperty("surfaceId")]
        public string SurfaceId { get; set; }

        [JsonProperty("sequence")]
        public long Sequence { get; set; }

        [JsonProperty("timestampUnixMs")]
        public long TimestampUnixMs { get; set; }

        [JsonProperty("points")]
        public List<InteractionPoint> Points { get; set; } = new List<InteractionPoint>();
    }

    [Serializable]
    public sealed class HelloPayload
    {
        [JsonProperty("unityPid")]
        public int UnityPid { get; set; }

        [JsonProperty("unityVersion")]
        public string UnityVersion { get; set; }

        [JsonProperty("sdkVersion")]
        public string SdkVersion { get; set; }

        [JsonProperty("surfaces")]
        public List<InteractionSurface> Surfaces { get; set; } = new List<InteractionSurface>();
    }

    [Serializable]
    public sealed class ProviderReferencePayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("instanceId")]
        public string InstanceId { get; set; }
    }

    [Serializable]
    public sealed class HelloAckPayload
    {
        [JsonProperty("bridgeVersion")]
        public string BridgeVersion { get; set; }

        [JsonProperty("activeProvider")]
        public ProviderReferencePayload ActiveProvider { get; set; }

        [JsonProperty("capabilities")]
        public List<string> Capabilities { get; set; } = new List<string>();
    }

    [Serializable]
    public sealed class StatusPayload
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("provider")]
        public ProviderReferencePayload Provider { get; set; }

        [JsonProperty("timestampUnixMs")]
        public long TimestampUnixMs { get; set; }
    }

    [Serializable]
    public sealed class ProviderChangedPayload
    {
        [JsonProperty("previousProvider")]
        public ProviderReferencePayload PreviousProvider { get; set; }

        [JsonProperty("activeProvider")]
        public ProviderReferencePayload ActiveProvider { get; set; }

        [JsonProperty("timestampUnixMs")]
        public long TimestampUnixMs { get; set; }
    }

    [Serializable]
    public sealed class PingPayload
    {
        [JsonProperty("timestampUnixMs")]
        public long TimestampUnixMs { get; set; }
    }

    [Serializable]
    public sealed class PongPayload
    {
        [JsonProperty("timestampUnixMs")]
        public long TimestampUnixMs { get; set; }
    }

    [Serializable]
    public sealed class ShutdownPayload
    {
        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    [Serializable]
    public sealed class ErrorPayload
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public static class InteractionIpcProtocol
    {
        public const int Version = 1;
        public const string PipeName = "Blaze.InteractionBridge";
        public const int MaximumPayloadLength = 4 * 1024 * 1024;

        internal static readonly JsonSerializerSettings SerializerSettings = CreateSerializerSettings();

        public static InteractionEnvelope Create(
            InteractionMessageType messageType,
            long sequence,
            object payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            return new InteractionEnvelope
            {
                MessageType = messageType,
                ProtocolVersion = Version,
                Sequence = sequence,
                Payload = JToken.FromObject(payload, JsonSerializer.Create(SerializerSettings))
            };
        }

        public static string Serialize(InteractionEnvelope envelope)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            return JsonConvert.SerializeObject(envelope, SerializerSettings);
        }

        public static InteractionEnvelope Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new ArgumentException("Interaction IPC JSON is required.", nameof(json));
            }

            var envelope = JsonConvert.DeserializeObject<InteractionEnvelope>(json, SerializerSettings);
            if (envelope == null)
            {
                throw new JsonException("Interaction IPC JSON did not contain an envelope.");
            }

            return envelope;
        }

        private static JsonSerializerSettings CreateSerializerSettings()
        {
            var settings = new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Include
            };
            settings.Converters.Add(new StringEnumConverter());
            return settings;
        }
    }
}
