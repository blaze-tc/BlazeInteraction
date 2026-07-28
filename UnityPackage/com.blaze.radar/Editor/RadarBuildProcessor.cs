using System;
using System.IO;
using System.Security.Cryptography;
using Blaze.Radar.Internal;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Blaze.Radar.Editor
{
    public sealed class RadarBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => 1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = RadarRuntimeSettings.LoadOrCreateRuntimeDefaults();
            var validation = RadarScreenTopologyValidator.Validate(settings.Screens);
            if (!validation.IsValid)
            {
                throw new BuildFailedException(
                    "Blaze Radar screen topology is invalid:" + Environment.NewLine +
                    string.Join(Environment.NewLine, validation.Errors));
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            var sourceDirectory = ResolveCurrentPackageBridgeDirectory();
            ValidateBridgePayload(sourceDirectory, "package source");

            var sourceExecutable = Path.Combine(sourceDirectory, BridgePathResolver.ExecutableName);
            var playerDirectory = Path.GetDirectoryName(report.summary.outputPath)
                ?? throw new BuildFailedException("Unable to resolve the player output directory.");
            var destinationDirectory = Path.Combine(playerDirectory, "RadarBridge");

            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }

            CopyDirectory(sourceDirectory, destinationDirectory);
            ValidateBridgePayload(destinationDirectory, "player destination");

            var destinationExecutable = Path.Combine(destinationDirectory, BridgePathResolver.ExecutableName);
            var sourceHash = ComputeSha256(sourceExecutable);
            var destinationHash = ComputeSha256(destinationExecutable);
            if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException(
                    $"RadarBridge copy verification failed. Source SHA-256 {sourceHash}, destination SHA-256 {destinationHash}.");
            }

            Debug.Log(
                $"Blaze Radar {UnitySdkVersion.Value}: copied the current package Bridge to '{destinationDirectory}'. " +
                $"RadarBridge.exe SHA-256: {destinationHash}");
        }

        private static string ResolveCurrentPackageBridgeDirectory()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(RadarBridgeLauncher).Assembly);
            if (packageInfo == null || string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
            {
                throw new BuildFailedException(
                    "Unable to resolve the installed com.blaze.radar package. Reinstall the package before building.");
            }

            if (!string.Equals(packageInfo.version, UnitySdkVersion.Value, StringComparison.Ordinal))
            {
                throw new BuildFailedException(
                    $"Blaze Radar package/code version mismatch. Package Manager resolved '{packageInfo.version}', " +
                    $"but the SDK code reports '{UnitySdkVersion.Value}'. Remove the package cache and reinstall the requested tag.");
            }

            var embeddedDirectory = BridgePathResolver.GetEmbeddedPublishDirectory(packageInfo.resolvedPath);
            var executable = Path.Combine(embeddedDirectory, BridgePathResolver.ExecutableName);
            if (!File.Exists(executable))
            {
                throw new BuildFailedException(
                    $"The current com.blaze.radar {packageInfo.version} package does not contain RadarBridge.exe. " +
                    $"Reinstall the package. Expected: {executable}");
            }

            return embeddedDirectory;
        }

        private static void ValidateBridgePayload(string directory, string location)
        {
            var validationError = BridgePayloadValidator.Validate(directory, UnitySdkVersion.Value);
            if (validationError != null)
                throw new BuildFailedException($"RadarBridge payload validation failed in the {location}: {validationError}");
        }

        private static string ComputeSha256(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
