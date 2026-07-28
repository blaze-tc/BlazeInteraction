using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Radar.Unity.Compatibility.Tests;

public sealed class ReleaseScriptBehaviorTests
{
    [Fact]
    public void UnsafeDeletionTarget_IsRejectedBeforeSentinelDeletion()
    {
        using var fixture = ReleaseFixture.Create();
        var sentinel = fixture.CreateSentinel("unsafe-target");

        var result = RunPowerShell(
            "tests/Radar.Unity.Compatibility.Tests/InvokeReleaseFunctions.ps1",
            "-Scenario", "UnsafeDeletion", "-FixtureRoot", fixture.Root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("exact approved path", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sentinel), "Unsafe preflight mutated the sentinel before rejecting the target.");
    }

    [Fact]
    public void FrameworkDependentEmbedding_IsRejectedBeforeSentinelDeletion()
    {
        using var fixture = ReleaseFixture.Create();
        var sentinel = fixture.CreateSentinel("framework-dependent");

        var result = RunPowerShell(
            "tests/Radar.Unity.Compatibility.Tests/InvokeReleaseFunctions.ps1",
            "-Scenario", "FrameworkDependent", "-FixtureRoot", fixture.Root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("framework-dependent", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sentinel), "Framework-dependent preflight mutated the sentinel before rejection.");
    }

    [Fact]
    public void PreparedOutput_WritesUtf8MarkerCopiesSchema2ProfilesAndValidatesRuntime()
    {
        using var fixture = ReleaseFixture.Create();
        fixture.CreateValidPayload();

        var result = RunPowerShell(
            "tests/Radar.Unity.Compatibility.Tests/InvokeReleaseFunctions.ps1",
            "-Scenario", "PrepareOutput", "-FixtureRoot", fixture.Root);

        Assert.Equal(0, result.ExitCode);
        var marker = File.ReadAllBytes(Path.Combine(fixture.Payload, "bridge-version.txt"));
        Assert.Equal("1.2.0", System.Text.Encoding.UTF8.GetString(marker));
        Assert.False(marker.Length >= 3 && marker[0] == 0xEF && marker[1] == 0xBB && marker[2] == 0xBF);
        Assert.Equal(2, ReadSchema(Path.Combine(fixture.Payload, "profiles", "default-profile.json")));
        Assert.Equal(2, ReadSchema(Path.Combine(fixture.Payload, "profiles", "f20-profile.json")));
    }

