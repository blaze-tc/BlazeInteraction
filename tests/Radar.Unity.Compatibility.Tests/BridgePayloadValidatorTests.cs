using System.Reflection;
using System.Text.Json;

namespace Radar.Unity.Compatibility.Tests;

public sealed class BridgePayloadValidatorTests
{
    [Fact]
    public void ValidSelfContainedPayload_IsAccepted()
    {
        using var fixture = PayloadFixture.Create();
        Assert.Null(Validate(fixture.Root));
    }

    [Theory]
    [InlineData("RadarBridge.exe")]
    [InlineData("RadarBridge.dll")]
    [InlineData("RadarBridge.deps.json")]
    [InlineData("RadarBridge.runtimeconfig.json")]
    [InlineData("coreclr.dll")]
    [InlineData("hostfxr.dll")]
    [InlineData("hostpolicy.dll")]
    [InlineData("System.Private.CoreLib.dll")]
    [InlineData("PresentationFramework.dll")]
    [InlineData("PresentationCore.dll")]
    [InlineData("WindowsBase.dll")]
    [InlineData("wpfgfx_cor3.dll")]
    public void MissingRequiredRuntimeDependency_IsRejected(string relativePath)
    {
        using var fixture = PayloadFixture.Create();
        File.Delete(Path.Combine(fixture.Root, relativePath));
        Assert.Contains(relativePath, Validate(fixture.Root), StringComparison.Ordinal);
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

    private static string? Validate(string directory)
    {
        var type = typeof(BridgePayloadValidatorTests).Assembly.GetType("Blaze.Radar.Internal.BridgePayloadValidator");
        Assert.NotNull(type);
        var method = type.GetMethod("Validate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string?)method.Invoke(null, new object[] { directory, "1.2.0" });
    }

    private sealed class PayloadFixture : IDisposable
    {
        private PayloadFixture(string root) { Root = root; }
        public string Root { get; }

        public static PayloadFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "RadarPayloadTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "profiles"));
            foreach (var file in new[]
                     {
                         "RadarBridge.exe", "RadarBridge.dll", "RadarBridge.deps.json", "coreclr.dll", "hostfxr.dll",
                         "hostpolicy.dll", "System.Private.CoreLib.dll", "PresentationFramework.dll", "PresentationCore.dll",
                         "WindowsBase.dll", "wpfgfx_cor3.dll"
                     }) File.WriteAllText(Path.Combine(root, file), file);
            File.WriteAllText(Path.Combine(root, "bridge-version.txt"), "1.2.0");
            File.WriteAllText(Path.Combine(root, "RadarBridge.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\"},{\"name\":\"Microsoft.WindowsDesktop.App\"}]}}");
            foreach (var profile in new[] { "default-profile.json", "f20-profile.json" })
                File.WriteAllText(Path.Combine(root, "profiles", profile), JsonSerializer.Serialize(new { schemaVersion = 2 }));
            return new PayloadFixture(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
