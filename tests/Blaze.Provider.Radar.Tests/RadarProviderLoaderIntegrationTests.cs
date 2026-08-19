using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.Interaction.Runtime;

namespace Blaze.Provider.Radar.Tests;

public sealed class RadarProviderLoaderIntegrationTests
{
    [Fact]
    public void BuildOutputContainsManifestButNotTheLegacyRadarExecutable()
    {
        var output = ProviderBuildOutput();

        Assert.True(File.Exists(Path.Combine(output, "provider.json")));
        Assert.False(File.Exists(Path.Combine(output, "RadarBridge.exe")));
        Assert.True(File.Exists(Path.Combine(output, "RadarBridge.dll")));
        Assert.True(File.Exists(Path.Combine(output, "Radar.Contracts.dll")));
    }

    [Fact]
    public void CatalogDiscoversTheExactRadarManifest()
    {
        using var fixture = new PublishedProviderFixture();

        var entry = Assert.Single(new ProviderCatalog().Discover(fixture.Root));

        Assert.True(entry.IsAvailable, entry.Error);
        var manifest = Assert.IsType<ProviderManifest>(entry.Manifest);
        Assert.Equal("blaze.radar.f10f20", manifest.Id);
        Assert.Equal("F10 / F20 激光雷达", manifest.DisplayName);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal(1, manifest.ProviderApiVersion);
        Assert.Equal("Blaze.Provider.Radar.dll", manifest.EntryAssembly);
        Assert.Equal("Blaze.Provider.Radar.RadarPlugin", manifest.EntryType);
        Assert.Equal("Radar", manifest.Category);
        Assert.Equal(
            ["interaction-point", "preview", "multi-sensor", "calibration", "multi-surface"],
            manifest.Capabilities);
    }

    [Fact]
    public async Task LoaderCreatesAndRunsTheRealRadarProviderInsideItsIndependentLoadContext()
    {
        using var fixture = new PublishedProviderFixture();
        var entry = Assert.Single(new ProviderCatalog().Discover(fixture.Root));
        using var loaded = new ProviderLoader().Load(entry);
        var provider = loaded.Plugin.CreateProvider(
            new ProviderCreateContext(entry.ProviderDirectory, EmptyServiceProvider.Instance));

        await provider.InitializeAsync(
            new ProviderInitializationContext(
            [
                new InteractionSurface
                {
                    SurfaceId = "front",
                    Name = "Front",
                    LogicalWidth = 1920,
                    LogicalHeight = 1080,
                    IsPrimary = true,
                    Order = 0
                }
            ],
            EmptyServiceProvider.Instance),
            CancellationToken.None);
        await provider.StartAsync(CancellationToken.None);

        Assert.Equal("blaze.radar.f10f20", loaded.Plugin.Descriptor.Id);
        Assert.Equal("radar-main", provider.ProviderInstanceId);
        Assert.Equal(ProviderRuntimeStatus.Running, provider.Status);
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, loaded.LoadContext);

        await provider.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();
        Assert.Equal(ProviderRuntimeStatus.Stopped, provider.Status);
    }

    private static string ProviderBuildOutput()
    {
        var configuration = new DirectoryInfo(Path.GetDirectoryName(typeof(RadarPlugin).Assembly.Location)!)
            .Parent?.Name ?? throw new InvalidOperationException("Could not determine the current build configuration.");
        var root = FindRepositoryRoot();
        return Path.Combine(
            root,
            "providers",
            "Radar",
            "Blaze.Provider.Radar",
            "bin",
            configuration,
            "net8.0-windows");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private sealed class PublishedProviderFixture : IDisposable
    {
        public PublishedProviderFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "Blaze.Provider.Radar.Loader.Tests", Guid.NewGuid().ToString("N"));
            var radarDirectory = Path.Combine(Root, "Radar");
            Directory.CreateDirectory(radarDirectory);
            foreach (var source in Directory.EnumerateFiles(ProviderBuildOutput()))
                File.Copy(source, Path.Combine(radarDirectory, Path.GetFileName(source)));
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }
}
