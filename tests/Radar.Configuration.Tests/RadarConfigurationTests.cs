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
            Assert.False(document.RootElement.TryGetProperty("device", out _));
            Assert.False(document.RootElement.TryGetProperty("tracking", out _));
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
}
