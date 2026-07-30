using System.Reflection;
using System.Text.Json.Nodes;

namespace Radar.Unity.Compatibility.Tests;

public sealed class BridgePayloadValidatorTests
{
    [Fact]
    public void Committed491FilePayload_IsAccepted()
    {
        var payload = Path.Combine(FindRepositoryRoot(), "UnityPackage", "com.blaze.radar", "Bridge~", "win-x64");
        Assert.Equal(491, Directory.GetFiles(payload, "*", SearchOption.AllDirectories).Length);
        Assert.Null(Validate(payload, "1.2.7"));
    }

    [Fact]
    public void ValidDepsDrivenFixture_IsAccepted()
    {
        using var fixture = PayloadFixture.Create();
        Assert.Null(Validate(fixture.Root));
    }

    [Theory]
    [InlineData("RadarBridge.exe")]
    [InlineData("RadarBridge.dll")]
    [InlineData("RadarBridge.deps.json")]
    [InlineData("RadarBridge.runtimeconfig.json")]
    [InlineData("hostfxr.dll")]
    [InlineData("hostpolicy.dll")]
    [InlineData("coreclr.dll")]
    [InlineData("System.Xaml.dll")]
    [InlineData("PresentationUI.dll")]
    [InlineData("vcruntime140_cor3.dll")]
    [InlineData("Managed.Dependency.dll")]
    [InlineData("Native.Dependency.dll")]
    [InlineData("Rid.Managed.dll")]
    [InlineData("Rid.Native.dll")]
    [InlineData("fr/Resource.Dependency.resources.dll")]
    public void MissingExplicitOrDepsDerivedAsset_IsRejected(string relativePath)
    {
        using var fixture = PayloadFixture.Create();
        File.Delete(Path.Combine(fixture.Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains(Path.GetFileName(relativePath), Validate(fixture.Root), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json", "valid JSON")]
    [InlineData("{}", "runtimeTarget")]
    public void MalformedOrIncompleteDepsJson_IsRejected(string json, string expectedError)
    {
        using var fixture = PayloadFixture.Create();
        File.WriteAllText(fixture.DepsPath, json);
        Assert.Contains(expectedError, Validate(fixture.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSelectedTarget_IsRejected()
    {
        using var fixture = PayloadFixture.Create();
        var deps = fixture.ReadDeps();
        deps["targets"]!.AsObject().Clear();
        fixture.WriteDeps(deps);
        Assert.Contains("target", Validate(fixture.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingLibrariesCatalog_IsRejected()
    {
        using var fixture = PayloadFixture.Create();
        var deps = fixture.ReadDeps();
        deps.Remove("libraries");
        fixture.WriteDeps(deps);
        Assert.Contains("libraries", Validate(fixture.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectedTargetLibraryMissingFromCatalog_IsRejected()
    {
        using var fixture = PayloadFixture.Create();
        var deps = fixture.ReadDeps();
        deps["libraries"]!.AsObject().Remove("RuntimePack/1.0.0");
        fixture.WriteDeps(deps);
        Assert.Contains("RuntimePack/1.0.0", Validate(fixture.Root), StringComparison.Ordinal);
    }

    [Fact]
    public void DepsReferenceToNonexistentAsset_IsRejected()
    {
        using var fixture = PayloadFixture.Create();
        var deps = fixture.ReadDeps();
        var target = deps["targets"]![PayloadFixture.TargetName]!.AsObject();
        target["App/1.2.0"]!["runtime"]!["lib/net8.0/Does.Not.Exist.dll"] = new JsonObject();
        fixture.WriteDeps(deps);
        Assert.Contains("Does.Not.Exist.dll", Validate(fixture.Root), StringComparison.Ordinal);
    }

    [Fact]
    public void FlattenedManagedPathsWithCaseOnlyBasenameCollision_AreRejected()
    {
        using var fixture = PayloadFixture.Create();
        fixture.AddManagedPathCollision();
        var error = Validate(fixture.Root);
        Assert.Contains("collision", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lib/a/Foo.dll", error, StringComparison.Ordinal);
        Assert.Contains("lib/b/foo.dll", error, StringComparison.Ordinal);
        Assert.Contains("Foo.dll", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeAndNativeAssetsWithSameBasename_AreRejected()
    {
        using var fixture = PayloadFixture.Create();
        fixture.AddRuntimeNativeCollision();
        var error = Validate(fixture.Root);
        Assert.Contains("collision", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtime", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SectionCollision.dll", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResourceLocaleAndPathCaseCollision_IsRejected()
    {
        using var fixture = PayloadFixture.Create();
        fixture.AddResourceCaseCollision();
        var error = Validate(fixture.Root);
        Assert.Contains("collision", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lib/net8.0/fr/LocaleCollision.resources.dll", error, StringComparison.Ordinal);
        Assert.Contains("lib/net8.0/FR/localecollision.resources.dll", error, StringComparison.Ordinal);
        Assert.Contains("fr", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RuntimeTargetsMappedPathCollision_IsRejected()
    {
        using var fixture = PayloadFixture.Create();
        fixture.AddRuntimeTargetsCollision();
        var error = Validate(fixture.Root);
        Assert.Contains("collision", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runtimes/win-x64/lib/net8.0/RidCollision.dll", error, StringComparison.Ordinal);
        Assert.Contains("runtimes/win-x64/native/ridcollision.dll", error, StringComparison.Ordinal);
        Assert.Contains("RidCollision.dll", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MismatchedRuntimeIdentifier_IsRejected()
    {
        using var fixture = PayloadFixture.Create();
        var deps = fixture.ReadDeps();
        deps["runtimeTarget"]!["name"] = ".NETCoreApp,Version=v8.0/linux-x64";
        fixture.WriteDeps(deps);
        Assert.Contains("win-x64", Validate(fixture.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedAssetSection_IsRejected()
    {
        using var fixture = PayloadFixture.Create();
        var deps = fixture.ReadDeps();
        deps["targets"]![PayloadFixture.TargetName]!["App/1.2.0"]!["runtime"] = new JsonArray();
        fixture.WriteDeps(deps);
        Assert.Contains("runtime", Validate(fixture.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-json", "valid JSON")]
    [InlineData("{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\"}}}", "framework-dependent")]
    [InlineData("{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"}]}}", "Microsoft.WindowsDesktop.App")]
    [InlineData("{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.WindowsDesktop.App\"}]}}", "Microsoft.NETCore.App")]
    public void CorruptOrFrameworkDependentRuntimeConfig_IsRejected(string json, string expectedError)
    {
        using var fixture = PayloadFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "RadarBridge.runtimeconfig.json"), json);
        Assert.Contains(expectedError, Validate(fixture.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("default-profile.json")]
    [InlineData("f20-profile.json")]
    public void MissingOrCorruptSchema2Profile_IsRejected(string profileName)
    {
        using var fixture = PayloadFixture.Create();
        var profile = Path.Combine(fixture.Root, "profiles", profileName);
        File.WriteAllText(profile, "{\"schemaVersion\":1}");
        Assert.Contains(profileName, Validate(fixture.Root), StringComparison.Ordinal);
        Assert.Contains("Schema 2", Validate(fixture.Root), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("default-profile.json")]
    [InlineData("f20-profile.json")]
    public void MissingSchema2Profile_IsRejected(string profileName)
    {
        using var fixture = PayloadFixture.Create();
        File.Delete(Path.Combine(fixture.Root, "profiles", profileName));
        Assert.Contains(profileName, Validate(fixture.Root), StringComparison.Ordinal);
    }

    private static string? Validate(string directory, string expectedVersion = "1.2.0")
    {
        var type = typeof(BridgePayloadValidatorTests).Assembly.GetType("Blaze.Radar.Internal.BridgePayloadValidator");
        Assert.NotNull(type);
        var method = type.GetMethod("Validate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method.Invoke(null, new object[] { directory, expectedVersion });
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Unable to locate RadarControl.sln.");
    }

    private sealed class PayloadFixture : IDisposable
    {
        internal const string TargetName = ".NETCoreApp,Version=v8.0/win-x64";

        private PayloadFixture(string root) { Root = root; }
        public string Root { get; }
        public string DepsPath => Path.Combine(Root, "RadarBridge.deps.json");

        public static PayloadFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "RadarPayloadTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "profiles"));
            Directory.CreateDirectory(Path.Combine(root, "fr"));
            foreach (var relativePath in new[]
                     {
                         "RadarBridge.exe", "RadarBridge.dll", "hostfxr.dll", "hostpolicy.dll", "coreclr.dll",
                         "System.Private.CoreLib.dll", "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "wpfgfx_cor3.dll",
                         "System.Xaml.dll", "PresentationUI.dll", "vcruntime140_cor3.dll", "Managed.Dependency.dll",
                         "Native.Dependency.dll", "Rid.Managed.dll", "Rid.Native.dll",
                         "fr/Resource.Dependency.resources.dll"
                     })
            {
                var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllText(path, relativePath);
            }

            File.WriteAllText(Path.Combine(root, "bridge-version.txt"), "1.2.0");
            File.WriteAllText(Path.Combine(root, "RadarBridge.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"},{\"name\":\"Microsoft.WindowsDesktop.App\"}]}}");
            foreach (var profile in new[] { "default-profile.json", "f20-profile.json" })
                File.WriteAllText(Path.Combine(root, "profiles", profile), "{\"schemaVersion\":2}");

            var deps = new JsonObject
            {
                ["runtimeTarget"] = new JsonObject { ["name"] = TargetName, ["signature"] = string.Empty },
                ["targets"] = new JsonObject
                {
                    [TargetName] = new JsonObject
                    {
                        ["App/1.2.0"] = new JsonObject
                        {
                            ["runtime"] = Assets("RadarBridge.dll", "lib/net8.0/Managed.Dependency.dll")
                        },
                        ["RuntimePack/1.0.0"] = new JsonObject
                        {
                            ["runtime"] = Assets("coreclr.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "System.Xaml.dll", "PresentationUI.dll"),
                            ["native"] = Assets("runtimes/win-x64/native/wpfgfx_cor3.dll", "runtimes/win-x64/native/vcruntime140_cor3.dll", "native/Native.Dependency.dll")
                        },
                        ["ResourcePack/1.0.0"] = new JsonObject
                        {
                            ["resources"] = new JsonObject
                            {
                                ["lib/net8.0/fr/Resource.Dependency.resources.dll"] = new JsonObject { ["locale"] = "fr" }
                            }
                        },
                        ["RidPack/1.0.0"] = new JsonObject
                        {
                            ["runtimeTargets"] = new JsonObject
                            {
                                ["runtimes/win-x64/lib/net8.0/Rid.Managed.dll"] = RuntimeTarget("win-x64", "runtime"),
                                ["runtimes/win-x64/native/Rid.Native.dll"] = RuntimeTarget("win-x64", "native"),
                                ["runtimes/linux-x64/native/Ignored.Native.so"] = RuntimeTarget("linux-x64", "native")
                            }
                        }
                    }
                },
                ["libraries"] = new JsonObject
                {
                    ["App/1.2.0"] = Library(),
                    ["RuntimePack/1.0.0"] = Library(),
                    ["ResourcePack/1.0.0"] = Library(),
                    ["RidPack/1.0.0"] = Library()
                }
            };
            File.WriteAllText(Path.Combine(root, "RadarBridge.deps.json"), deps.ToJsonString());
            return new PayloadFixture(root);
        }

        public JsonObject ReadDeps() => JsonNode.Parse(File.ReadAllText(DepsPath))!.AsObject();
        public void WriteDeps(JsonObject deps) => File.WriteAllText(DepsPath, deps.ToJsonString());

        public void AddManagedPathCollision()
        {
            var deps = ReadDeps();
            var runtime = deps["targets"]![TargetName]!["App/1.2.0"]!["runtime"]!.AsObject();
            runtime["lib/a/Foo.dll"] = new JsonObject();
            runtime["lib/b/foo.dll"] = new JsonObject();
            WriteDeps(deps);
            File.WriteAllText(Path.Combine(Root, "Foo.dll"), "one flattened file");
        }

        public void AddRuntimeNativeCollision()
        {
            var deps = ReadDeps();
            deps["targets"]![TargetName]!["App/1.2.0"]!["runtime"]!["lib/net8.0/SectionCollision.dll"] = new JsonObject();
            deps["targets"]![TargetName]!["RuntimePack/1.0.0"]!["native"]!["native/SectionCollision.dll"] = new JsonObject();
            WriteDeps(deps);
            File.WriteAllText(Path.Combine(Root, "SectionCollision.dll"), "one flattened file");
        }

        public void AddResourceCaseCollision()
        {
            var deps = ReadDeps();
            var resources = deps["targets"]![TargetName]!["ResourcePack/1.0.0"]!["resources"]!.AsObject();
            resources["lib/net8.0/fr/LocaleCollision.resources.dll"] = new JsonObject { ["locale"] = "fr" };
            resources["lib/net8.0/FR/localecollision.resources.dll"] = new JsonObject { ["locale"] = "FR" };
            WriteDeps(deps);
            File.WriteAllText(Path.Combine(Root, "fr", "LocaleCollision.resources.dll"), "one resource file");
        }

        public void AddRuntimeTargetsCollision()
        {
            var deps = ReadDeps();
            var runtimeTargets = deps["targets"]![TargetName]!["RidPack/1.0.0"]!["runtimeTargets"]!.AsObject();
            runtimeTargets["runtimes/win-x64/lib/net8.0/RidCollision.dll"] = RuntimeTarget("win-x64", "runtime");
            runtimeTargets["runtimes/win-x64/native/ridcollision.dll"] = RuntimeTarget("win-x64", "native");
            WriteDeps(deps);
            File.WriteAllText(Path.Combine(Root, "RidCollision.dll"), "one flattened file");
        }

        private static JsonObject Assets(params string[] paths)
        {
            var assets = new JsonObject();
            foreach (var path in paths) assets[path] = new JsonObject();
            return assets;
        }

        private static JsonObject RuntimeTarget(string rid, string assetType) =>
            new() { ["rid"] = rid, ["assetType"] = assetType };

        private static JsonObject Library() => new() { ["type"] = "project", ["serviceable"] = false, ["sha512"] = string.Empty };

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
