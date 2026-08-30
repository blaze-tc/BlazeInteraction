using System.Text.Json;
using System.Security.Cryptography;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Radar.Unity.Compatibility.Tests;

public sealed class EmbeddedBridgePayloadTests
{
    [Fact]
    public void PackageContainsOneSelfContainedInteractionBridgeAndBothExternalProviders()
    {
        var repositoryRoot = FindRepositoryRoot();
        var packageRoot = Path.Combine(repositoryRoot, "UnityPackage", "com.blaze.interaction");
        var publishDirectory = Path.Combine(packageRoot, "Bridge~", "win-x64");

        foreach (var relativePath in new[]
                 {
                     "BlazeInteractionBridge.exe",
                     "BlazeInteractionBridge.deps.json",
                     "BlazeInteractionBridge.runtimeconfig.json",
                     "hostfxr.dll",
                     "hostpolicy.dll",
                     "bridge-version.txt",
                     Path.Combine("Providers", "Radar", "provider.json"),
                     Path.Combine("Providers", "Radar", "Blaze.Provider.Radar.dll"),
                     Path.Combine("Providers", "CameraVision", "provider.json"),
                     Path.Combine("Providers", "CameraVision", "Blaze.Provider.CameraVision.dll"),
                     Path.Combine("Providers", "CameraVision", "hand-runtime.json"),
                     Path.Combine("Providers", "CameraVision", "models", "hand_landmarker.task"),
                     Path.Combine("Providers", "CameraVision", "runtimes", "win-x64", "native", "Blaze.HandTracking.Native.dll")
                 })
        {
            Assert.True(File.Exists(Path.Combine(publishDirectory, relativePath)), relativePath);
        }

        Assert.Equal("BlazeInteractionBridge.exe", Path.GetFileName(Assert.Single(
            Directory.EnumerateFiles(publishDirectory, "*.exe", SearchOption.AllDirectories))));
        Assert.Equal("BlazeInteractionBridge.exe", Path.GetFileName(Assert.Single(
            Directory.EnumerateFiles(publishDirectory, "*.exe", SearchOption.TopDirectoryOnly))));
        Assert.Equal("1.1.0", File.ReadAllText(Path.Combine(publishDirectory, "bridge-version.txt")).Trim());

        using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            publishDirectory, "BlazeInteractionBridge.runtimeconfig.json")));
        Assert.True(runtimeConfig.RootElement.GetProperty("runtimeOptions").TryGetProperty(
            "includedFrameworks", out _));

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            publishDirectory, "Providers", "Radar", "provider.json")));
        Assert.Equal("blaze.radar.f10f20", manifest.RootElement.GetProperty("id").GetString());
        Assert.Equal("1.1.0", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(1, manifest.RootElement.GetProperty("providerApiVersion").GetInt32());

        using var cameraManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            publishDirectory, "Providers", "CameraVision", "provider.json")));
        Assert.Equal("blaze.camera.vision", cameraManifest.RootElement.GetProperty("id").GetString());
        Assert.Equal("1.1.0", cameraManifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(1, cameraManifest.RootElement.GetProperty("providerApiVersion").GetInt32());
        Assert.Contains(
            Directory.EnumerateFiles(
                Path.Combine(publishDirectory, "Providers", "CameraVision"),
                "*.dll",
                SearchOption.AllDirectories),
            path => string.Equals(Path.GetFileName(path), "OpenCvSharpExtern.dll", StringComparison.OrdinalIgnoreCase));
        foreach (var forbiddenName in new[] { "python", "CameraWorker", "MediaPipeWorker", "ProviderHost" })
        {
            Assert.DoesNotContain(
                Directory.EnumerateFileSystemEntries(
                    Path.Combine(publishDirectory, "Providers", "CameraVision"),
                    "*",
                    SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains(forbiddenName, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void EmbeddedCameraHandRuntimeMatchesFrozenProvenance()
    {
        var repositoryRoot = FindRepositoryRoot();
        var cameraDirectory = Path.Combine(
            repositoryRoot,
            "UnityPackage", "com.blaze.interaction", "Bridge~", "win-x64",
            "Providers", "CameraVision");
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot,
            "eng",
            "mediapipe-hand.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            cameraDirectory,
            "hand-runtime.json")));

        Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            provenance.RootElement.GetProperty("abiVersion").GetInt32(),
            manifest.RootElement.GetProperty("abiVersion").GetInt32());
        AssertEmbeddedAssetMatches(
            cameraDirectory,
            manifest.RootElement.GetProperty("nativeLibrary"),
            provenance.RootElement.GetProperty("nativeDllSha256").GetString()!);
        AssertEmbeddedAssetMatches(
            cameraDirectory,
            manifest.RootElement.GetProperty("model"),
            provenance.RootElement.GetProperty("modelSha256").GetString()!);
    }

    private static void AssertEmbeddedAssetMatches(
        string cameraDirectory,
        JsonElement asset,
        string expectedHash)
    {
        Assert.Equal(expectedHash, asset.GetProperty("sha256").GetString());
        var path = Path.Combine(cameraDirectory, asset.GetProperty("path").GetString()!);
        Assert.True(File.Exists(path), path);
        Assert.Equal(
            expectedHash,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
    }

    [Fact]
    public void EmbeddedContractsExposeInteractionFootprint()
    {
        var assemblyPath = Path.Combine(
            FindRepositoryRoot(),
            "UnityPackage", "com.blaze.interaction", "Bridge~", "win-x64",
            "Blaze.Interaction.Contracts.dll");
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var interactionPoint = metadata.TypeDefinitions
            .Select(metadata.GetTypeDefinition)
            .Single(type => metadata.GetString(type.Namespace) == "Blaze.Interaction.Contracts" &&
                            metadata.GetString(type.Name) == "InteractionPoint");

        Assert.Contains(interactionPoint.GetProperties(), handle =>
            metadata.GetString(metadata.GetPropertyDefinition(handle).Name) == "Fp");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BlazeInteraction.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate BlazeInteraction.sln.");
    }
}
