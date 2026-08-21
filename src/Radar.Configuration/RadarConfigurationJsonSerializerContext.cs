using System.Text.Json.Serialization;

namespace Yuexin.Radar.Configuration;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(RadarAppConfiguration))]
[JsonSerializable(typeof(LegacyRadarAppConfiguration))]
internal sealed partial class RadarConfigurationJsonSerializerContext : JsonSerializerContext;
