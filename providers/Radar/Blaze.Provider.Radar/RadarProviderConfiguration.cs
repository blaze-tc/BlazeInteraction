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

        var configurationPath = string.IsNullOrWhiteSpace(storage.ProfilePath)
            ? Path.Combine(storage.GetProviderDataDirectory(RadarFrameAdapter.ProviderId), "config.json")
            : storage.ProfilePath;
        configurationPath = Path.GetFullPath(configurationPath);

        if (!File.Exists(configurationPath))
        {
            var bundledDefaultPath = Path.Combine(
                Path.GetFullPath(providerDirectory),
                "profiles",
                "radar-default.json");
            Directory.CreateDirectory(Path.GetDirectoryName(configurationPath)!);
            File.Copy(bundledDefaultPath, configurationPath, overwrite: false);
        }

        var configuration = await RadarConfigurationStore.LoadAsync(configurationPath, cancellationToken)
            .ConfigureAwait(false);
        return new RadarProviderConfigurationResult(configuration, configurationPath);
    }
}
