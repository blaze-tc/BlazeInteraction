using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.Interaction.Runtime;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;

namespace Blaze.Provider.Radar.Tests;

public sealed class RadarProviderLoaderIntegrationTests
{
    [Fact]
    public async Task DotnetPublishProducesAProviderOnlyTreeWithTheCanonicalRadarProfile()
    {
        using var output = new TemporaryDirectory("Blaze.Provider.Radar.Publish.Tests");
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(
            repositoryRoot,
            "providers",
            "Radar",
            "Blaze.Provider.Radar",
            "Blaze.Provider.Radar.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "publish", projectPath, "-c", "Release", "--no-restore", "--nologo", "-o", output.Path
                 })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet publish.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        var diagnostics = (await standardOutput) + Environment.NewLine + (await standardError);

        Assert.True(process.ExitCode == 0, diagnostics);
        Assert.Empty(Directory.EnumerateFiles(output.Path, "*.exe", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(
            output.Path,
            "RadarBridge.runtimeconfig.json",
            SearchOption.AllDirectories));
        Assert.True(File.Exists(Path.Combine(output.Path, "profiles", "radar-default.json")));
    }

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
        using var fixture = new PublishedProviderFixture(useEmptyRadarProfile: true);
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

    [Fact]
    public void InitializedAndStoppedRadarProviderReleasesItsProviderPluginAndLoadContext()
    {
        using var fixture = new PublishedProviderFixture(useEmptyRadarProfile: true);

        var references = CreateInitializeStopDisposeAndUnload(fixture.Root, startProvider: false);
        CollectUntilDead(references.Provider, references.Plugin, references.LoadContext);

        Assert.False(references.Provider.IsAlive);
        Assert.False(references.Plugin.IsAlive);
        Assert.False(references.LoadContext.IsAlive);
    }

    [Fact]
    public void RunningSimulationRadarProviderReleasesItsProviderPluginAndLoadContext()
    {
        using var fixture = new PublishedProviderFixture(useSimulationRadarProfile: true);

        var references = CreateInitializeStopDisposeAndUnload(fixture.Root, startProvider: true);
        CollectUntilDead(references.Provider, references.Plugin, references.LoadContext);

        Assert.False(references.Provider.IsAlive);
        Assert.False(references.Plugin.IsAlive);
        Assert.False(references.LoadContext.IsAlive);
    }

