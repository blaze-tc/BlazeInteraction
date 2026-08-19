using System.Text.Json.Serialization;

namespace Yuexin.Radar.Device;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RadarRecordingHeader))]
internal sealed partial class RadarRecordingJsonSerializerContext : JsonSerializerContext;
