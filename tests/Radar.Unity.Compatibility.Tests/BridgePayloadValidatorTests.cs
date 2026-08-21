using System.Text.Json;
using System.Text.Json.Nodes;
using Blaze.Interaction.Editor;

namespace Radar.Unity.Compatibility.Tests;

public sealed class BridgePayloadValidatorTests
{
    [Fact]
    public void ValidSelfContainedPayloadWithExternalRadarProviderPasses()
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
        }

        public string Root { get; }
        public string RadarRoot { get; }
        public string ManifestPath { get; }

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
            return fixture;
        }

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
