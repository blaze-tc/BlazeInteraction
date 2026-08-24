using System;
using System.IO;
using UnityEngine;

namespace Blaze.Interaction.Editor
{
    internal static class InteractionBridgePayloadValidator
    {
        private static readonly string[] RequiredHostFiles =
        {
            "BlazeInteractionBridge.exe",
            "BlazeInteractionBridge.dll",
            "BlazeInteractionBridge.deps.json",
            "BlazeInteractionBridge.runtimeconfig.json",
            "hostfxr.dll",
            "hostpolicy.dll",
            "bridge-version.txt"
        };

        internal static string Validate(string directory, string expectedVersion)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return "Bridge payload directory does not exist: " + directory;
            }

            for (var index = 0; index < RequiredHostFiles.Length; index++)
            {
                if (!File.Exists(Path.Combine(directory, RequiredHostFiles[index])))
                {
                    return "Required Bridge host file is missing: " + RequiredHostFiles[index];
                }
            }

            var executables = Directory.GetFiles(directory, "*.exe", SearchOption.AllDirectories);
            if (executables.Length != 1 ||
                !string.Equals(Path.GetFileName(executables[0]), "BlazeInteractionBridge.exe", StringComparison.Ordinal))
            {
                return "Bridge payload must contain exactly one executable named BlazeInteractionBridge.exe.";
            }

            var actualVersion = File.ReadAllText(Path.Combine(directory, "bridge-version.txt")).Trim();
            if (!string.Equals(actualVersion, expectedVersion, StringComparison.Ordinal))
            {
                return "Bridge version marker expected '" + expectedVersion + "', found '" + actualVersion + "'.";
            }

            var runtimeConfig = File.ReadAllText(Path.Combine(directory, "BlazeInteractionBridge.runtimeconfig.json"));
            if (runtimeConfig.IndexOf("includedFrameworks", StringComparison.Ordinal) < 0 ||
                runtimeConfig.IndexOf("Microsoft.NETCore.App", StringComparison.Ordinal) < 0 ||
                runtimeConfig.IndexOf("Microsoft.WindowsDesktop.App", StringComparison.Ordinal) < 0)
            {
                return "BlazeInteractionBridge runtimeconfig is not self-contained.";
            }

            var radarDirectory = Path.Combine(directory, "Providers", "Radar");
            var manifestPath = Path.Combine(radarDirectory, "provider.json");
            if (!File.Exists(manifestPath))
            {
                return "External Radar Provider manifest is missing.";
            }

            ProviderManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<ProviderManifest>(File.ReadAllText(manifestPath));
            }
            catch (Exception exception)
            {
                return "External Radar Provider manifest is invalid JSON: " + exception.Message;
            }

            if (manifest == null ||
                !string.Equals(manifest.id, "blaze.radar.f10f20", StringComparison.Ordinal) ||
                !string.Equals(manifest.version, expectedVersion, StringComparison.Ordinal) ||
                manifest.providerApiVersion != 1 ||
                !string.Equals(manifest.entryAssembly, "Blaze.Provider.Radar.dll", StringComparison.Ordinal) ||
                !string.Equals(manifest.entryType, "Blaze.Provider.Radar.RadarPlugin", StringComparison.Ordinal))
            {
                return "External Radar Provider manifest does not match Gate A Provider API 1 and version " + expectedVersion + ".";
            }

            if (!File.Exists(Path.Combine(radarDirectory, manifest.entryAssembly)))
            {
                return "External Radar Provider entry assembly is missing: " + manifest.entryAssembly;
            }

            var cameraDirectory = Path.Combine(directory, "Providers", "CameraVision");
            var cameraManifestPath = Path.Combine(cameraDirectory, "provider.json");
            if (!File.Exists(cameraManifestPath))
            {
                return "External CameraVision Provider manifest is missing.";
            }

            ProviderManifest cameraManifest;
            try
            {
                cameraManifest = JsonUtility.FromJson<ProviderManifest>(File.ReadAllText(cameraManifestPath));
            }
            catch (Exception exception)
            {
                return "External CameraVision Provider manifest is invalid JSON: " + exception.Message;
            }

            if (cameraManifest == null ||
                !string.Equals(cameraManifest.id, "blaze.camera.vision", StringComparison.Ordinal) ||
                !string.Equals(cameraManifest.version, expectedVersion, StringComparison.Ordinal) ||
                cameraManifest.providerApiVersion != 1 ||
                !string.Equals(cameraManifest.entryAssembly, "Blaze.Provider.CameraVision.dll", StringComparison.Ordinal) ||
                !string.Equals(cameraManifest.entryType, "Blaze.Provider.CameraVision.CameraVisionPlugin", StringComparison.Ordinal))
            {
                return "External CameraVision Provider manifest does not match Provider API 1 and version " + expectedVersion + ".";
            }

            if (!File.Exists(Path.Combine(cameraDirectory, cameraManifest.entryAssembly)))
            {
                return "External CameraVision Provider entry assembly is missing: " + cameraManifest.entryAssembly;
            }

            var nativeLibraries = Directory.GetFiles(
                cameraDirectory,
                "OpenCvSharpExtern.dll",
                SearchOption.AllDirectories);
            if (nativeLibraries.Length != 1)
            {
                return "External CameraVision Provider must contain exactly one OpenCvSharpExtern.dll native runtime.";
            }

            return null;
        }

        [Serializable]
        private sealed class ProviderManifest
        {
            public string id;
            public string version;
            public int providerApiVersion;
            public string entryAssembly;
            public string entryType;
        }
    }
}
