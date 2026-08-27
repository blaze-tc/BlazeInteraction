using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Blaze.Interaction.Editor;

namespace Radar.Unity.Compatibility.Tests;

public sealed class BridgePayloadValidatorTests
{
    [Fact]
    public void ValidSelfContainedPayloadWithRadarAndCameraVisionProvidersPasses()
    {
        using var fixture = PayloadFixture.Create();

        Assert.Null(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing-interaction-payload")]
    public void MissingPayloadDirectoryIsRejected(string? directory)
    {
        var path = string.IsNullOrEmpty(directory)
            ? directory
            : Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), directory);

        Assert.NotNull(InteractionBridgePayloadValidator.Validate(path!, "1.0.0"));
    }

    [Theory]
    [InlineData("BlazeInteractionBridge.exe")]
    [InlineData("BlazeInteractionBridge.dll")]
    [InlineData("BlazeInteractionBridge.deps.json")]
    [InlineData("BlazeInteractionBridge.runtimeconfig.json")]
    [InlineData("hostfxr.dll")]
    [InlineData("hostpolicy.dll")]
    [InlineData("bridge-version.txt")]
    public void MissingRequiredHostFileIsRejected(string file)
    {
        using var fixture = PayloadFixture.Create();
        File.Delete(Path.Combine(fixture.Root, file));

        Assert.NotNull(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    [Theory]
    [InlineData("createdump.exe")]
    [InlineData("RadarBridge.exe")]
    [InlineData("tools/helper.exe")]
    public void AnySecondExecutableIsRejected(string relativePath)
    {
        using var fixture = PayloadFixture.Create();
        fixture.Write(relativePath, "extra");

        Assert.Contains(
            "exactly one executable",
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bridge")]
    [InlineData("provider")]
    public void BridgeOrProviderVersionMismatchIsRejected(string target)
    {
        using var fixture = PayloadFixture.Create();
        if (target == "bridge")
        {
            fixture.Write("bridge-version.txt", "0.9.0");
        }
        else
        {
            fixture.SetManifest("version", JsonValue.Create("0.9.0"));
        }

        Assert.NotNull(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    [Theory]
    [InlineData("includedFrameworks")]
    [InlineData("Microsoft.NETCore.App")]
    [InlineData("Microsoft.WindowsDesktop.App")]
    public void RuntimeConfigWithoutSelfContainedMarkerIsRejected(string marker)
    {
        using var fixture = PayloadFixture.Create();
        var path = Path.Combine(fixture.Root, "BlazeInteractionBridge.runtimeconfig.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(marker, "removed", StringComparison.Ordinal));

        Assert.Contains(
            "self-contained",
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("id", "wrong.provider")]
    [InlineData("version", "2.0.0")]
    [InlineData("entryAssembly", "Wrong.dll")]
    [InlineData("entryType", "Wrong.Plugin")]
    [InlineData("id", "")]
    public void RadarManifestIdentityMismatchIsRejected(string property, string value)
    {
        using var fixture = PayloadFixture.Create();
        fixture.SetManifest(property, JsonValue.Create(value));

        Assert.NotNull(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void UnsupportedProviderApiIsRejected(int apiVersion)
    {
        using var fixture = PayloadFixture.Create();
        fixture.SetManifest("providerApiVersion", JsonValue.Create(apiVersion));

        Assert.NotNull(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    [Fact]
    public void MissingProviderEntryAssemblyIsRejected()
    {
        using var fixture = PayloadFixture.Create();
        File.Delete(Path.Combine(fixture.RadarRoot, "Blaze.Provider.Radar.dll"));

        Assert.Contains(
            "entry assembly",
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingProviderManifestIsRejected()
    {
        using var fixture = PayloadFixture.Create();
        File.Delete(fixture.ManifestPath);

        Assert.Contains(
            "manifest",
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingCameraVisionManifestIsRejected()
    {
        using var fixture = PayloadFixture.Create();
        File.Delete(fixture.CameraManifestPath);

        Assert.Contains(
            "CameraVision",
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingCameraVisionEntryOrNativeRuntimeIsRejected()
    {
        using var missingEntry = PayloadFixture.Create();
        File.Delete(Path.Combine(missingEntry.CameraRoot, "Blaze.Provider.CameraVision.dll"));
        Assert.NotNull(InteractionBridgePayloadValidator.Validate(missingEntry.Root, "1.0.0"));

        using var missingNative = PayloadFixture.Create();
        File.Delete(Path.Combine(missingNative.CameraRoot, "OpenCvSharpExtern.dll"));
        Assert.NotNull(InteractionBridgePayloadValidator.Validate(missingNative.Root, "1.0.0"));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("native")]
    [InlineData("model")]
    public void MissingHandRuntimeAssetIsRejected(string target)
    {
        using var fixture = PayloadFixture.Create();
        File.Delete(target switch
        {
            "manifest" => fixture.HandRuntimeManifestPath,
            "native" => fixture.HandNativePath,
            "model" => fixture.HandModelPath,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        });

        Assert.Contains(
            target,
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Blaze.HandTracking.Native.dll")]
    [InlineData("hand_landmarker.task")]
    public void DuplicateHandRuntimeAssetIsRejected(string fileName)
    {
        using var fixture = PayloadFixture.Create();
        fixture.Write("Providers/CameraVision/duplicate/" + fileName, "duplicate");

        Assert.Contains(
            "exactly one",
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("native")]
    [InlineData("model")]
    public void HandRuntimeHashMismatchIsRejected(string target)
    {
        using var fixture = PayloadFixture.Create();
        File.WriteAllText(target == "native" ? fixture.HandNativePath : fixture.HandModelPath, "changed");

        Assert.Contains(
            "SHA-256",
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("python311.dll")]
    [InlineData("CameraWorker.dll")]
    [InlineData("MediaPipeWorker.dll")]
    [InlineData("ProviderHost.dll")]
    [InlineData("python/runtime.zip")]
    public void ForbiddenCameraRuntimeNamesAreRejected(string relativePath)
    {
        using var fixture = PayloadFixture.Create();
        fixture.Write("Providers/CameraVision/" + relativePath, "forbidden");

        Assert.Contains(
            "forbidden",
            InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"id\":")]
    public void MalformedProviderManifestIsRejected(string json)
    {
        using var fixture = PayloadFixture.Create();
        File.WriteAllText(fixture.ManifestPath, json);

        Assert.NotNull(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    [Theory]
    [InlineData("renamed")]
    [InlineData("nested")]
    public void CanonicalExecutableLocationAndUniquenessAreRequired(string mode)
    {
        using var fixture = PayloadFixture.Create();
        var executable = Path.Combine(fixture.Root, "BlazeInteractionBridge.exe");
        if (mode == "renamed")
        {
            File.Move(executable, Path.Combine(fixture.Root, "Wrong.exe"));
        }
        else
        {
            fixture.Write("nested/BlazeInteractionBridge.exe", "duplicate");
        }

        Assert.NotNull(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    [Fact]
    public void BridgeVersionMarkerTrimsReleaseNewline()
    {
        using var fixture = PayloadFixture.Create();
        fixture.Write("bridge-version.txt", "1.0.0\r\n");

        Assert.Null(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    [Fact]
    public void NestedNonExecutableProviderDependenciesRemainAllowed()
    {
        using var fixture = PayloadFixture.Create();
        fixture.Write("Providers/Radar/native/win-x64/vendor.dll", "native");
        fixture.Write("Providers/Radar/dependencies/helper.dll", "managed");

        Assert.Null(InteractionBridgePayloadValidator.Validate(fixture.Root, "1.0.0"));
    }

    private sealed class PayloadFixture : IDisposable
    {
        private PayloadFixture(string root)
        {
            Root = root;
            RadarRoot = Path.Combine(root, "Providers", "Radar");
            ManifestPath = Path.Combine(RadarRoot, "provider.json");
            CameraRoot = Path.Combine(root, "Providers", "CameraVision");
            CameraManifestPath = Path.Combine(CameraRoot, "provider.json");
            HandRuntimeManifestPath = Path.Combine(CameraRoot, "hand-runtime.json");
            HandNativePath = Path.Combine(CameraRoot, "runtimes", "win-x64", "native", "Blaze.HandTracking.Native.dll");
            HandModelPath = Path.Combine(CameraRoot, "models", "hand_landmarker.task");
        }

        public string Root { get; }
        public string RadarRoot { get; }
        public string ManifestPath { get; }
        public string CameraRoot { get; }
        public string CameraManifestPath { get; }
        public string HandRuntimeManifestPath { get; }
        public string HandNativePath { get; }
        public string HandModelPath { get; }

        public static PayloadFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "InteractionPayloadTests", Guid.NewGuid().ToString("N"));
            var fixture = new PayloadFixture(root);
            Directory.CreateDirectory(fixture.RadarRoot);
            foreach (var file in new[]
                     {
                         "BlazeInteractionBridge.exe",
                         "BlazeInteractionBridge.dll",
                         "BlazeInteractionBridge.deps.json",
                         "hostfxr.dll",
                         "hostpolicy.dll"
                     })
            {
                fixture.Write(file, "payload");
            }

            fixture.Write(
                "BlazeInteractionBridge.runtimeconfig.json",
                "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"},{\"name\":\"Microsoft.WindowsDesktop.App\"}]}}");
            fixture.Write("bridge-version.txt", "1.0.0");
            fixture.Write(
                "Providers/Radar/provider.json",
                JsonSerializer.Serialize(new
                {
                    id = "blaze.radar.f10f20",
                    version = "1.0.0",
                    providerApiVersion = 1,
                    entryAssembly = "Blaze.Provider.Radar.dll",
                    entryType = "Blaze.Provider.Radar.RadarPlugin"
                }));
            fixture.Write("Providers/Radar/Blaze.Provider.Radar.dll", "provider");
            fixture.Write(
                "Providers/CameraVision/provider.json",
                JsonSerializer.Serialize(new
                {
                    id = "blaze.camera.vision",
                    version = "1.0.0",
                    providerApiVersion = 1,
                    entryAssembly = "Blaze.Provider.CameraVision.dll",
                    entryType = "Blaze.Provider.CameraVision.CameraVisionPlugin"
                }));
            fixture.Write("Providers/CameraVision/Blaze.Provider.CameraVision.dll", "provider");
            fixture.Write("Providers/CameraVision/OpenCvSharpExtern.dll", "native");
            fixture.Write("Providers/CameraVision/runtimes/win-x64/native/Blaze.HandTracking.Native.dll", "hand-native");
            fixture.Write("Providers/CameraVision/models/hand_landmarker.task", "hand-model");
            fixture.Write(
                "Providers/CameraVision/hand-runtime.json",
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    abiVersion = 1,
                    nativeLibrary = new
                    {
                        path = "runtimes/win-x64/native/Blaze.HandTracking.Native.dll",
                        sha256 = Sha256(fixture.HandNativePath)
                    },
                    model = new
                    {
                        path = "models/hand_landmarker.task",
                        sha256 = Sha256(fixture.HandModelPath)
                    }
                }));
            return fixture;
        }

        private static string Sha256(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        public void SetManifest(string property, JsonNode? value)
        {
            var manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
            manifest[property] = value;
            File.WriteAllText(ManifestPath, manifest.ToJsonString());
        }

        public void Write(string relativePath, string contents)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