    [Fact]
    public void PowerShellValidator_AcceptsCommitted491FilePayload()
    {
        var payload = Path.Combine(FindRepositoryRoot(), "UnityPackage", "com.blaze.radar", "Bridge~", "win-x64");
        Assert.Equal(491, Directory.GetFiles(payload, "*", SearchOption.AllDirectories).Length);
        var result = RunPowerShell(
            "tests/Radar.Unity.Compatibility.Tests/InvokeReleaseFunctions.ps1",
            "-Scenario", "ValidateOutput", "-FixtureRoot", payload);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData("System.Xaml.dll")]
    [InlineData("PresentationUI.dll")]
    [InlineData("vcruntime140_cor3.dll")]
    [InlineData("Managed.Dependency.dll")]
    [InlineData("Native.Dependency.dll")]
    [InlineData("Rid.Managed.dll")]
    [InlineData("Rid.Native.dll")]
    [InlineData("fr/Resource.Dependency.resources.dll")]
    public void PowerShellValidator_RejectsMissingDepsDerivedAsset(string relativePath)
    {
        using var fixture = ReleaseFixture.Create();
        fixture.CreateValidPayload();
        File.Delete(Path.Combine(fixture.Payload, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var result = RunPowerShell(
            "tests/Radar.Unity.Compatibility.Tests/InvokeReleaseFunctions.ps1",
            "-Scenario", "PrepareOutput", "-FixtureRoot", fixture.Root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(Path.GetFileName(relativePath), result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellValidator_RejectsMalformedDepsJson()
    {
        using var fixture = ReleaseFixture.Create();
        fixture.CreateValidPayload();
        File.WriteAllText(Path.Combine(fixture.Payload, "RadarBridge.deps.json"), "not-json");

        var result = RunPowerShell(
            "tests/Radar.Unity.Compatibility.Tests/InvokeReleaseFunctions.ps1",
            "-Scenario", "PrepareOutput", "-FixtureRoot", fixture.Root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("valid JSON", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PowerShellValidator_RejectsNonexistentDepsReference()
    {
        using var fixture = ReleaseFixture.Create();
        fixture.CreateValidPayload();
        var depsPath = Path.Combine(fixture.Payload, "RadarBridge.deps.json");
        var deps = JsonNode.Parse(File.ReadAllText(depsPath))!.AsObject();
        deps["targets"]![ReleaseFixture.TargetName]!["App/1.2.0"]!["runtime"]!["lib/net8.0/Does.Not.Exist.dll"] = new JsonObject();
        File.WriteAllText(depsPath, deps.ToJsonString());

        var result = RunPowerShell(
            "tests/Radar.Unity.Compatibility.Tests/InvokeReleaseFunctions.ps1",
            "-Scenario", "PrepareOutput", "-FixtureRoot", fixture.Root);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Does.Not.Exist.dll", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedSmokeCommand_ExecutesTheCommittedPayload()
    {
        var result = RunPowerShell(
            "scripts/test-embedded-bridge.ps1",
            "-StartupTimeoutSeconds", "20");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("IPC v2 Hello/HelloAck passed with Bridge version 1.2.0.", result.Output, StringComparison.Ordinal);
        Assert.Contains("Parent-process shutdown passed with exit code 0.", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedSmokeSetupFailure_CleansDirectoryAndOwnedProcesses()
    {
        using var fixture = ReleaseFixture.Create();
        var diagnostics = Path.Combine(fixture.Root, "processes.txt");
        var result = RunPowerShell(
            "scripts/test-embedded-bridge.ps1",
            "-StartupTimeoutSeconds", "20",
            "-InjectSetupFailure",
            "-DiagnosticProcessFile", diagnostics);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Injected setup failure", result.Output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(FindRepositoryRoot(), "tmp", "embedded-bridge-smoke")));
        Assert.True(File.Exists(diagnostics));
        foreach (var pid in File.ReadAllLines(diagnostics).Select(line => int.Parse(line.Split('=')[1])))
        {
            Assert.False(IsProcessRunning(pid), $"Owned smoke process {pid} survived failure cleanup.");
        }
    }

    [Fact]
    public void EmbeddedSmoke_ParentRemainsAlivePastEightSecondSetupDelay()
    {
        var result = RunPowerShell(
            "scripts/test-embedded-bridge.ps1",
            "-StartupTimeoutSeconds", "12",
            "-SetupDelaySeconds", "9");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("IPC v2 Hello/HelloAck passed with Bridge version 1.2.0.", result.Output, StringComparison.Ordinal);
    }

    private static int ReadSchema(string path) =>
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("schemaVersion").GetInt32();

    private static bool IsProcessRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static ProcessResult RunPowerShell(string relativeScript, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = FindRepositoryRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), relativeScript.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(90_000), "PowerShell release contract timed out.");
        return new ProcessResult(process.ExitCode, standardOutput.Result + standardError.Result);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Unable to locate RadarControl.sln.");
    }

    private sealed record ProcessResult(int ExitCode, string Output);

    private sealed class ReleaseFixture : IDisposable
    {
        internal const string TargetName = ".NETCoreApp,Version=v8.0/win-x64";

        private ReleaseFixture(string root)
        {
            Root = root;
            Payload = Path.Combine(root, "payload");
            Profiles = Path.Combine(root, "source-profiles");
            Directory.CreateDirectory(Payload);
            Directory.CreateDirectory(Profiles);
        }

        public string Root { get; }
        public string Payload { get; }
        public string Profiles { get; }

        public static ReleaseFixture Create()
        {
            var root = Path.Combine(FindRepositoryRoot(), "tmp", "release-script-tests", Guid.NewGuid().ToString("N"));
            return new ReleaseFixture(root);
        }

        public string CreateSentinel(string directoryName)
        {
            var directory = Path.Combine(Root, directoryName);
            Directory.CreateDirectory(directory);
            var sentinel = Path.Combine(directory, "sentinel.txt");
            File.WriteAllText(sentinel, "preserve");
            return sentinel;
        }

        public void CreateValidPayload()
        {
            Directory.CreateDirectory(Path.Combine(Payload, "fr"));
            foreach (var relativePath in new[]
                     {
                         "RadarBridge.exe", "RadarBridge.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
                         "System.Private.CoreLib.dll", "PresentationFramework.dll", "PresentationCore.dll", "WindowsBase.dll", "wpfgfx_cor3.dll",
                         "System.Xaml.dll", "PresentationUI.dll", "vcruntime140_cor3.dll", "Managed.Dependency.dll",
                         "Native.Dependency.dll", "Rid.Managed.dll", "Rid.Native.dll",
                         "fr/Resource.Dependency.resources.dll"
                     })
            {
                var path = Path.Combine(Payload, relativePath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllText(path, relativePath);
            }
            File.WriteAllText(Path.Combine(Payload, "RadarBridge.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"},{\"name\":\"Microsoft.WindowsDesktop.App\"}]}}");
            File.WriteAllText(Path.Combine(Profiles, "default-profile.json"), "{\"schemaVersion\":2}");
            File.WriteAllText(Path.Combine(Profiles, "f20-profile.json"), "{\"schemaVersion\":2}");
            var deps = new JsonObject
            {
                ["runtimeTarget"] = new JsonObject { ["name"] = TargetName },
                ["targets"] = new JsonObject
                {
                    [TargetName] = new JsonObject
                    {
                        ["App/1.2.0"] = new JsonObject { ["runtime"] = Assets("RadarBridge.dll", "lib/net8.0/Managed.Dependency.dll") },
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
                    ["App/1.2.0"] = Library(), ["RuntimePack/1.0.0"] = Library(),
                    ["ResourcePack/1.0.0"] = Library(), ["RidPack/1.0.0"] = Library()
                }
            };
            File.WriteAllText(Path.Combine(Payload, "RadarBridge.deps.json"), deps.ToJsonString());
        }

        private static JsonObject Assets(params string[] paths)
        {
            var assets = new JsonObject();
            foreach (var path in paths) assets[path] = new JsonObject();
            return assets;
        }

        private static JsonObject RuntimeTarget(string rid, string assetType) =>
            new() { ["rid"] = rid, ["assetType"] = assetType };

        private static JsonObject Library() => new() { ["type"] = "project", ["serviceable"] = false };

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
