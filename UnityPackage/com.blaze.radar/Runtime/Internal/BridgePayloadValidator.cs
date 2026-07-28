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
        private const string ExpectedRuntimeIdentifier = "win-x64";

        private static readonly IReadOnlyList<string> RequiredHostFiles = new[]
        {
            "RadarBridge.exe",
            "RadarBridge.dll",
            "RadarBridge.deps.json",
            "RadarBridge.runtimeconfig.json",
            "hostfxr.dll",
            "hostpolicy.dll"
        };

        internal static string Validate(string directory, string expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return $"RadarBridge payload directory does not exist: {directory}";

            foreach (var relativePath in RequiredHostFiles)
            {
                if (!File.Exists(Path.Combine(directory, relativePath)))
                    return $"Required RadarBridge host file is missing: {relativePath}";
            }

            var runtimeError = ValidateRuntimeConfig(Path.Combine(directory, "RadarBridge.runtimeconfig.json"));
            if (runtimeError != null) return runtimeError;

            var dependencyError = ValidateDependencyManifest(directory, Path.Combine(directory, "RadarBridge.deps.json"));
            if (dependencyError != null) return dependencyError;

            var markerPath = Path.Combine(directory, "bridge-version.txt");
            if (!File.Exists(markerPath)) return "Required RadarBridge version marker is missing: bridge-version.txt";
            var actualVersion = File.ReadAllText(markerPath).Trim();
            if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
                return $"RadarBridge version marker expected '{expectedVersion}', found '{actualVersion}'.";

            foreach (var profileName in new[] { "default-profile.json", "f20-profile.json" })
            {
                var profileError = ValidateSchema2Profile(Path.Combine(directory, "profiles", profileName), profileName);
                if (profileError != null) return profileError;
            }

            return null;
        }

        private static string ValidateDependencyManifest(string directory, string path)
        {
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception exception) { return $"RadarBridge.deps.json must contain valid JSON: {exception.Message}"; }

            var runtimeTarget = root["runtimeTarget"] as JObject;
            var targetName = (string)runtimeTarget?["name"];
            if (string.IsNullOrWhiteSpace(targetName))
                return "RadarBridge.deps.json is missing runtimeTarget.name.";

            var separatorIndex = targetName.LastIndexOf('/');
            if (separatorIndex < 0 || separatorIndex == targetName.Length - 1)
                return $"RadarBridge.deps.json runtimeTarget.name does not identify RID '{ExpectedRuntimeIdentifier}': {targetName}";
            var runtimeIdentifier = targetName.Substring(separatorIndex + 1);
            if (!string.Equals(runtimeIdentifier, ExpectedRuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
                return $"RadarBridge.deps.json runtime target RID must be '{ExpectedRuntimeIdentifier}', found '{runtimeIdentifier}'.";

            var targets = root["targets"] as JObject;
            if (targets == null) return "RadarBridge.deps.json is missing targets.";
            var selectedTarget = targets[targetName] as JObject;
            if (selectedTarget == null)
                return $"RadarBridge.deps.json is missing selected target '{targetName}'.";

            var libraries = root["libraries"] as JObject;
            if (libraries == null || !libraries.Properties().Any())
                return "RadarBridge.deps.json is missing libraries.";

            var requiredAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var discoveredAssetCount = 0;
            foreach (var libraryProperty in selectedTarget.Properties())
            {
                var library = libraryProperty.Value as JObject;
                if (library == null)
                    return $"RadarBridge.deps.json target library '{libraryProperty.Name}' must be an object.";
                if (!(libraries[libraryProperty.Name] is JObject))
                    return $"RadarBridge.deps.json target library '{libraryProperty.Name}' is missing from libraries.";

                foreach (var assetType in new[] { "runtime", "native", "resources" })
                {
                    var sectionError = CollectAssetSection(
                        library, assetType, requiredAssets, ref discoveredAssetCount);
                    if (sectionError != null)
                        return $"RadarBridge.deps.json library '{libraryProperty.Name}' {sectionError}";
                }

                var runtimeTargetsError = CollectRuntimeTargets(
                    library, requiredAssets, ref discoveredAssetCount);
                if (runtimeTargetsError != null)
                    return $"RadarBridge.deps.json library '{libraryProperty.Name}' {runtimeTargetsError}";
            }

            if (discoveredAssetCount == 0)
                return $"RadarBridge.deps.json selected target '{targetName}' contains no runtime, native, resources or runtimeTargets assets.";

            foreach (var relativePath in requiredAssets.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(directory, relativePath)))
                    return $"RadarBridge dependency asset is missing: {relativePath}";
            }

            return null;
        }

        private static string CollectAssetSection(
            JObject library,
            string assetType,
            ISet<string> requiredAssets,
            ref int discoveredAssetCount)
        {
            var token = library[assetType];
            if (token == null) return null;
            var section = token as JObject;
            if (section == null) return $"{assetType} assets must be an object.";

            foreach (var asset in section.Properties())
            {
                if (!(asset.Value is JObject metadata))
                    return $"{assetType} asset '{asset.Name}' metadata must be an object.";
                var relativePath = MapPublishedAsset(asset.Name, assetType, metadata, out var mappingError);
                if (mappingError != null) return mappingError;
                requiredAssets.Add(relativePath);
                discoveredAssetCount++;
            }
            return null;
        }

        private static string CollectRuntimeTargets(
            JObject library,
            ISet<string> requiredAssets,
            ref int discoveredAssetCount)
        {
            var token = library["runtimeTargets"];
            if (token == null) return null;
            var section = token as JObject;
            if (section == null) return "runtimeTargets assets must be an object.";

            foreach (var asset in section.Properties())
            {
                var metadata = asset.Value as JObject;
                if (metadata == null) return $"runtimeTargets asset '{asset.Name}' metadata must be an object.";
                var rid = (string)metadata["rid"];
                var assetType = (string)metadata["assetType"];
                if (string.IsNullOrWhiteSpace(rid) || string.IsNullOrWhiteSpace(assetType))
                    return $"runtimeTargets asset '{asset.Name}' must declare rid and assetType.";
                if (!string.Equals(rid, ExpectedRuntimeIdentifier, StringComparison.OrdinalIgnoreCase)) continue;

                var relativePath = MapPublishedAsset(asset.Name, assetType, metadata, out var mappingError);
                if (mappingError != null) return mappingError;
                requiredAssets.Add(relativePath);
                discoveredAssetCount++;
            }
            return null;
        }

        private static string MapPublishedAsset(
            string assetPath,
            string assetType,
            JObject metadata,
            out string error)
        {
            error = null;
            var fileName = Path.GetFileName((assetPath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                error = $"{assetType} asset path is invalid: '{assetPath}'.";
                return null;
            }

            if (string.Equals(assetType, "runtime", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(assetType, "native", StringComparison.OrdinalIgnoreCase))
                return fileName;

            if (!string.Equals(assetType, "resources", StringComparison.OrdinalIgnoreCase))
            {
                error = $"runtimeTargets asset '{assetPath}' has unsupported assetType '{assetType}'.";
                return null;
            }

            var locale = (string)metadata["locale"];
            if (string.IsNullOrWhiteSpace(locale) || locale == "." || locale == ".." ||
                locale.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
            {
                error = $"resources asset '{assetPath}' has invalid locale '{locale}'.";
                return null;
            }
            return Path.Combine(locale, fileName);
        }

        private static string ValidateRuntimeConfig(string path)
        {
            JObject root;
            try { root = JObject.Parse(File.ReadAllText(path)); }
            catch (Exception exception) { return $"RadarBridge.runtimeconfig.json must contain valid JSON: {exception.Message}"; }

            var runtimeOptions = root["runtimeOptions"] as JObject;
            if (runtimeOptions == null) return "RadarBridge.runtimeconfig.json is missing runtimeOptions.";
            if (runtimeOptions["framework"] != null || runtimeOptions["frameworks"] != null)
                return "RadarBridge.runtimeconfig.json describes framework-dependent output.";

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
