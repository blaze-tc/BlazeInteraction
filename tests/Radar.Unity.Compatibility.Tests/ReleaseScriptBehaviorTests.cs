using System.Diagnostics;

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
            foreach (var file in new[]
                     {
                         "RadarBridge.exe", "RadarBridge.dll", "RadarBridge.deps.json", "coreclr.dll", "hostfxr.dll",
                         "hostpolicy.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll", "PresentationCore.dll",
                         "WindowsBase.dll", "wpfgfx_cor3.dll"
                     }) File.WriteAllText(Path.Combine(Payload, file), file);
            File.WriteAllText(Path.Combine(Payload, "RadarBridge.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"},{\"name\":\"Microsoft.WindowsDesktop.App\"}]}}");
            File.WriteAllText(Path.Combine(Profiles, "default-profile.json"), "{\"schemaVersion\":2}");
            File.WriteAllText(Path.Combine(Profiles, "f20-profile.json"), "{\"schemaVersion\":2}");
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
