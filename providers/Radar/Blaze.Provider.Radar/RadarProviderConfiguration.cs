using Blaze.Interaction.Provider.Abstractions;
using Yuexin.Radar.Configuration;

namespace Blaze.Provider.Radar;

internal sealed record RadarProviderConfigurationResult(
    RadarAppConfiguration Configuration,
    string ConfigurationPath);

internal static class RadarProviderConfiguration
{
    internal static async Task<RadarProviderConfigurationResult> LoadAsync(
        string providerDirectory,
        IProviderStorageContext storage,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerDirectory);
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedProviderDirectory = Path.GetFullPath(providerDirectory);
        var configurationPath = string.IsNullOrWhiteSpace(storage.ProfilePath)
            ? Path.Combine(storage.GetProviderDataDirectory(RadarFrameAdapter.ProviderId), "config.json")
            : storage.ProfilePath;
        configurationPath = Path.GetFullPath(configurationPath);
        if (IsSameOrDescendant(normalizedProviderDirectory, configurationPath))
        {
            throw new ArgumentException("The writable Radar profile cannot be inside the provider package directory.", nameof(storage));
        }

        if (!File.Exists(configurationPath))
        {
            var bundledDefaultPath = Path.Combine(
                normalizedProviderDirectory,
                "profiles",
                "radar-default.json");
            CopyBundledDefaultIfMissing(bundledDefaultPath, configurationPath);
        }

        var configuration = await RadarConfigurationStore.LoadAsync(configurationPath, cancellationToken)
            .ConfigureAwait(false);
        return new RadarProviderConfigurationResult(configuration, configurationPath);
    }

    private static void CopyBundledDefaultIfMissing(string bundledDefaultPath, string configurationPath)
    {
        if (File.Exists(configurationPath))
        {
            return;
        }

        var destinationDirectory = Path.GetDirectoryName(configurationPath)
            ?? throw new InvalidOperationException("The configuration path must include a directory.");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(configurationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(bundledDefaultPath, temporaryPath, overwrite: false);
            try
            {
                File.Move(temporaryPath, configurationPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(configurationPath))
            {
                // Another Bridge won the concurrent first-start publication race.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool IsSameOrDescendant(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return string.Equals(relative, ".", StringComparison.Ordinal) ||
               (!Path.IsPathRooted(relative) &&
                !string.Equals(relative, "..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
