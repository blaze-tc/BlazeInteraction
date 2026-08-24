using System.Diagnostics;
using System.IO.Pipes;
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
        Assert.True(File.Exists(Path.Combine(cameraDirectory, "Blaze.Provider.CameraVision.dll")));
        Assert.True(File.Exists(Path.Combine(cameraDirectory, "provider.json")));
        Assert.True(File.Exists(Path.Combine(
            cameraDirectory,
            "profiles",
            "camera-vision-default.json")));
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
        await SmokePublishedBridgeAsync(
            output.Path,
            expectedProviderId: "blaze.radar.f10f20",
            expectedInstanceId: "radar-main");
        await SmokePublishedBridgeAsync(
            output.Path,
            expectedProviderId: "blaze.camera.vision",
            expectedInstanceId: "camera-vision-main",
            selectedProviderId: "blaze.camera.vision");
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
        string expectedProviderId,
        string expectedInstanceId,
        string? selectedProviderId = null)
    {
        var pipeName = $"Blaze.InteractionBridge.Release.{Guid.NewGuid():N}";
        var startInfo = new ProcessStartInfo(Path.Combine(publishRoot, "BlazeInteractionBridge.exe"))
        {
            WorkingDirectory = publishRoot,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "--parent-pid", Environment.ProcessId.ToString(),
                     "--minimized",
                     "--pipe-name", pipeName
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
            Assert.Equal(InteractionMessageType.HelloAck, acknowledgement.MessageType);
            Assert.Equal(
                expectedProviderId,
                acknowledgement.DeserializePayload<HelloAckPayload>().ActiveProvider!.Id);

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
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "BlazeInteractionReleaseTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
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
    }
}
