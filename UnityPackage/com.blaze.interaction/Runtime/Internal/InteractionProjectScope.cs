using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Blaze.Interaction.Internal
{
    internal sealed class InteractionProjectScope
    {
        internal InteractionProjectScope(string dataRoot, string profilePath, string pipeName)
        {
            DataRoot = dataRoot;
            ProfilePath = profilePath;
            PipeName = pipeName;
        }

        internal string DataRoot { get; }
        internal string ProfilePath { get; }
        internal string PipeName { get; }
    }

    internal static class InteractionProjectScopeResolver
    {
        internal static InteractionProjectScope Resolve(
            bool isEditor,
            string applicationDataPath,
            string persistentDataPath,
            string basePipeName,
            string profilePath)
        {
            var environmentRoot = ResolveEnvironmentRoot(isEditor, applicationDataPath, persistentDataPath);
            var dataRoot = Path.GetFullPath(Path.Combine(
                environmentRoot,
                isEditor ? "Library" : string.Empty,
                "BlazeInteraction"));
            var resolvedProfilePath = ResolveProfilePath(environmentRoot, profilePath);
            var resolvedPipeName = ResolvePipeName(dataRoot, basePipeName);

            return new InteractionProjectScope(dataRoot, resolvedProfilePath, resolvedPipeName);
        }

        private static string ResolveEnvironmentRoot(
            bool isEditor,
            string applicationDataPath,
            string persistentDataPath)
        {
            var sourcePath = isEditor ? applicationDataPath : persistentDataPath;
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException(
                    isEditor ? "The Unity Assets path is required." : "The player persistent data path is required.",
                    isEditor ? nameof(applicationDataPath) : nameof(persistentDataPath));
            }

            var fullPath = NormalizeDirectoryPath(sourcePath);
            if (!isEditor)
            {
                return fullPath;
            }

            var projectRoot = Directory.GetParent(fullPath);
            if (projectRoot == null)
            {
                throw new ArgumentException("The Unity Assets path must have a project root.", nameof(applicationDataPath));
            }

            return NormalizeDirectoryPath(projectRoot.FullName);
        }

        private static string NormalizeDirectoryPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return fullPath.Length > root.Length
                ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : fullPath;
        }

        private static string ResolveProfilePath(string environmentRoot, string profilePath)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                return null;
            }

            return Path.IsPathRooted(profilePath)
                ? Path.GetFullPath(profilePath)
                : Path.GetFullPath(Path.Combine(environmentRoot, profilePath));
        }

        private static string ResolvePipeName(string dataRoot, string basePipeName)
        {
            var pipeName = string.IsNullOrWhiteSpace(basePipeName)
                ? InteractionIpcProtocol.PipeName
                : basePipeName.Trim();
            var identity = dataRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(identity));
            }

            var suffix = BitConverter.ToString(hash).Replace("-", string.Empty).Substring(0, 16);
            return pipeName + "." + suffix;
        }
    }
}
