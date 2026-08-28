using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Resources;
using System.Security.Cryptography;
using System.Text.Json;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Ipc;
using Blaze.Interaction.Runtime;

namespace Blaze.Interaction.Release.Tests;

public sealed class InteractionPublishLayoutTests
{
    [Fact]
    public async Task PublishScript_ProducesOneBridgeExecutableAndTwoValidatedProviders()
    {
        using var output = new TemporaryDirectory();
        var repositoryRoot = FindRepositoryRoot();
        var script = Path.Combine(repositoryRoot, "scripts", "publish-interaction-bridge.ps1");
        var startInfo = new ProcessStartInfo("powershell")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var argument in new[]
                 {
                     "-ExecutionPolicy", "Bypass", "-File", script,
                     "-Runtime", "win-x64", "-OutputDirectory", output.Path
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start interaction publish script.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        var diagnostics = (await stdout) + Environment.NewLine + (await stderr);

        Assert.True(process.ExitCode == 0, diagnostics);
        Assert.Equal(
            "BlazeInteractionBridge.exe",
            Path.GetFileName(Assert.Single(Directory.EnumerateFiles(
                output.Path,
                "*.exe",
                SearchOption.AllDirectories))));
        Assert.Equal(
            "1.0.0",
            (await File.ReadAllTextAsync(Path.Combine(output.Path, "bridge-version.txt"))).Trim());
        var runtimeConfig = await File.ReadAllTextAsync(
            Path.Combine(output.Path, "BlazeInteractionBridge.runtimeconfig.json"));
        Assert.Contains("includedFrameworks", runtimeConfig, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(output.Path, "hostfxr.dll")));
        Assert.True(File.Exists(Path.Combine(output.Path, "hostpolicy.dll")));
        var radarDirectory = Path.Combine(output.Path, "Providers", "Radar");
        Assert.True(File.Exists(Path.Combine(radarDirectory, "Blaze.Provider.Radar.dll")));
        var providers = new ProviderCatalog().Discover(Path.Combine(output.Path, "Providers"));
        Assert.Equal(2, providers.Count);
        var radar = Assert.Single(providers, entry => entry.Manifest?.Id == "blaze.radar.f10f20");
        Assert.True(radar.IsAvailable, radar.Error);
        Assert.Equal("blaze.radar.f10f20", radar.Manifest!.Id);
        Assert.Equal("1.0.0", radar.Manifest.Version);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(repositoryRoot, "config", "default-profile.json")),
            await File.ReadAllBytesAsync(Path.Combine(radarDirectory, "profiles", "radar-default.json")));

        var cameraDirectory = Path.Combine(output.Path, "Providers", "CameraVision");
        var cameraAssemblyPath = Path.Combine(cameraDirectory, "Blaze.Provider.CameraVision.dll");
        Assert.True(File.Exists(cameraAssemblyPath));
        Assert.True(
            ContainsWpfResource(cameraAssemblyPath, "resources/interactionconsoletheme.baml"),
            "CameraVision publish must embed the shared Interaction console theme.");
        Assert.True(File.Exists(Path.Combine(cameraDirectory, "provider.json")));
        Assert.True(File.Exists(Path.Combine(
            cameraDirectory,
            "profiles",
            "camera-vision-default.json")));
        Assert.True(File.Exists(Path.Combine(
            cameraDirectory,
            "models",
            "hand_landmarker.task")));
        Assert.True(File.Exists(Path.Combine(
            cameraDirectory,
            "runtimes",
            "win-x64",
            "native",
            "Blaze.HandTracking.Native.dll")));
        var provenancePath = Path.Combine(repositoryRoot, "eng", "mediapipe-hand.json");
        using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(provenancePath));
        var expectedModelHash = provenance.RootElement.GetProperty("modelSha256").GetString();
        var expectedNativeHash = provenance.RootElement.GetProperty("nativeDllSha256").GetString();
        var expectedAbiVersion = provenance.RootElement.GetProperty("abiVersion").GetInt32();
        var runtimeManifestPath = Path.Combine(cameraDirectory, "hand-runtime.json");
        Assert.True(File.Exists(runtimeManifestPath), "CameraVision hand-runtime.json");
        using var runtimeManifest = JsonDocument.Parse(await File.ReadAllTextAsync(runtimeManifestPath));
        Assert.Equal(1, runtimeManifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(expectedAbiVersion, runtimeManifest.RootElement.GetProperty("abiVersion").GetInt32());
        Assert.Equal(
            expectedNativeHash,
            runtimeManifest.RootElement.GetProperty("nativeLibrary").GetProperty("sha256").GetString());
        Assert.Equal(
            expectedModelHash,
            runtimeManifest.RootElement.GetProperty("model").GetProperty("sha256").GetString());
        Assert.Equal(
            expectedNativeHash,
            Sha256(Path.Combine(cameraDirectory,
                runtimeManifest.RootElement.GetProperty("nativeLibrary").GetProperty("path").GetString()!)));
        Assert.Equal(
            expectedModelHash,
            Sha256(Path.Combine(cameraDirectory,
                runtimeManifest.RootElement.GetProperty("model").GetProperty("path").GetString()!)));
        foreach (var forbiddenName in new[] { "python", "CameraWorker", "MediaPipeWorker", "ProviderHost" })
        {
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(cameraDirectory, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains(forbiddenName, StringComparison.OrdinalIgnoreCase));
        }
        Assert.Contains(
            Directory.EnumerateFiles(cameraDirectory, "*.dll", SearchOption.AllDirectories),
            path => string.Equals(
                Path.GetFileName(path),
                "OpenCvSharpExtern.dll",
                StringComparison.OrdinalIgnoreCase));
        var camera = Assert.Single(providers, entry => entry.Manifest?.Id == "blaze.camera.vision");
        Assert.True(camera.IsAvailable, camera.Error);
        Assert.Equal(1, camera.Manifest!.ProviderApiVersion);
        Assert.Equal("Blaze.Provider.CameraVision.dll", camera.Manifest.EntryAssembly);

        await ConfigureRadarSimulationAsync(radarDirectory);
        var dataRoot = output.CreateUniqueChildDirectory("Data");
        await SmokePublishedBridgeAsync(
            output.Path,
            dataRoot,
            expectedProviderId: "blaze.radar.f10f20",
            expectedInstanceId: "radar-main",
            selectedProviderId: "blaze.radar.f10f20");
        await SmokePublishedBridgeAsync(
            output.Path,
            dataRoot,
            expectedProviderId: "blaze.camera.vision",
            expectedInstanceId: "camera-vision-main",
            selectedProviderId: "blaze.camera.vision",
            expectInteractionFrame: false);
    }

    private static Task ConfigureRadarSimulationAsync(string radarDirectory) =>
        File.WriteAllTextAsync(
            Path.Combine(radarDirectory, "profiles", "radar-default.json"),
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

    private static async Task SmokePublishedBridgeAsync(
        string publishRoot,
        string dataRoot,
        string expectedProviderId,
        string expectedInstanceId,
        string? selectedProviderId = null,
        bool expectInteractionFrame = true)
    {
        var pipeName = $"Blaze.InteractionBridge.Release.{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo(Path.Combine(publishRoot, "BlazeInteractionBridge.exe"))
        {
            WorkingDirectory = publishRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "--parent-pid", Environment.ProcessId.ToString(),
                     "--minimized",
                     "--pipe-name", pipeName,
                     "--data-root", dataRoot
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (selectedProviderId is not null)
        {
            startInfo.ArgumentList.Add("--provider");
            startInfo.ArgumentList.Add(selectedProviderId);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the published interaction bridge.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Exception? smokeFailure = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            var surface = new InteractionSurface
            {
                SurfaceId = "main",
                Name = "Main",
                LogicalWidth = 1920,
                LogicalHeight = 1080,
                IsPrimary = true,
                Order = 0
            };
            await InteractionIpcStream.WriteAsync(
                client,
                InteractionEnvelope.Create(
                    InteractionMessageType.Hello,
                    1,
                    new HelloPayload(
                        Environment.ProcessId,
                        "2021.3.45f1",
                        "1.0.0",
                        [surface])),
                timeout.Token);

            var acknowledgement = await InteractionIpcStream.ReadAsync(client, timeout.Token);
            if (acknowledgement.MessageType == InteractionMessageType.Error)
            {
                var error = acknowledgement.DeserializePayload<ErrorPayload>();
                throw new InvalidOperationException(
                    $"Bridge rejected Hello with {error.Code}: {error.Message}");
            }

            Assert.Equal(InteractionMessageType.HelloAck, acknowledgement.MessageType);
            Assert.Equal(
                expectedProviderId,
                acknowledgement.DeserializePayload<HelloAckPayload>().ActiveProvider!.Id);

            if (!expectInteractionFrame)
            {
                return;
            }

            InteractionEnvelope frame;
            do
            {
                frame = await InteractionIpcStream.ReadAsync(client, timeout.Token);
            }
            while (frame.MessageType != InteractionMessageType.InteractionFrame);

            var payload = frame.DeserializePayload<InteractionFrame>();
            Assert.Equal(expectedProviderId, payload.ProviderId);
            Assert.Equal(expectedInstanceId, payload.ProviderInstanceId);
            Assert.Equal("main", payload.SurfaceId);
        }
        catch (Exception exception)
        {
            smokeFailure = exception;
        }
        finally
        {
            if (!process.HasExited)
            {
                _ = process.CloseMainWindow();
                using var graceful = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(graceful.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }

        if (smokeFailure is not null)
        {
            var exitCode = process.HasExited ? process.ExitCode.ToString() : "still running";
            var arguments = string.Join(" ", startInfo.ArgumentList);
            throw new InvalidOperationException(
                $"Published Bridge smoke failed. ExitCode={exitCode}; Arguments={arguments}; " +
                $"StandardOutput={await stdout}; StandardError={await stderr}",
                smokeFailure);
        }
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static bool ContainsWpfResource(string assemblyPath, string resourcePath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();
        var resourceDirectory = peReader.PEHeaders.CorHeader?.ResourcesDirectory
            ?? throw new InvalidDataException("Assembly has no managed resource directory.");
        var resourceBlock = peReader.GetSectionData(resourceDirectory.RelativeVirtualAddress);
        foreach (var handle in metadata.ManifestResources)
        {
            var manifestResource = metadata.GetManifestResource(handle);
            if (!manifestResource.Implementation.IsNil ||
                !metadata.GetString(manifestResource.Name)
                    .EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var offset = checked((int)manifestResource.Offset);
            var lengthPrefix = resourceBlock.GetContent(offset, sizeof(int));
            var length = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix.AsSpan());
            var payload = resourceBlock.GetContent(offset + sizeof(int), length).ToArray();
            using var payloadStream = new MemoryStream(payload, writable: false);
            using var resources = new ResourceReader(payloadStream);
            return resources.Cast<DictionaryEntry>().Any(entry => string.Equals(
                entry.Key?.ToString(),
                resourcePath,
                StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BlazeInteraction.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private static readonly string ApprovedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "BlazeInteractionReleaseTests"));

        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                ApprovedRoot,
                Guid.NewGuid().ToString("N"));
            AssertOwnedRoot(Path);
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string CreateUniqueChildDirectory(string prefix)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
            var child = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                Path,
                $"{prefix}-{Guid.NewGuid():N}"));
            AssertOwnedChild(child);
            Directory.CreateDirectory(child);
            return child;
        }

        public void Dispose()
        {
            AssertOwnedRoot(Path);
            const int maximumAttempts = 10;
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return;
                }
                catch (Exception exception) when (
                    attempt < maximumAttempts &&
                    exception is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(attempt * 100));
                }
            }
        }

        private static void AssertOwnedRoot(string path)
        {
            var actual = System.IO.Path.GetFullPath(path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var parent = System.IO.Path.GetDirectoryName(actual);
            var leaf = System.IO.Path.GetFileName(actual);
            if (!string.Equals(parent, ApprovedRoot, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(leaf, "N", out _))
            {
                throw new InvalidOperationException(
                    $"Release-test cleanup target is outside the approved root: {actual}");
            }
        }

        private void AssertOwnedChild(string path)
        {
            var actual = System.IO.Path.GetFullPath(path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar);
            var expectedParent = System.IO.Path.GetFullPath(Path)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar);
            if (!string.Equals(
                    System.IO.Path.GetDirectoryName(actual),
                    expectedParent,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Release-test data root is outside the owned temporary directory: {actual}");
            }
        }
    }
}
