using System.IO;
using System.Text.Json;

namespace Blaze.Interaction.Bridge.Wpf;

internal sealed record BridgeSettings(int SchemaVersion, string? SelectedProviderId)
{
    public const int CurrentSchemaVersion = 1;

    public static BridgeSettings CreateDefault() => new(CurrentSchemaVersion, null);
}

internal sealed class BridgeSettingsException : Exception
{
    public BridgeSettingsException(string message)
        : base(message)
    {
    }

    public BridgeSettingsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class BridgeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public BridgeSettingsStore(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            throw new ArgumentException("Bridge data root cannot be blank.", nameof(dataRoot));
        }

        SettingsPath = Path.GetFullPath(Path.Combine(dataRoot, "bridge-settings.json"));
    }

    public string SettingsPath { get; }

    public async Task<BridgeSettings> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(SettingsPath))
        {
            return BridgeSettings.CreateDefault();
        }

        try
        {
            await using var stream = new FileStream(
                SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<BridgeSettings>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (settings is null)
            {
                throw new BridgeSettingsException(
                    "Bridge settings are invalid: JSON produced no settings.");
            }

            Validate(settings);
            return settings;
        }
        catch (BridgeSettingsException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new BridgeSettingsException(
                $"Bridge settings are invalid: {exception.Message}",
                exception);
        }
    }

    public async Task SaveAsync(
        BridgeSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("Bridge settings path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(SettingsPath)}.{Guid.NewGuid():N}.tmp");

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
                        settings,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task ResetAsync(CancellationToken cancellationToken) =>
        SaveAsync(BridgeSettings.CreateDefault(), cancellationToken);

    private static void Validate(BridgeSettings settings)
    {
        if (settings.SchemaVersion != BridgeSettings.CurrentSchemaVersion)
        {
            throw new BridgeSettingsException(
                $"Bridge settings schema version {settings.SchemaVersion} is unsupported.");
        }

        if (settings.SelectedProviderId is not null &&
            string.IsNullOrWhiteSpace(settings.SelectedProviderId))
        {
            throw new BridgeSettingsException(
                "Bridge settings selected provider ID cannot be blank.");
        }
    }
}
