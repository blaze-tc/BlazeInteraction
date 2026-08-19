namespace Blaze.Interaction.Runtime;

internal static class ProviderPathSecurity
{
    internal static bool IsSimpleFileName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Path.IsPathRooted(value)
            && value.IndexOfAny(['/', '\\']) < 0
            && value is not "." and not ".."
            && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
    }

    internal static bool TryGetSafeProviderRoot(string? providerDirectory, out string fullRoot)
    {
        fullRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(providerDirectory))
        {
            return false;
        }

        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(providerDirectory));
            var directory = new DirectoryInfo(fullRoot);
            return directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            fullRoot = string.Empty;
            return false;
        }
    }

    internal static bool TryResolveContainedFile(
        string providerRoot,
        string candidatePath,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(providerRoot));
            var fullCandidate = Path.GetFullPath(candidatePath);
            if (!IsContained(fullRoot, fullCandidate) || HasReparseDirectoryBetween(fullRoot, fullCandidate))
            {
                return false;
            }

            var file = new FileInfo(fullCandidate);
            if (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = file.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !IsContained(fullRoot, target.FullName))
                {
                    return false;
                }

                resolvedPath = Path.GetFullPath(target.FullName);
                return true;
            }

            resolvedPath = fullCandidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasReparseDirectoryBetween(string fullRoot, string fullCandidate)
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(fullCandidate)!);
        while (!PathEquals(current.FullName, fullRoot))
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 || current.Parent is null)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool IsContained(string fullRoot, string fullCandidate)
    {
        if (PathEquals(fullRoot, fullCandidate))
        {
            return true;
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, PathComparison());
    }

    private static bool PathEquals(string first, string second)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(first),
            Path.TrimEndingDirectorySeparator(second),
            PathComparison());
    }

    private static StringComparison PathComparison()
    {
        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
