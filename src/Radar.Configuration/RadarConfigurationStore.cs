using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Configuration;

public static class RadarConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static RadarAppConfiguration LoadFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return CreateDiagnosticConfiguration("Configuration JSON is missing.");
        return LoadFromBytes(Encoding.UTF8.GetBytes(json));
    }

    public static async Task<RadarAppConfiguration> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return RadarAppConfiguration.CreateDefault();

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var schema = ReadSchemaVersion(bytes);
        if (schema.Kind == SchemaReadKind.Schema2) return DeserializeSchema2(bytes);
        if (schema.Kind != SchemaReadKind.Schema1) return CreateDiagnosticConfiguration(schema.Diagnostic!);

        LegacyRadarAppConfiguration legacy;
        try
        {
            legacy = JsonSerializer.Deserialize<LegacyRadarAppConfiguration>(GetJsonBytes(bytes), JsonOptions) ?? new LegacyRadarAppConfiguration();
        }
        catch (JsonException exception)
        {
            return CreateDiagnosticConfiguration($"Configuration JSON is malformed: {exception.Message}");
        }

        var migrated = MigrateSchemaOne(legacy);
        EnsureSections(migrated);
        var validation = ConfigurationValidator.ValidateAndNormalize(migrated);
        if (!validation.IsValid)
        {
            migrated.LoadWarnings.AddRange(validation.Errors.Select(error => $"Schema 1 migration was not saved: {error}"));
            return migrated;
        }

        var backupPath = CreateSchemaOneBackup(path);
        try
        {
            await SaveAsync(path, migrated, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The original source is still available both at path and in the byte-for-byte backup.
            throw;
        }

        migrated.LoadWarnings.Add($"Schema 1 configuration migrated to Schema 2; original preserved at '{backupPath}'.");
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
        var temporaryPath = Path.Combine(directory!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static string GetDefaultUserConfigurationPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yuexin", "RadarBridge", "config.json");

    private static RadarAppConfiguration LoadFromBytes(byte[] bytes)
    {
        var schema = ReadSchemaVersion(bytes);
        return schema.Kind switch
        {
            SchemaReadKind.Schema1 => MigrateSchemaOne(JsonSerializer.Deserialize<LegacyRadarAppConfiguration>(GetJsonBytes(bytes), JsonOptions) ?? new LegacyRadarAppConfiguration()),
            SchemaReadKind.Schema2 => DeserializeSchema2(bytes),
            _ => CreateDiagnosticConfiguration(schema.Diagnostic!)
        };
    }

    private static RadarAppConfiguration DeserializeSchema2(byte[] bytes)
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<RadarAppConfiguration>(GetJsonBytes(bytes), JsonOptions) ?? RadarAppConfiguration.CreateDefault();
            EnsureSections(configuration);
            ConfigurationValidator.ValidateAndNormalize(configuration);
            return configuration;
        }
        catch (JsonException exception)
        {
            return CreateDiagnosticConfiguration($"Configuration JSON is malformed: {exception.Message}");
        }
    }

    private static SchemaReadResult ReadSchemaVersion(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(GetJsonBytes(bytes));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return SchemaReadResult.Rejected("Configuration root must be an object.");

            var discriminators = document.RootElement.EnumerateObject()
                .Where(property => string.Equals(property.Name, "schemaVersion", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (discriminators.Length == 0) return SchemaReadResult.Rejected("Configuration schemaVersion is missing; the file was not migrated or overwritten.");
            if (discriminators.Length != 1) return SchemaReadResult.Rejected("Configuration schemaVersion is ambiguous; the file was not migrated or overwritten.");
            if (!discriminators[0].Value.TryGetInt32(out var version))
                return SchemaReadResult.Rejected("Configuration schemaVersion must be an integer; the file was not migrated or overwritten.");

            return version switch
            {
                1 => new SchemaReadResult(SchemaReadKind.Schema1, null),
                2 => new SchemaReadResult(SchemaReadKind.Schema2, null),
                _ => SchemaReadResult.Rejected($"Configuration schemaVersion '{version}' is unsupported; the file was not migrated or overwritten.")
            };
        }
        catch (JsonException exception)
        {
            return SchemaReadResult.Rejected($"Configuration JSON is malformed: {exception.Message}");
        }
    }

    private static string CreateSchemaOneBackup(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var baseName = Path.GetFileNameWithoutExtension(path);
        for (var attempt = 0; ; attempt++)
        {
            var suffix = attempt == 0 ? string.Empty : $".{attempt}";
            var backupPath = Path.Combine(directory, $"{baseName}.schema1.{DateTime.UtcNow:yyyyMMddHHmmssfff}{suffix}.bak");
            try
            {
                File.Copy(path, backupPath, overwrite: false);
                return backupPath;
            }
            catch (IOException) when (File.Exists(backupPath))
            {
                // Timestamp collisions are rare but must never overwrite an earlier backup.
            }
        }
    }

    private static byte[] GetJsonBytes(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) return bytes[Encoding.UTF8.Preamble.Length..];
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)) return Encoding.UTF8.GetBytes(Encoding.Unicode.GetString(bytes, Encoding.Unicode.Preamble.Length, bytes.Length - Encoding.Unicode.Preamble.Length));
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble)) return Encoding.UTF8.GetBytes(Encoding.BigEndianUnicode.GetString(bytes, Encoding.BigEndianUnicode.Preamble.Length, bytes.Length - Encoding.BigEndianUnicode.Preamble.Length));
        return bytes;
    }

    private static RadarAppConfiguration MigrateSchemaOne(LegacyRadarAppConfiguration legacy)
    {
        var migrated = new RadarAppConfiguration
        {
            SchemaVersion = 2,
            Ipc = legacy.Ipc,
            Screens = [new RadarScreenConfiguration
            {
                ScreenId = "main",
                UnityDisplayName = "Main",
                IsPrimary = true,
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
        migrated.Screens[0].Tracking = RadarScreenTrackingConfiguration.FromLegacy(legacy.Tracking, migrated.LoadWarnings);
        return migrated;
    }

    private static RadarAppConfiguration CreateDiagnosticConfiguration(string diagnostic)
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.LoadWarnings.Add(diagnostic);
        return configuration;
    }

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

    private enum SchemaReadKind { Schema1, Schema2, Rejected }
    private readonly record struct SchemaReadResult(SchemaReadKind Kind, string? Diagnostic)
    {
        public static SchemaReadResult Rejected(string diagnostic) => new(SchemaReadKind.Rejected, diagnostic);
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
