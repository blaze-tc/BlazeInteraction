using System.Text.Json;
using System.Text.Json.Serialization;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Provider.CameraVision;

internal sealed record CameraVisionConfigurationLoadResult(
    CameraVisionConfiguration? Configuration,
    string ConfigurationPath,
    string? Error)
{
    public bool IsSuccess => Configuration is not null && Error is null;
}

internal sealed class CameraVisionConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public CameraVisionConfigurationStore(IProviderStorageContext storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        var providerDirectory = storage.GetProviderDataDirectory(CameraVisionPlugin.ProviderId);
        if (string.IsNullOrWhiteSpace(providerDirectory))
        {
            throw new ArgumentException(
                "Provider storage returned an empty CameraVision directory.",
                nameof(storage));
        }

        ConfigurationPath = Path.GetFullPath(
            Path.Combine(providerDirectory, "camera-vision.json"));
    }

    public string ConfigurationPath { get; }

    public async Task<CameraVisionConfigurationLoadResult> LoadOrCreateAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(ConfigurationPath))
        {
            try
            {
                await WriteAtomicallyAsync(
                    CameraVisionConfiguration.CreateDefault(),
                    overwrite: false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (File.Exists(ConfigurationPath))
            {
                // Another provider host won the first-write race.
            }
        }

        try
        {
            await using var stream = new FileStream(
                ConfigurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var configuration = await JsonSerializer.DeserializeAsync<CameraVisionConfiguration>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (configuration is null)
            {
                return Invalid("CameraVision configuration is invalid: JSON produced no configuration.");
            }

            return new CameraVisionConfigurationLoadResult(
                configuration,
                ConfigurationPath,
                Error: null);
        }
        catch (Exception exception) when (IsConfigurationError(exception))
        {
            return Invalid($"CameraVision configuration is invalid: {exception.Message}");
        }
    }

    public Task SaveAsync(
        CameraVisionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return WriteAtomicallyAsync(configuration, overwrite: true, cancellationToken);
    }

    public async Task<CameraVisionConfiguration> ResetAsync(CancellationToken cancellationToken)
    {
        var configuration = CameraVisionConfiguration.CreateDefault();
        await SaveAsync(configuration, cancellationToken).ConfigureAwait(false);
        return configuration;
    }

    private async Task WriteAtomicallyAsync(
        CameraVisionConfiguration configuration,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(ConfigurationPath)
            ?? throw new InvalidOperationException("CameraVision configuration path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(ConfigurationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        configuration,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, ConfigurationPath, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private CameraVisionConfigurationLoadResult Invalid(string error) =>
        new(null, ConfigurationPath, error);

    private static bool IsConfigurationError(Exception exception) =>
        exception is JsonException or NotSupportedException or ArgumentException;
}
