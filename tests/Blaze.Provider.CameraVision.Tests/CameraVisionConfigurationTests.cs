using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraVisionConfigurationTests
{
    [Fact]
    public void Defaults_MatchHandMvpAcceptanceValues()
    {
        var configuration = CameraVisionConfiguration.CreateDefault();

        Assert.Equal(2, configuration.SchemaVersion);
        Assert.Equal(1280, configuration.Capture.Width);
        Assert.Equal(720, configuration.Capture.Height);
        Assert.Equal(30d, configuration.Capture.FramesPerSecond);
        Assert.Equal(8, configuration.MaxHands);
        Assert.Equal(HandTrackingPoint.IndexTip, configuration.TrackingPoint);
        Assert.InRange(configuration.MinDetectionConfidence, 0f, 1f);
        Assert.InRange(configuration.MinTrackingConfidence, 0f, 1f);
        Assert.Equal(0.35f, configuration.SmoothingFactor);
        Assert.True(configuration.MaximumMatchDistance > 0f);
        Assert.True(configuration.LostFrameTolerance > 0);
        Assert.Equal(
            new CameraCaptureMode(1280, 720, 30),
            configuration.DeviceProfiles[configuration.Capture.DeviceIndex].Mode);
    }

    [Fact]
    public void Configuration_AllowsThirtyTwoHandsAndSnapshotsCalibrations()
    {
        var calibrations = new Dictionary<string, CameraCalibration>
        {
            ["surface-main"] = SquareCalibration()
        };

        var configuration = Configuration(maxHands: 32, calibrations: calibrations);
        calibrations.Clear();

        Assert.Equal(32, configuration.MaxHands);
        Assert.True(configuration.Calibrations.ContainsKey("surface-main"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Configuration_RejectsNonPositiveMaxHands(int maxHands)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Configuration(maxHands));
    }

    [Fact]
    public void Store_UsesDifferentFilesForDifferentProjectRoots()
    {
        using var firstRoot = new TemporaryDirectory();
        using var secondRoot = new TemporaryDirectory();

        var first = new CameraVisionConfigurationStore(new Storage(firstRoot.Path));
        var second = new CameraVisionConfigurationStore(new Storage(secondRoot.Path));

        Assert.NotEqual(first.ConfigurationPath, second.ConfigurationPath);
        Assert.StartsWith(Path.GetFullPath(firstRoot.Path), first.ConfigurationPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(secondRoot.Path), second.ConfigurationPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FirstLoad_CreatesProjectScopedConfiguration()
    {
        using var root = new TemporaryDirectory();
        var store = new CameraVisionConfigurationStore(new Storage(root.Path));

        var result = await store.LoadOrCreateAsync(CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error);
        Assert.NotNull(result.Configuration);
        Assert.True(File.Exists(store.ConfigurationPath));
        Assert.Equal("camera-vision.json", Path.GetFileName(store.ConfigurationPath));
    }

    [Fact]
    public async Task Save_RoundTripsSettingsAndSurfaceCalibration()
    {
        using var root = new TemporaryDirectory();
        var store = new CameraVisionConfigurationStore(new Storage(root.Path));
        var expected = Configuration(
            maxHands: 32,
            calibrations: new Dictionary<string, CameraCalibration>
            {
                ["surface-a"] = SquareCalibration()
            });

        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadOrCreateAsync(CancellationToken.None);

        Assert.True(loaded.IsSuccess, loaded.Error);
        Assert.Equal(32, loaded.Configuration!.MaxHands);
        Assert.True(loaded.Configuration.Calibrations.ContainsKey("surface-a"));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Path.GetDirectoryName(store.ConfigurationPath)!),
            path => path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Load_MigratesSchema1CaptureIntoSelectedDeviceProfile()
    {
        using var root = new TemporaryDirectory();
        var store = new CameraVisionConfigurationStore(new Storage(root.Path));
        Directory.CreateDirectory(Path.GetDirectoryName(store.ConfigurationPath)!);
        await File.WriteAllTextAsync(store.ConfigurationPath,
            """
            {
              "schemaVersion": 1,
              "capture": {
                "deviceIndex": 2,
                "width": 1280,
                "height": 720,
                "framesPerSecond": 30,
                "mirrorX": true,
                "rotation": "rotate90",
                "reconnectDelay": "00:00:01"
              },
              "maxHands": 8,
              "minDetectionConfidence": 0.5,
              "minTrackingConfidence": 0.5,
              "trackingPoint": "indexTip",
              "smoothingFactor": 0.35,
              "maximumMatchDistance": 0.2,
              "lostFrameTolerance": 2,
              "calibrations": {}
            }
            """);

        var loaded = await store.LoadOrCreateAsync(CancellationToken.None);

        Assert.True(loaded.IsSuccess, loaded.Error);
        Assert.Equal(2, loaded.Configuration!.SchemaVersion);
        var profile = loaded.Configuration.DeviceProfiles[2];
        Assert.Equal(new CameraCaptureMode(1280, 720, 30), profile.Mode);
        Assert.True(profile.MirrorX);
        Assert.Equal(CameraRotation.Rotate90, profile.Rotation);
    }

    [Fact]
    public async Task SaveAndLoad_PreservesIndependentProfilesForTwoDevices()
    {
        using var root = new TemporaryDirectory();
        var store = new CameraVisionConfigurationStore(new Storage(root.Path));
        var profiles = new Dictionary<int, CameraDeviceProfile>
        {
            [0] = new(new CameraCaptureMode(1280, 720, 30), false, CameraRotation.Rotate0),
            [1] = new(new CameraCaptureMode(1920, 1080, 60), true, CameraRotation.Rotate180)
        };
        var configuration = new CameraVisionConfiguration(
            schemaVersion: 2,
            capture: new CameraCaptureOptions(),
            maxHands: 8,
            minDetectionConfidence: 0.5f,
            minTrackingConfidence: 0.5f,
            trackingPoint: HandTrackingPoint.IndexTip,
            smoothingFactor: 0.35f,
            maximumMatchDistance: 0.2f,
            lostFrameTolerance: 2,
            calibrations: null,
            deviceProfiles: profiles);

        await store.SaveAsync(configuration, CancellationToken.None);
        var loaded = await store.LoadOrCreateAsync(CancellationToken.None);

        Assert.True(loaded.IsSuccess, loaded.Error);
        Assert.Equal(2, loaded.Configuration!.DeviceProfiles.Count);
        Assert.Equal(60, loaded.Configuration.DeviceProfiles[1].Mode.FramesPerSecond);
        Assert.True(loaded.Configuration.DeviceProfiles[1].MirrorX);
        Assert.Equal(CameraRotation.Rotate180, loaded.Configuration.DeviceProfiles[1].Rotation);
    }

    [Fact]
    public async Task MalformedJson_IsPreservedAndReportedWithoutOverwrite()
    {
        using var root = new TemporaryDirectory();
        var store = new CameraVisionConfigurationStore(new Storage(root.Path));
        Directory.CreateDirectory(Path.GetDirectoryName(store.ConfigurationPath)!);
        var malformed = "{\"schemaVersion\":}"u8.ToArray();
        await File.WriteAllBytesAsync(store.ConfigurationPath, malformed);

        var result = await store.LoadOrCreateAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Configuration);
        Assert.Contains("invalid", result.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(malformed, await File.ReadAllBytesAsync(store.ConfigurationPath));
    }

    [Fact]
    public async Task Reset_ReplacesExistingConfigurationWithDefaults()
    {
        using var root = new TemporaryDirectory();
        var store = new CameraVisionConfigurationStore(new Storage(root.Path));
        await store.SaveAsync(Configuration(maxHands: 32), CancellationToken.None);

        var reset = await store.ResetAsync(CancellationToken.None);
        var loaded = await store.LoadOrCreateAsync(CancellationToken.None);

        Assert.Equal(8, reset.MaxHands);
        Assert.True(loaded.IsSuccess, loaded.Error);
        Assert.Equal(8, loaded.Configuration!.MaxHands);
    }

    private static CameraVisionConfiguration Configuration(
        int maxHands,
        IReadOnlyDictionary<string, CameraCalibration>? calibrations = null) =>
        new(
            schemaVersion: 1,
            capture: new CameraCaptureOptions(),
            maxHands: maxHands,
            minDetectionConfidence: 0.5f,
            minTrackingConfidence: 0.5f,
            trackingPoint: HandTrackingPoint.IndexTip,
            smoothingFactor: 0.35f,
            maximumMatchDistance: 0.2f,
            lostFrameTolerance: 2,
            calibrations: calibrations);

    private static CameraCalibration SquareCalibration() => new(
        new Vector2Data(0f, 0f),
        new Vector2Data(1280f, 0f),
        new Vector2Data(1280f, 720f),
        new Vector2Data(0f, 720f));

    private sealed record Storage(string DataRoot) : IProviderStorageContext
    {
        public string? ProfilePath => null;

        public string GetProviderDataDirectory(string providerId) =>
            Path.Combine(DataRoot, "Providers", providerId);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Blaze.Provider.CameraVision.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
