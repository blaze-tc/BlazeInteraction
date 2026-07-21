using Yuexin.Radar.Configuration;
using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Configuration.Tests;

public sealed class RadarConfigurationTests
{
    [Fact]
    public void NewConfiguration_CreatesOnePrimaryMainScreenAndSensor()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        var screen = Assert.Single(configuration.Screens);
        var sensor = Assert.Single(screen.Sensors);

        Assert.Equal(2, configuration.SchemaVersion);
        Assert.Equal("main", screen.ScreenId);
        Assert.True(screen.IsPrimary);
        Assert.Equal(new RadarPixelRect(0, 0, 1920, 1080), sensor.OutputRectPixels);
        Assert.Equal(RadarModel.F10, sensor.Device.DeviceModel);
        Assert.Equal("Yuexin.RadarBridge", configuration.Ipc.PipeName);
    }

    [Fact]
    public void Validator_ClampsMaximumDistanceToSelectedModel()
    {
        var f10 = RadarAppConfiguration.CreateDefault();
        f10.Range.MaximumDistanceMeters = 99f;

        var f10Result = ConfigurationValidator.ValidateAndNormalize(f10);

        Assert.True(f10Result.IsValid);
        Assert.Equal(10f, f10.Range.MaximumDistanceMeters);

        var f20 = RadarAppConfiguration.CreateDefault();
        f20.Device.DeviceModel = RadarModel.F20;
        f20.Range.MaximumDistanceMeters = 99f;

        var f20Result = ConfigurationValidator.ValidateAndNormalize(f20);

        Assert.True(f20Result.IsValid);
        Assert.Equal(40f, f20.Range.MaximumDistanceMeters);
    }

    [Fact]
    public void LoadFromJson_UnknownModelFallsBackToF10()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "screens": [{
                "screenId": "main",
                "isPrimary": true,
                "sensors": [{
                  "sensorId": "sensor-1",
                  "device": {
                    "deviceModel": "F99",
                    "radarIp": "192.168.0.100",
                    "port": 8487
                  }
                }]
              }]
            }
            """;

        var configuration = RadarConfigurationStore.LoadFromJson(json);

        Assert.Equal(RadarModel.F10, configuration.Screens[0].Sensors[0].Device.DeviceModel);
    }

    [Fact]
    public async Task SaveAndLoad_RetainsF20Selection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadarControl.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var configuration = RadarAppConfiguration.CreateDefault();
            configuration.Device.DeviceModel = RadarModel.F20;
            configuration.Range.MaximumDistanceMeters = 20f;
            configuration.Range.VisualizationRangeMeters = 7.5f;

            await RadarConfigurationStore.SaveAsync(path, configuration);
            var loaded = await RadarConfigurationStore.LoadAsync(path);

            Assert.Equal(RadarModel.F20, loaded.Device.DeviceModel);
            Assert.Equal(20f, loaded.Range.MaximumDistanceMeters);
            Assert.Equal(7.5f, loaded.Range.VisualizationRangeMeters);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Validator_RejectsInvalidEndpointAndRanges()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Device.RadarIp = "not-an-ip";
        configuration.Device.Port = 0;
        configuration.Range.MinimumDistanceMeters = -1f;

        var result = ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("radarIp", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("port", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("minimumDistanceMeters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_SchemaOne_CreatesBackupAndMigratesAllSections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadarControl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                {"schemaVersion":1,"device":{"deviceModel":"F20","radarIp":"10.0.0.8","port":8487},
                 "range":{"minimumDistanceMeters":0.2,"maximumDistanceMeters":20,"visualizationRangeMeters":8},
                 "tracking":{"maximumAssociationDistanceMeters":0.7}}
                """);

            var migrated = await RadarConfigurationStore.LoadAsync(path);

            Assert.True(migrated.CanPersist);
            Assert.Equal(2, migrated.SchemaVersion);
            Assert.Equal(RadarModel.F20, migrated.Screens[0].Sensors[0].Device.DeviceModel);
            Assert.Equal("10.0.0.8", migrated.Screens[0].Sensors[0].Device.RadarIp);
            Assert.Equal(0.7f, migrated.Tracking.MaximumAssociationDistanceMeters);
            Assert.Single(Directory.GetFiles(directory, "config.schema1.*.bak"));
            using var savedDocument = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.False(savedDocument.RootElement.TryGetProperty("device", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validator_RejectsDuplicateIdsAndOutOfBoundsOutputRect()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Screens.Add(configuration.Screens[0].CloneWithId("main"));
        configuration.Screens[0].Sensors[0].OutputRectPixels = new RadarPixelRect(1800, 0, 400, 1080);

        var result = ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, value => value.Contains("duplicate screenId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, value => value.Contains("outputRectPixels", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RequiresExactlyOneAssociatedPrimaryWhenScreensAreAssociated()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Screens[0].IsAssociated = true;
        configuration.Screens.Add(new RadarScreenConfiguration
        {
            ScreenId = "side",
            IsAssociated = true,
            IsPrimary = true,
            Sensors = [new RadarSensorConfiguration { SensorId = "sensor-2" }]
        });

        var result = ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, value => value.Contains("associated primary", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0d)]
    public void Validator_RejectsNonFiniteOrNonPositiveScreenFusionValues(double fusionDistancePixels)
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Screens[0].Fusion.FusionDistancePixels = (float)fusionDistancePixels;

        var result = ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, value => value.Contains("fusionDistancePixels", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_RejectsNonFiniteSensorTransformAndInteractionValues()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Screens[0].Sensors[0].Transform.OffsetXMeters = float.NaN;
        configuration.Screens[0].Interaction.DwellRadiusNormalized = float.PositiveInfinity;

        var result = ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, value => value.Contains("transform", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, value => value.Contains("interaction", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveAsync_WritesOnlySchemaTwoSections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadarControl.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var configuration = RadarAppConfiguration.CreateDefault();
            configuration.Device.DeviceModel = RadarModel.F20;

            await RadarConfigurationStore.SaveAsync(path, configuration);

            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"screens\"", json);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            Assert.False(document.RootElement.TryGetProperty("device", out var ignoredDevice));
            Assert.False(document.RootElement.TryGetProperty("tracking", out var ignoredTracking));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_RetainsCalibrationWhenModelChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadarControl.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var configuration = RadarAppConfiguration.CreateDefault();
            configuration.Calibration = new RadarCalibrationConfiguration
            {
                IsValid = true,
                DeviceModel = RadarModel.F10,
                PhysicalCorners = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)],
                HomographyMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(1000),
                MaximumCornerError = 0.001
            };
            configuration.Device.DeviceModel = RadarModel.F20;

            await RadarConfigurationStore.SaveAsync(path, configuration);
            var loaded = await RadarConfigurationStore.LoadAsync(path);

            Assert.Equal(RadarModel.F20, loaded.Device.DeviceModel);
            Assert.True(loaded.Calibration.IsValid);
            Assert.Equal(RadarModel.F10, loaded.Calibration.DeviceModel);
            Assert.Equal(4, loaded.Calibration.PhysicalCorners.Count);
            Assert.Equal(9, loaded.Calibration.HomographyMatrix.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_CaseInsensitiveSchemaVersion_LoadsSchemaTwoWithoutMigration()
    {
        await WithTemporaryConfigurationAsync(async (directory, path) =>
        {
            var json = """
                {"SchemaVersion":2,"screens":[{"screenId":"preserved","isPrimary":true,
                  "sensors":[{"sensorId":"sensor-a","device":{"radarIp":"10.0.0.9","port":8487}}]}]}
                """;
            await File.WriteAllTextAsync(path, json);

            var loaded = await RadarConfigurationStore.LoadAsync(path);

            Assert.Equal("preserved", Assert.Single(loaded.Screens).ScreenId);
            Assert.Equal("sensor-a", loaded.Screens[0].Sensors[0].SensorId);
            Assert.Empty(Directory.GetFiles(directory, "*.bak"));
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        });
    }

    [Theory]
    [InlineData("{\"schemaVersion\":3,\"screens\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"SchemaVersion\":2}")]
    [InlineData("{\"screens\":[]}")]
    public async Task LoadAsync_UnsupportedOrAmbiguousSchema_PreservesSourceAndReturnsDiagnostic(string json)
    {
        await WithTemporaryConfigurationAsync(async (directory, path) =>
        {
            await File.WriteAllTextAsync(path, json);

            var loaded = await RadarConfigurationStore.LoadAsync(path);

            Assert.Equal(json, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.bak"));
            Assert.NotEmpty(loaded.LoadWarnings);
        });
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_PreservesSourceAndReturnsDiagnostic()
    {
        await WithTemporaryConfigurationAsync(async (directory, path) =>
        {
            const string malformed = "{\"schemaVersion\":2,\"screens\":[";
            await File.WriteAllTextAsync(path, malformed);

            var loaded = await RadarConfigurationStore.LoadAsync(path);

            Assert.Equal(malformed, await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.bak"));
            Assert.Contains(loaded.LoadWarnings, warning => warning.Contains("malformed", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task LoadAsync_SchemaOne_PreservesBackupBytesAndExposesMigrationWarning()
    {
        await WithTemporaryConfigurationAsync(async (directory, path) =>
        {
            var original = System.Text.Encoding.UTF8.GetPreamble()
                .Concat(System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"tracking\":{\"maximumAssociationDistanceMeters\":0.7},\"device\":{\"radarIp\":\"10.0.0.8\",\"port\":8487}}"))
                .ToArray();
            await File.WriteAllBytesAsync(path, original);

            var migrated = await RadarConfigurationStore.LoadAsync(path);

            var backup = Assert.Single(Directory.GetFiles(directory, "config.schema1.*.bak"));
            Assert.Equal(original, await File.ReadAllBytesAsync(backup));
            Assert.Contains(migrated.LoadWarnings, warning => warning.Contains("maximumAssociationDistanceMeters", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task LoadAsync_SchemaOneUtf16Bom_PreservesOriginalBytes()
    {
        await WithTemporaryConfigurationAsync(async (directory, path) =>
        {
            var json = "{\"schemaVersion\":1,\"device\":{\"radarIp\":\"10.0.0.8\",\"port\":8487}}";
            var original = System.Text.Encoding.Unicode.GetPreamble()
                .Concat(System.Text.Encoding.Unicode.GetBytes(json))
                .ToArray();
            await File.WriteAllBytesAsync(path, original);

            var migrated = await RadarConfigurationStore.LoadAsync(path);

            Assert.Equal(2, migrated.SchemaVersion);
            var backup = Assert.Single(Directory.GetFiles(directory, "config.schema1.*.bak"));
            Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        });
    }

    [Fact]
    public void CloneWithId_DeepCopiesLegacyTrackingAndMutableCollections()
    {
        var screen = RadarAppConfiguration.CreateDefault().Screens[0];
        screen.Tracking.MaximumAssociationDistanceMeters = 0.7f;
        screen.Sensors[0].Range.ActivePolygon = [new RadarPoint2(1, 2)];

        var clone = screen.CloneWithId("copy");
        clone.Tracking.MaximumAssociationDistanceMeters = 0.1f;
        clone.Sensors[0].Range.ActivePolygon[0] = new RadarPoint2(3, 4);

        Assert.Equal(0.7f, screen.Tracking.MaximumAssociationDistanceMeters);
        Assert.Equal(new RadarPoint2(1, 2), screen.Sensors[0].Range.ActivePolygon[0]);
        Assert.NotSame(screen.Sensors[0].Range.ActivePolygon, clone.Sensors[0].Range.ActivePolygon);
    }

    [Fact]
    public void Validator_ReportsOwnedPathsForAllNonFinitePersistedSensorValues()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        var screen = configuration.Screens[0];
        screen.ScreenId = "side";
        var sensor = screen.Sensors[0];
        sensor.SensorId = "sensor-2";
        sensor.Range.ActivePolygon = [new RadarPoint2(float.NaN, 0)];
        sensor.Range.MaskedPolygons = [[new RadarPoint2(0, float.PositiveInfinity)]];
        sensor.Calibration.PhysicalCorners = [new RadarPoint2(float.NaN, 0)];
        sensor.Calibration.HomographyMatrix = [double.PositiveInfinity];
        sensor.Calibration.TransformSnapshot.OffsetYMeters = float.NaN;
        sensor.Calibration.MaximumCornerError = double.PositiveInfinity;

        var result = ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.False(result.IsValid);
        Assert.All(result.Errors, error => Assert.Contains("screens[side]", error, StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("sensors[sensor-2]", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("physicalCorners", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("transformSnapshot", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FlatBridge_CreatesMainSensorDelegatesWithoutSerializingSecondState()
    {
        var configuration = new RadarAppConfiguration { Screens = [] };
        configuration.Device = new RadarDeviceConfiguration { RadarIp = "10.0.0.12", Port = 8487 };
        configuration.Tracking = new RadarScreenTrackingConfiguration { MaximumAssociationDistancePixels = 222f };

        await WithTemporaryConfigurationAsync(async (_, path) =>
        {
            await RadarConfigurationStore.SaveAsync(path, configuration);
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));

            var screen = Assert.Single(configuration.Screens);
            Assert.Equal("main", screen.ScreenId);
            Assert.Equal("10.0.0.12", Assert.Single(screen.Sensors).Device.RadarIp);
            Assert.Equal(222f, screen.Tracking.MaximumAssociationDistancePixels);
            Assert.False(document.RootElement.TryGetProperty("device", out var ignoredDevice));
            Assert.False(document.RootElement.TryGetProperty("tracking", out var ignoredTracking));
        });
    }

    [Fact]
    public async Task SaveAsync_ConcurrentSaves_UseUniqueTemporaryFilesAndCleanUp()
    {
        await WithTemporaryConfigurationAsync(async (directory, path) =>
        {
            var saves = Enumerable.Range(0, 12)
                .Select(_ => RadarConfigurationStore.SaveAsync(path, RadarAppConfiguration.CreateDefault()));

            await Task.WhenAll(saves);

            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        });
    }

    [Fact]
    public async Task RejectedLoad_CannotBePersistedAndPreservesOriginalBytes()
    {
        await WithTemporaryConfigurationAsync(async (_, path) =>
        {
            var original = System.Text.Encoding.UTF8.GetBytes("{\"schemaVersion\":99,\"screens\":[]}");
            await File.WriteAllBytesAsync(path, original);

            var rejected = await RadarConfigurationStore.LoadAsync(path);

            Assert.False(rejected.CanPersist);
            await Assert.ThrowsAsync<InvalidOperationException>(() => RadarConfigurationStore.SaveAsync(path, rejected));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        });
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"tracking\":null}", "root.tracking")]
    [InlineData("{\"schemaVersion\":2,\"screens\":[null]}", "root.screens[0]")]
    [InlineData("{\"schemaVersion\":2,\"screens\":[{\"screenId\":\"main\",\"sensors\":[null]}]}", "sensors[0]")]
    [InlineData("{\"schemaVersion\":2,\"screens\":[{\"screenId\":\"main\",\"sensors\":[{\"sensorId\":\"sensor-1\",\"range\":{\"maskedPolygons\":[null]}}]}]}", "maskedPolygons[0]")]
    public async Task LoadAsync_ExplicitNullSections_ReturnsRejectedDiagnosticWithoutOverwriting(string json, string pathFragment)
    {
        await WithTemporaryConfigurationAsync(async (_, path) =>
        {
            await File.WriteAllTextAsync(path, json);

            var rejected = await RadarConfigurationStore.LoadAsync(path);

            Assert.False(rejected.CanPersist);
            Assert.Contains(rejected.LoadWarnings, warning => warning.Contains(pathFragment, StringComparison.Ordinal));
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        });
    }

    [Fact]
    public async Task LoadFromJson_SchemaOneMatchesLoadAsyncNormalization()
    {
        const string json = """
            {"schemaVersion":1,"device":{"deviceModel":"F10","radarIp":"10.0.0.8","port":8487},
             "range":{"minimumDistanceMeters":0.1,"maximumDistanceMeters":99,"visualizationRangeMeters":99}}
            """;
        var fromJson = RadarConfigurationStore.LoadFromJson(json);

        await WithTemporaryConfigurationAsync(async (_, path) =>
        {
            await File.WriteAllTextAsync(path, json);
            var fromFile = await RadarConfigurationStore.LoadAsync(path);

            Assert.Equal(fromFile.Screens[0].Sensors[0].Range.MaximumDistanceMeters, fromJson.Screens[0].Sensors[0].Range.MaximumDistanceMeters);
            Assert.Equal(fromFile.Screens[0].Sensors[0].Range.VisualizationRangeMeters, fromJson.Screens[0].Sensors[0].Range.VisualizationRangeMeters);
            Assert.Equal(fromFile.Screens[0].Tracking.MaximumAssociationDistancePixels, fromJson.Screens[0].Tracking.MaximumAssociationDistancePixels);
            Assert.Equal(fromFile.CanPersist, fromJson.CanPersist);
        });
    }

    [Fact]
    public async Task SaveAsync_MoveFailure_CleansUniqueTemporaryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadarControl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => RadarConfigurationStore.SaveAsync(directory, RadarAppConfiguration.CreateDefault()));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(directory)!, $".{Path.GetFileName(directory)}.*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validator_DuplicateIds_ReportsArrayIndexAndId()
    {
        var configuration = RadarAppConfiguration.CreateDefault();
        configuration.Screens.Add(configuration.Screens[0].CloneWithId("main"));
        configuration.Screens[0].Sensors.Add(new RadarSensorConfiguration { SensorId = "sensor-1" });

        var result = ConfigurationValidator.ValidateAndNormalize(configuration);

        Assert.Contains(result.Errors, error => error.Contains("screens[1](id='main')", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("sensors[1](id='sensor-1')", StringComparison.Ordinal));
    }

    private static async Task WithTemporaryConfigurationAsync(Func<string, string, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadarControl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await action(directory, Path.Combine(directory, "config.json"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
