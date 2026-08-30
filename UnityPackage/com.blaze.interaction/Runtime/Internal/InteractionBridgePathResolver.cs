using System;
using System.IO;

namespace Blaze.Interaction.Internal
{
    public static class InteractionBridgePathResolver
    {
        public const string RuntimeIdentifier = "win-x64";
        public const string ExecutableName = "BlazeInteractionBridge.exe";
        public const string PlayerDirectoryName = "BlazeInteractionBridge";

        public static string ResolveEditorExecutable(string configuredExecutable, string packageRoot)
        {
            if (!string.IsNullOrWhiteSpace(configuredExecutable))
            {
                return Path.GetFullPath(configuredExecutable);
            }

            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                throw new ArgumentException("The Unity package root is required.", nameof(packageRoot));
            }

            return Path.Combine(GetEmbeddedPublishDirectory(packageRoot), ExecutableName);
        }

        public static string ResolvePlayerExecutable(string playerDirectory)
        {
            if (string.IsNullOrWhiteSpace(playerDirectory))
            {
                throw new ArgumentException("The player directory is required.", nameof(playerDirectory));
            }

            return Path.Combine(
                Path.GetFullPath(playerDirectory),
                PlayerDirectoryName,
                ExecutableName);
        }

        public static string GetEmbeddedPublishDirectory(string packageRoot)
        {
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                throw new ArgumentException("The Unity package root is required.", nameof(packageRoot));
            }

            return Path.Combine(Path.GetFullPath(packageRoot), "Bridge~", RuntimeIdentifier);
        }
    }
}
