using System;
using System.IO;
using System.Security.Cryptography;
using Blaze.Interaction.Internal;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Blaze.Interaction.Editor
{
    public sealed class InteractionBuildProcessor : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder { get { return 1000; } }

        public void OnPreprocessBuild(BuildReport report)
        {
            var settings = InteractionRuntimeSettings.LoadOrCreateRuntimeDefaults();
            var validation = InteractionSurfaceTopologyValidator.Validate(settings.Surfaces);
            if (!validation.IsValid)
            {
                throw new BuildFailedException(
                    "Blaze Interaction surface topology is invalid:" + Environment.NewLine +
                    string.Join(Environment.NewLine, validation.Errors));
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            var sourceDirectory = ResolveCurrentPackageBridgeDirectory();
            var playerDirectory = Path.GetDirectoryName(report.summary.outputPath);
            if (string.IsNullOrWhiteSpace(playerDirectory))
            {
                throw new BuildFailedException("Unable to resolve the player output directory.");
            }

            var destinationDirectory = Path.Combine(
                playerDirectory,
                InteractionBridgePathResolver.PlayerDirectoryName);
            var hash = CopyValidatedPayload(sourceDirectory, destinationDirectory, InteractionSdkVersion.Value);
            Debug.Log(
                "Blaze Interaction " + InteractionSdkVersion.Value +
                ": copied the current package Bridge to '" + destinationDirectory +
                "'. BlazeInteractionBridge.exe SHA-256: " + hash);
        }

        internal static string CopyValidatedPayloadForTests(
            string sourceDirectory,
            string destinationDirectory,
            string expectedVersion)
        {
            return CopyValidatedPayload(sourceDirectory, destinationDirectory, expectedVersion);
        }

        internal static string ComputeSha256ForTests(string path)
        {
            return ComputeSha256(path);
        }

        private static string CopyValidatedPayload(
            string sourceDirectory,
            string destinationDirectory,
            string expectedVersion)
        {
            ValidatePayload(sourceDirectory, expectedVersion, "package source");
            var sourceExecutable = Path.Combine(sourceDirectory, InteractionBridgePathResolver.ExecutableName);
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, true);
            }

            CopyDirectory(sourceDirectory, destinationDirectory);
            ValidatePayload(destinationDirectory, expectedVersion, "player destination");
            var destinationExecutable = Path.Combine(destinationDirectory, InteractionBridgePathResolver.ExecutableName);
            var sourceHash = ComputeSha256(sourceExecutable);
            var destinationHash = ComputeSha256(destinationExecutable);
            if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new BuildFailedException(
                    "BlazeInteractionBridge copy verification failed. Source SHA-256 " + sourceHash +
                    ", destination SHA-256 " + destinationHash + ".");
            }

            return destinationHash;
        }

        private static string ResolveCurrentPackageBridgeDirectory()
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(InteractionBridgeLauncher).Assembly);
            if (packageInfo == null || string.IsNullOrWhiteSpace(packageInfo.resolvedPath))
            {
                throw new BuildFailedException(
                    "Unable to resolve the installed com.blaze.interaction package. Reinstall it before building.");
            }

            if (!string.Equals(packageInfo.version, InteractionSdkVersion.Value, StringComparison.Ordinal))
            {
                throw new BuildFailedException(
                    "Blaze Interaction package/code version mismatch. Package Manager resolved '" +
                    packageInfo.version + "', but SDK code reports '" + InteractionSdkVersion.Value + "'.");
            }

            return InteractionBridgePathResolver.GetEmbeddedPublishDirectory(packageInfo.resolvedPath);
        }

        private static void ValidatePayload(string directory, string expectedVersion, string location)
        {
            var error = InteractionBridgePayloadValidator.Validate(directory, expectedVersion);
            if (error != null)
            {
                throw new BuildFailedException(
                    "BlazeInteractionBridge payload validation failed in the " + location + ": " + error);
            }
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
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
            }

            foreach (var directory in Directory.GetDirectories(source))
            {
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