    [Fact]
    public void RunningReplayRadarProviderReleasesItsProviderPluginAndLoadContext()
    {
        using var fixture = new PublishedProviderFixture(useReplayRadarProfile: true);

        var references = CreateInitializeStopDisposeAndUnload(
            fixture.Root,
            startProvider: true,
            allowReplayHeaderRead: true);
        CollectUntilDead(references.Provider, references.Plugin, references.LoadContext);

        Assert.False(references.Provider.IsAlive);
        Assert.False(references.Plugin.IsAlive);
        Assert.False(references.LoadContext.IsAlive);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Provider, WeakReference Plugin, WeakReference LoadContext)
        CreateInitializeStopDisposeAndUnload(
            string providersRoot,
            bool startProvider,
            bool allowReplayHeaderRead = false)
    {
        var entry = Assert.Single(new ProviderCatalog().Discover(providersRoot));
        var loaded = new ProviderLoader().Load(entry);
        var plugin = loaded.Plugin;
        var provider = plugin.CreateProvider(
            new ProviderCreateContext(entry.ProviderDirectory, EmptyServiceProvider.Instance));
        provider.InitializeAsync(
            new ProviderInitializationContext(
            [
                new InteractionSurface
                {
                    SurfaceId = "main",
                    Name = "Main",
                    LogicalWidth = 1920,
                    LogicalHeight = 1080,
                    IsPrimary = true,
                    Order = 0
                }
            ],
            EmptyServiceProvider.Instance),
            CancellationToken.None).GetAwaiter().GetResult();
        if (startProvider)
            provider.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (allowReplayHeaderRead)
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
        provider.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        provider.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var references = (
            new WeakReference(provider),
            new WeakReference(plugin),
            new WeakReference(loaded.LoadContext));
        loaded.Dispose();
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectUntilDead(params WeakReference[] references)
    {
        for (var attempt = 0; attempt < 20 && references.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
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
        public PublishedProviderFixture(
            bool useEmptyRadarProfile = false,
            bool useSimulationRadarProfile = false,
            bool useReplayRadarProfile = false)
        {
            if (new[] { useEmptyRadarProfile, useSimulationRadarProfile, useReplayRadarProfile }.Count(value => value) > 1)
                throw new ArgumentException("Only one test Radar profile may be selected.");
            Root = Path.Combine(Path.GetTempPath(), "Blaze.Provider.Radar.Loader.Tests", Guid.NewGuid().ToString("N"));
            var radarDirectory = Path.Combine(Root, "Radar");
            Directory.CreateDirectory(radarDirectory);
            foreach (var source in Directory.EnumerateFiles(ProviderBuildOutput(), "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(ProviderBuildOutput(), source);
                var destination = Path.Combine(radarDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            if (useEmptyRadarProfile)
            {
                var profilePath = Path.Combine(radarDirectory, "profiles", "radar-default.json");
                Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
                File.WriteAllText(
                    profilePath,
                    """
                    {
                      "schemaVersion": 2,
                      "ipc": {},
                      "screens": [
                        {
                          "screenId": "main",
                          "unityDisplayName": "Main",
                          "isPrimary": true,
                          "sensors": []
                        }
                      ]
                    }
                    """);
            }
            else if (useSimulationRadarProfile)
            {
                var profilePath = Path.Combine(radarDirectory, "profiles", "radar-default.json");
                Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
                File.WriteAllText(
                    profilePath,
                    """
                    {
                      "schemaVersion": 2,
                      "ipc": {},
                      "screens": [
                        {
                          "screenId": "main",
                          "unityDisplayName": "Main",
                          "isPrimary": true,
                          "sensors": [
                            {
                              "sensorId": "sensor-1",
                              "enabled": true,
                              "sourceMode": "simulation",
                              "range": {
                                "activePolygon": [
                                  { "x": -2.5, "y": 2.5 },
                                  { "x": 2.5, "y": 2.5 },
                                  { "x": 2.5, "y": -2.5 },
                                  { "x": -2.5, "y": -2.5 }
                                ]
                              }
                            }
                          ]
                        }
                      ]
                    }
                    """);
            }
            else if (useReplayRadarProfile)
            {
                var replayPath = Path.Combine(radarDirectory, "test-data", "minimal.radarrec");
                Directory.CreateDirectory(Path.GetDirectoryName(replayPath)!);
                CreateMinimalRecording(replayPath);
                var profilePath = Path.Combine(radarDirectory, "profiles", "radar-default.json");
                Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
                var escapedReplayPath = replayPath.Replace("\\", "\\\\", StringComparison.Ordinal);
                File.WriteAllText(
                    profilePath,
                    """
                    {
                      "schemaVersion": 2,
                      "ipc": {},
                      "screens": [
                        {
                          "screenId": "main",
                          "unityDisplayName": "Main",
                          "isPrimary": true,
                          "sensors": [
                            {
                              "sensorId": "sensor-1",
                              "enabled": true,
                              "sourceMode": "replay",
                              "replayFilePath": "__REPLAY_FILE__",
                              "replaySpeed": 1.0,
                              "replayLoop": false,
                              "range": {
                                "activePolygon": [
                                  { "x": -2.5, "y": 2.5 },
                                  { "x": 2.5, "y": 2.5 },
                                  { "x": 2.5, "y": -2.5 },
                                  { "x": -2.5, "y": -2.5 }
                                ]
                              }
                            }
                          ]
                        }
                      ]
                    }
                    """.Replace("__REPLAY_FILE__", escapedReplayPath, StringComparison.Ordinal));
            }
        }

        public string Root { get; }

        private static void CreateMinimalRecording(string path)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var writer = new RadarRecordingWriter(stream, leaveOpen: true);
#pragma warning disable xUnit1031 // Fixture construction intentionally bridges the async recording API.
            writer.InitializeAsync(new RadarRecordingHeader(
                    RadarModel.F10,
                    "{\"sourceMode\":\"replay\"}",
                    "test-firmware",
                    DateTimeOffset.FromUnixTimeMilliseconds(1_000)))
                .AsTask().GetAwaiter().GetResult();
            writer.WriteConnectionStateAsync(
                    RadarConnectionState.Connected,
                    DateTimeOffset.FromUnixTimeMilliseconds(1_010))
                .AsTask().GetAwaiter().GetResult();
            writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore xUnit1031
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string category)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), category, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }

}
