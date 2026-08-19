#nullable disable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;

namespace Blaze.Radar
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum RadarIpcMessageType
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

    [JsonConverter(typeof(StringEnumConverter))]
    public enum RadarPointerPhase
    {
        Hover = 0,
        Down = 1,
        Move = 2,
        Up = 3
    }

    [Serializable]
    public sealed class RadarIpcEnvelope
    {
        public int protocolVersion = RadarIpcProtocol.Version;
        public RadarIpcMessageType messageType;
        public long sequence;
        public long timestampUnixMilliseconds;
        public JToken payload;
    }

    [Serializable]
    public sealed class RadarScreenDefinitionPayload
    {
        public string screenId;
        public string name;
        public int defaultWidthPixels;
        public int defaultHeightPixels;
        public bool isPrimary;
        public int order;
    }

    [Serializable]
    public sealed class RadarHelloPayload
    {
        public int unityProcessId;
        public string unityVersion;
        public List<RadarScreenDefinitionPayload> screens = new List<RadarScreenDefinitionPayload>();
    }

    [Serializable]
    public sealed class RadarScreenInfo
    {
        public string screenId;
        public string name;
        public int widthPixels;
        public int heightPixels;
        public bool isPrimary;
        public int order;
    }

    [Serializable]
    public sealed class RadarScreenPointer
    {
        public int pointerId;
        public RadarPointerPhase phase;
        public float normalizedX;
        public float normalizedY;
        public float pixelX;
        public float pixelY;
        public float confidence;
        public long timestampUnixMilliseconds;
    }

    [Serializable]
    public sealed class RadarScreenPointerFrame
    {
        public RadarScreenInfo screen;
        public long sequence;
        public long timestampUnixMilliseconds;
        public List<RadarScreenPointer> pointers = new List<RadarScreenPointer>();
    }

    [Serializable]
    public sealed class RadarPointerBatchPayload
    {
        public List<RadarScreenPointerFrame> screens = new List<RadarScreenPointerFrame>();
    }

    [Serializable]
    public sealed class RadarHelloAckPayload
    {
        public string bridgeVersion;
        public int protocolVersion;
        public bool connected;
        public string capability;
        public List<RadarScreenInfo> screens = new List<RadarScreenInfo>();
    }

    // Kept as the primary-screen adapter for existing SDK consumers.
    [Serializable]
    public sealed class RadarPointerMessage
    {
        public int pointerId;
        public float normalizedX;
        public float normalizedY;
        public RadarPointerPhase phase;
        public float confidence;
        public long timestampUnixMilliseconds;
    }

    // Kept as the primary-screen adapter for existing SDK consumers.
    [Serializable]
    public sealed class RadarPointerFrameMessage
    {
        public long sequence;
        public long timestampUnixMilliseconds;
        public List<RadarPointerMessage> pointers = new List<RadarPointerMessage>();
    }

    [Serializable]
    public sealed class RadarPingPayload
    {
        public long clientTimestampUnixMilliseconds;
    }

    [Serializable]
    public sealed class RadarErrorPayload
    {
        public string code;
        public string message;
    }

    public static class RadarIpcProtocol
    {
        public const int Version = 2;

        public static RadarIpcEnvelope Create(RadarIpcMessageType type, long sequence, object payload)
        {
            if (type == RadarIpcMessageType.PointerFrame)
            {
                throw new InvalidOperationException(
                    "PointerFrame is a protocol v1 legacy payload and cannot be published on protocol v2.");
            }

            return new RadarIpcEnvelope
            {
                protocolVersion = Version,
                messageType = type,
                sequence = sequence,
                timestampUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                payload = payload == null ? JValue.CreateNull() : JToken.FromObject(payload)
            };
        }
    }
}
