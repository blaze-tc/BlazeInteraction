using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.Interaction.Runtime;

namespace Blaze.Provider.CameraVision.Tests;

public sealed class CameraVisionProviderTests
{
    [Fact]
    public void Manifest_IsProviderApi1AndMatchesPluginDescriptor()
    {
        var manifestPath = Path.Combine(ProviderOutputDirectory(), "provider.json");
        var manifest = JsonSerializer.Deserialize<ProviderManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("CameraVision manifest deserialized to null.");
        var plugin = new CameraVisionPlugin();

        Assert.Equal(1, manifest.ProviderApiVersion);
        Assert.Equal(CameraVisionPlugin.ProviderId, manifest.Id);
        Assert.Equal(plugin.Descriptor.Id, manifest.Id);
        Assert.Equal(plugin.Descriptor.DisplayName, manifest.DisplayName);
        Assert.Equal(plugin.Descriptor.Version, Version.Parse(manifest.Version));
        Assert.Equal(plugin.Descriptor.Category, manifest.Category);
        Assert.Equal(
            plugin.Descriptor.Capabilities.Order(StringComparer.Ordinal),
            manifest.Capabilities.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CatalogAndLoader_DiscoverCameraVisionProvider()
    {
        using var directory = new TemporaryDirectory();
        var providerDirectory = Path.Combine(directory.Path, "CameraVision");
        CopyDirectory(ProviderOutputDirectory(), providerDirectory);

        var entry = Assert.Single(new ProviderCatalog().Discover(directory.Path));
        using var loaded = new ProviderLoader().Load(entry);

        Assert.True(entry.IsAvailable, entry.Error);
        Assert.Equal(CameraVisionPlugin.ProviderId, loaded.Plugin.Descriptor.Id);
    }

    [Fact]
    public async Task Lifecycle_InitializeStartStopDisposeTransitionsCleanly()
    {
        var plugin = new CameraVisionPlugin();
        var provider = plugin.CreateProvider(new ProviderCreateContext(
            ProviderOutputDirectory(),
            UnavailableCameraServices.Instance));
        var statuses = new List<ProviderRuntimeStatus>();
        provider.StatusChanged += (_, args) => statuses.Add(args.Status);

        await provider.InitializeAsync(Initialization(), CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);
        await provider.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();

        Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
        Assert.Contains(ProviderRuntimeStatus.Ready, statuses);
        Assert.Contains(ProviderRuntimeStatus.Running, statuses);
        Assert.Contains(ProviderRuntimeStatus.Stopped, statuses);
    }

    [Fact]
    public async Task Lifecycle_RepeatedStartStopFiftyTimesReleasesEachRun()
    {
        var provider = new CameraVisionPlugin().CreateProvider(new ProviderCreateContext(
            ProviderOutputDirectory(),
            UnavailableCameraServices.Instance));
        await provider.InitializeAsync(Initialization(), CancellationToken.None);

        for (var cycle = 0; cycle < 50; cycle++)
        {
            await provider.StartAsync(CancellationToken.None);
            Assert.Equal(ProviderRuntimeStatus.Running, provider.Status);
            await provider.StopAsync(CancellationToken.None);
            Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
        }

        await provider.DisposeAsync();
        await provider.DisposeAsync();
        Assert.Equal(50, ((CameraVisionProvider)provider).CompletedRunCount);
    }

    [Fact]
    public async Task UnavailableCamera_DoesNotFaultAndPublishesStandardFakePointFrame()
    {
        var provider = (CameraVisionProvider)new CameraVisionPlugin().CreateProvider(
            new ProviderCreateContext(
                ProviderOutputDirectory(),
                UnavailableCameraServices.Instance));
        var frameReady = new TaskCompletionSource<InteractionFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        provider.FrameReceived += (_, args) => frameReady.TrySetResult(args.Frame);
        await provider.InitializeAsync(Initialization(), CancellationToken.None);

        await provider.StartAsync(CancellationToken.None);
        var frame = await frameReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var point = Assert.Single(frame.Points);
        Assert.Equal(ProviderRuntimeStatus.Running, provider.Status);
        Assert.Equal(CameraVisionPlugin.ProviderId, frame.ProviderId);
        Assert.Equal(CameraVisionPlugin.DefaultInstanceId, frame.ProviderInstanceId);
        Assert.Equal("main", frame.SurfaceId);
        Assert.True(frame.Sequence > 0);
        Assert.Equal("fake-visual-detector", point.SourceId);
        Assert.Equal(InteractionPhase.Hover, point.Phase);
        Assert.InRange(point.NormalizedPosition.X, 0f, 1f);
        Assert.Equal(.5f, point.NormalizedPosition.Y);
        Assert.Equal(point.NormalizedPosition.X * 1920f, point.PixelPosition.X, 3);
        Assert.Equal(540f, point.PixelPosition.Y);
        Assert.Null(point.Extensions);

        await WaitUntilAsync(() => provider.CameraStatus == CameraCaptureStatus.Disconnected);
        Assert.Equal(ProviderRuntimeStatus.Running, provider.Status);
        await provider.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();
    }

    private static ProviderInitializationContext Initialization() => new(
        [new InteractionSurface
        {
            SurfaceId = "main",
            Name = "Main",
            LogicalWidth = 1920,
            LogicalHeight = 1080,
            IsPrimary = true,
            Order = 0
        }],
        UnavailableCameraServices.Instance);

    private static string ProviderOutputDirectory()
    {
        var directory = Path.GetDirectoryName(typeof(CameraVisionPlugin).Assembly.Location)!;
        Assert.True(File.Exists(Path.Combine(directory, "provider.json")),
            $"CameraVision provider manifest was not copied to {directory}.");
        return directory;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var child in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class UnavailableCameraServices : IServiceProvider, ICameraCaptureBackendFactory
    {
        internal static UnavailableCameraServices Instance { get; } = new();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICameraCaptureBackendFactory) ? this : null;

        public ICameraCaptureBackend Create() => new UnavailableCameraBackend();
    }

    private sealed class UnavailableCameraBackend : ICameraCaptureBackend
    {
        public bool IsOpen => false;
        public bool TryOpen(CameraCaptureOptions options) => false;
        public bool TryRead(out CameraFrame? frame)
        {
            frame = null;
            return false;
        }

        public void Close() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BlazeCameraVisionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
