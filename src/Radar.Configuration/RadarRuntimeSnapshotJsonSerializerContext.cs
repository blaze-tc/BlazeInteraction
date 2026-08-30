using System.Text.Json.Serialization;

namespace Yuexin.Radar.Configuration;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RadarScreenConfiguration))]
[JsonSerializable(typeof(RadarSensorConfiguration))]
internal sealed partial class RadarRuntimeSnapshotJsonSerializerContext : JsonSerializerContext;
