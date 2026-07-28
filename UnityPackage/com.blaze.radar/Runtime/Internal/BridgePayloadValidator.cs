#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Blaze.Radar.Internal
{
    internal static class BridgePayloadValidator
    {
        private static readonly IReadOnlyList<string> RequiredRuntimeFiles = new[]
        {
            "RadarBridge.exe",
            "RadarBridge.dll",
            "RadarBridge.deps.json",
            "RadarBridge.runtimeconfig.json",
            "coreclr.dll",
            "hostfxr.dll",
            "hostpolicy.dll",
            "System.Private.CoreLib.dll",
            "PresentationFramework.dll",
            "PresentationCore.dll",
            "WindowsBase.dll",
            "wpfgfx_cor3.dll"
        };

        internal static string Validate(string directory, string expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return $"RadarBridge payload directory does not exist: {directory}";
            }

            foreach (var relativePath in RequiredRuntimeFiles)
            {
                var path = Path.Combine(directory, relativePath);
                if (!File.Exists(path)) return $"Required RadarBridge runtime dependency is missing: {relativePath}";
            }

            var runtimeError = ValidateRuntimeConfig(Path.Combine(directory, "RadarBridge.runtimeconfig.json"));
            if (runtimeError != null) return runtimeError;

            var markerPath = Path.Combine(directory, "bridge-version.txt");
            if (!File.Exists(markerPath)) return "Required RadarBridge version marker is missing: bridge-version.txt";
            var actualVersion = File.ReadAllText(markerPath).Trim();
            if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
            {
                return $"RadarBridge version marker expected '{expectedVersion}', found '{actualVersion}'.";
            }

            foreach (var profileName in new[] { "default-profile.json", "f20-profile.json" })
            {
                var profileError = ValidateSchema2Profile(Path.Combine(directory, "profiles", profileName), profileName);
                if (profileError != null) return profileError;
            }

            return null;
        }

        private static string ValidateRuntimeConfig(string path)
        {
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception exception) { return $"RadarBridge.runtimeconfig.json must contain valid JSON: {exception.Message}"; }

            var runtimeOptions = root["runtimeOptions"] as JObject;
            if (runtimeOptions == null) return "RadarBridge.runtimeconfig.json is missing runtimeOptions.";
            if (runtimeOptions["framework"] != null || runtimeOptions["frameworks"] != null)
            {
                return "RadarBridge.runtimeconfig.json describes framework-dependent output.";
            }

            var includedFrameworks = runtimeOptions["includedFrameworks"] as JArray;
            if (includedFrameworks == null) return "RadarBridge.runtimeconfig.json is missing self-contained includedFrameworks.";
            var names = includedFrameworks
                .OfType<JObject>()
                .Select(item => (string)item["name"])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            foreach (var requiredFramework in new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" })
            {
                if (!names.Contains(requiredFramework, StringComparer.Ordinal))
                    return $"RadarBridge.runtimeconfig.json is missing included framework '{requiredFramework}'.";
            }

            return null;
        }

        private static string ValidateSchema2Profile(string path, string profileName)
        {
            if (!File.Exists(path)) return $"Required Schema 2 Bridge profile is missing: {profileName}";
            try
            {
                var profile = JObject.Parse(File.ReadAllText(path));
                var schemaVersion = (int?)profile["schemaVersion"];
                if (schemaVersion != 2) return $"Bridge profile {profileName} must use Schema 2.";
            }
            catch (Exception exception)
            {
                return $"Bridge profile {profileName} must contain valid Schema 2 JSON: {exception.Message}";
            }
            return null;
        }
    }
}
