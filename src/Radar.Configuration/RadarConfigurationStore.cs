using System.Text.Json;
using System.Text.Json.Serialization;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Configuration;

public static class RadarConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static RadarAppConfiguration LoadFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return RadarAppConfiguration.CreateDefault();

        try
        {
            var configuration = ReadSchemaVersion(json) <= 1
                ? MigrateSchemaOne(JsonSerializer.Deserialize<LegacyRadarAppConfiguration>(json, JsonOptions) ?? new())
                : JsonSerializer.Deserialize<RadarAppConfiguration>(json, JsonOptions) ?? RadarAppConfiguration.CreateDefault();
            EnsureSections(configuration);
            ConfigurationValidator.ValidateAndNormalize(configuration);
            return configuration;
        }
        catch (JsonException)
        {
            return RadarAppConfiguration.CreateDefault();
        }
    }

    public static async Task<RadarAppConfiguration> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return RadarAppConfiguration.CreateDefault();

        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (ReadSchemaVersion(json) > 1) return LoadFromJson(json);

        RadarAppConfiguration migrated;
        try
        {
            migrated = MigrateSchemaOne(JsonSerializer.Deserialize<LegacyRadarAppConfiguration>(json, JsonOptions) ?? new());
        }
        catch (JsonException)
        {
            return RadarAppConfiguration.CreateDefault();
        }

        EnsureSections(migrated);
        ConfigurationValidator.ValidateAndNormalize(migrated);
        var backupPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            $"{Path.GetFileNameWithoutExtension(path)}.schema1.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak");
        await File.WriteAllTextAsync(backupPath, json, cancellationToken).ConfigureAwait(false);
        await SaveAsync(path, migrated, cancellationToken).ConfigureAwait(false);
        return migrated;
    }

    public static async Task SaveAsync(string path, RadarAppConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(configuration);

        EnsureSections(configuration);
        var validation = ConfigurationValidator.ValidateAndNormalize(configuration);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(configuration, JsonOptions);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static string GetDefaultUserConfigurationPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yuexin", "RadarBridge", "config.json");

    private static int ReadSchemaVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("schemaVersion", out var value) && value.TryGetInt32(out var version) ? version : 1;
    }

    private static RadarAppConfiguration MigrateSchemaOne(LegacyRadarAppConfiguration legacy) => new()
    {
        SchemaVersion = 2,
        Ipc = legacy.Ipc,
        Screens = [new RadarScreenConfiguration
        {
            ScreenId = "main",
            UnityDisplayName = "Main",
            IsPrimary = true,
            Tracking = RadarScreenTrackingConfiguration.FromLegacy(legacy.Tracking),
            Interaction = legacy.Interaction,
            Sensors = [new RadarSensorConfiguration
            {
                SensorId = "sensor-1",
                Device = legacy.Device,
                Transform = legacy.Transform,
                Range = legacy.Range,
                Clustering = legacy.Clustering,
                Calibration = legacy.Calibration,
                OutputRectPixels = new RadarPixelRect(0, 0, 1920, 1080)
            }]
        }]
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
        options.Converters.Add(new LenientRadarModelJsonConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static void EnsureSections(RadarAppConfiguration configuration)
    {
        configuration.SchemaVersion = 2;
        configuration.Ipc ??= new RadarIpcConfiguration();
        configuration.Screens ??= [];
        foreach (var screen in configuration.Screens)
        {
            screen.Fusion ??= new RadarFusionConfiguration();
            screen.Tracking ??= new RadarScreenTrackingConfiguration();
            screen.Interaction ??= new RadarInteractionConfiguration();
            screen.Sensors ??= [];
            foreach (var sensor in screen.Sensors) EnsureSensorSections(sensor);
        }
    }

    private static void EnsureSensorSections(RadarSensorConfiguration sensor)
    {
        sensor.Device ??= new RadarDeviceConfiguration();
        sensor.Transform ??= new RadarTransformConfiguration();
        sensor.Range ??= new RadarRangeConfiguration();
        sensor.Range.ActivePolygon ??= [];
        sensor.Range.MaskedPolygons ??= [];
        sensor.Range.EdgeDeadZones ??= new RadarEdgeDeadZoneConfiguration();
        sensor.Clustering ??= new RadarClusteringConfiguration();
        sensor.Calibration ??= new RadarCalibrationConfiguration();
        sensor.Calibration.PhysicalCorners ??= [];
        sensor.Calibration.HomographyMatrix ??= [];
        sensor.Calibration.TransformSnapshot ??= new RadarTransformConfiguration();
    }

    private sealed class LenientRadarModelJsonConverter : JsonConverter<RadarModel>
    {
        public override RadarModel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && Enum.TryParse<RadarModel>(reader.GetString(), true, out var model) && Enum.IsDefined(model)) return model;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric) && Enum.IsDefined(typeof(RadarModel), numeric)) return (RadarModel)numeric;
            return RadarModel.F10;
        }

        public override void Write(Utf8JsonWriter writer, RadarModel value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Enum.IsDefined(value) ? value.ToString() : RadarModel.F10.ToString());
    }
}
