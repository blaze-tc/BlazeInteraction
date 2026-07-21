using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Configuration;

public static class RadarConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly object SaveLocksGate = new();
    private static readonly Dictionary<string, SaveLockEntry> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    internal static int ActiveSaveLockCount
    {
        get
        {
            lock (SaveLocksGate) return SaveLocks.Count;
        }
    }

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

        var migrated = DeserializeSchemaOne(bytes);
        if (!migrated.CanPersist) return migrated;

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
        if (!configuration.CanPersist)
            throw new InvalidOperationException("Configuration was rejected during loading and cannot replace its source file. Create a new configuration explicitly before saving.");
        if (!TryEnsureSections(configuration, out var structureDiagnostic))
            throw new InvalidOperationException(structureDiagnostic);

        EnsureSections(configuration);
        var validation = ConfigurationValidator.ValidateAndNormalize(configuration);
        if (!validation.IsValid) throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));

        var targetPath = Path.GetFullPath(path);
        var saveLock = RentSaveLock(targetPath);
        try
        {
            await saveLock.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var directory = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(configuration, JsonOptions);
                var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
                    File.Move(temporaryPath, targetPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            finally
            {
                saveLock.Gate.Release();
            }
        }
        finally
        {
            ReturnSaveLock(targetPath, saveLock);
        }
    }

    public static string GetDefaultUserConfigurationPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yuexin", "RadarBridge", "config.json");

    private static SaveLockEntry RentSaveLock(string targetPath)
    {
        lock (SaveLocksGate)
        {
            if (!SaveLocks.TryGetValue(targetPath, out var entry))
            {
                entry = new SaveLockEntry();
                SaveLocks.Add(targetPath, entry);
            }

            entry.ReferenceCount++;
            return entry;
        }
    }

    private static void ReturnSaveLock(string targetPath, SaveLockEntry entry)
    {
        lock (SaveLocksGate)
        {
            if (--entry.ReferenceCount == 0 && SaveLocks.TryGetValue(targetPath, out var current) && ReferenceEquals(current, entry))
            {
                SaveLocks.Remove(targetPath);
                entry.Gate.Dispose();
            }
        }
    }

    private static RadarAppConfiguration LoadFromBytes(byte[] bytes)
    {
        var schema = ReadSchemaVersion(bytes);
        return schema.Kind switch
        {
            SchemaReadKind.Schema1 => DeserializeSchemaOne(bytes),
            SchemaReadKind.Schema2 => DeserializeSchema2(bytes),
            _ => CreateDiagnosticConfiguration(schema.Diagnostic!)
        };
    }

    private static RadarAppConfiguration DeserializeSchema2(byte[] bytes)
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<RadarAppConfiguration>(GetJsonBytes(bytes), JsonOptions) ?? RadarAppConfiguration.CreateDefault();
            return NormalizeLoadedConfiguration(configuration);
        }
        catch (JsonException exception)
        {
            return CreateDiagnosticConfiguration($"Configuration JSON is malformed: {exception.Message}");
        }
    }

    private static RadarAppConfiguration DeserializeSchemaOne(byte[] bytes)
    {
        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyRadarAppConfiguration>(GetJsonBytes(bytes), JsonOptions) ?? new LegacyRadarAppConfiguration();
            if (!TryValidateLegacySections(legacy, out var diagnostic)) return CreateDiagnosticConfiguration(diagnostic);
            return NormalizeLoadedConfiguration(MigrateSchemaOne(legacy));
        }
        catch (JsonException exception)
        {
            return CreateDiagnosticConfiguration($"Configuration JSON is malformed: {exception.Message}");
        }
    }

    private static RadarAppConfiguration NormalizeLoadedConfiguration(RadarAppConfiguration configuration)
    {
        if (!TryEnsureSections(configuration, out var structureDiagnostic)) return CreateDiagnosticConfiguration(structureDiagnostic);
        EnsureSections(configuration);
        var validation = ConfigurationValidator.ValidateAndNormalize(configuration);
        if (validation.IsValid) return configuration;
        return CreateDiagnosticConfiguration($"Configuration validation failed: {string.Join(" | ", validation.Errors)}");
    }

    private static bool TryValidateLegacySections(LegacyRadarAppConfiguration legacy, out string diagnostic)
    {
        var sections = new (string Path, object? Value)[]
        {
            ("root.device", legacy.Device), ("root.transform", legacy.Transform), ("root.range", legacy.Range),
            ("root.clustering", legacy.Clustering), ("root.tracking", legacy.Tracking), ("root.interaction", legacy.Interaction),
            ("root.ipc", legacy.Ipc), ("root.calibration", legacy.Calibration)
        };
        var missing = sections.FirstOrDefault(section => section.Value is null);
        if (missing.Path is not null)
        {
            diagnostic = $"{missing.Path} must not be null; the file was not migrated or overwritten.";
            return false;
        }
        diagnostic = string.Empty;
        return true;
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
        configuration.PersistenceState = RadarConfigurationPersistenceState.RejectedLoad;
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

    private static bool TryEnsureSections(RadarAppConfiguration configuration, out string diagnostic)
    {
        if (configuration.Ipc is null) return NullSection("root.ipc", out diagnostic);
        if (configuration.Screens is null) return NullSection("root.screens", out diagnostic);
        for (var screenIndex = 0; screenIndex < configuration.Screens.Count; screenIndex++)
        {
            if (configuration.Screens[screenIndex] is not { } screen) return NullSection($"root.screens[{screenIndex}]", out diagnostic);
            var screenPath = $"root.screens[{screenIndex}]";
            if (screen.Fusion is null) return NullSection($"{screenPath}.fusion", out diagnostic);
            if (screen.Tracking is null) return NullSection($"{screenPath}.tracking", out diagnostic);
            if (screen.Interaction is null) return NullSection($"{screenPath}.interaction", out diagnostic);
            if (screen.Sensors is null) return NullSection($"{screenPath}.sensors", out diagnostic);
            for (var sensorIndex = 0; sensorIndex < screen.Sensors.Count; sensorIndex++)
            {
                if (screen.Sensors[sensorIndex] is not { } sensor) return NullSection($"{screenPath}.sensors[{sensorIndex}]", out diagnostic);
                var sensorPath = $"{screenPath}.sensors[{sensorIndex}]";
                if (sensor.Device is null) return NullSection($"{sensorPath}.device", out diagnostic);
                if (sensor.Transform is null) return NullSection($"{sensorPath}.transform", out diagnostic);
                if (sensor.Range is null) return NullSection($"{sensorPath}.range", out diagnostic);
                if (sensor.Clustering is null) return NullSection($"{sensorPath}.clustering", out diagnostic);
                if (sensor.Calibration is null) return NullSection($"{sensorPath}.calibration", out diagnostic);
                if (sensor.Range.ActivePolygon is null) return NullSection($"{sensorPath}.range.activePolygon", out diagnostic);
                if (sensor.Range.MaskedPolygons is null) return NullSection($"{sensorPath}.range.maskedPolygons", out diagnostic);
                for (var polygonIndex = 0; polygonIndex < sensor.Range.MaskedPolygons.Count; polygonIndex++)
                    if (sensor.Range.MaskedPolygons[polygonIndex] is null) return NullSection($"{sensorPath}.range.maskedPolygons[{polygonIndex}]", out diagnostic);
                if (sensor.Range.EdgeDeadZones is null) return NullSection($"{sensorPath}.range.edgeDeadZones", out diagnostic);
                if (sensor.Calibration.PhysicalCorners is null) return NullSection($"{sensorPath}.calibration.physicalCorners", out diagnostic);
                if (sensor.Calibration.HomographyMatrix is null) return NullSection($"{sensorPath}.calibration.homographyMatrix", out diagnostic);
                if (sensor.Calibration.TransformSnapshot is null) return NullSection($"{sensorPath}.calibration.transformSnapshot", out diagnostic);
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool NullSection(string path, out string diagnostic)
    {
        diagnostic = $"{path} must not be null; the file was not migrated or overwritten.";
        return false;
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
    private sealed class SaveLockEntry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }
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
