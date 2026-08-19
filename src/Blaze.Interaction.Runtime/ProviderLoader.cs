using System.Reflection;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Runtime;

public enum ProviderLoadFailure
{
    InvalidCatalogEntry = 0,
    MissingEntryAssembly = 1,
    AssemblyLoadFailed = 2,
    MissingEntryType = 3,
    InvalidPluginType = 4,
    ActivationFailed = 5
}

public sealed class ProviderLoadException : Exception
{
    public ProviderLoadException(ProviderLoadFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ProviderLoadFailure Failure { get; }
}

public sealed class LoadedProvider : IDisposable
{
    private IInteractionProviderPlugin? _plugin;
    private bool _disposed;

    internal LoadedProvider(
        ProviderManifest manifest,
        IInteractionProviderPlugin plugin,
        ProviderLoadContext loadContext)
    {
        Manifest = manifest;
        _plugin = plugin;
        LoadContext = loadContext;
    }

    public ProviderManifest Manifest { get; }

    public IInteractionProviderPlugin Plugin => _plugin
        ?? throw new ObjectDisposedException(nameof(LoadedProvider));

    public ProviderLoadContext LoadContext { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _plugin = null;
        LoadContext.Unload();
    }
}

public sealed record ProviderLoadResult(
    ProviderCatalogEntry CatalogEntry,
    LoadedProvider? Provider,
    ProviderLoadFailure? Failure,
    string? Error)
{
    public bool IsSuccess => Provider is not null && Failure is null;
}

public sealed class ProviderLoader
{
    public LoadedProvider Load(ProviderCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsAvailable || entry.Manifest is null)
        {
            throw new ProviderLoadException(
                ProviderLoadFailure.InvalidCatalogEntry,
                entry.Error ?? "The provider catalog entry is unavailable.");
        }

        var manifest = entry.Manifest;
        var assemblyPath = Path.GetFullPath(Path.Combine(entry.ProviderDirectory, manifest.EntryAssembly));
        if (!File.Exists(assemblyPath))
        {
            throw new ProviderLoadException(
                ProviderLoadFailure.MissingEntryAssembly,
                $"Provider entry assembly was not found: {manifest.EntryAssembly}");
        }

        var context = new ProviderLoadContext(assemblyPath);
        try
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadFromAssemblyPath(assemblyPath);
            }
            catch (Exception exception) when (exception is BadImageFormatException
                                              or FileLoadException
                                              or FileNotFoundException)
            {
                throw new ProviderLoadException(
                    ProviderLoadFailure.AssemblyLoadFailed,
                    $"Provider entry assembly could not be loaded: {manifest.EntryAssembly}",
                    exception);
            }

            var entryType = assembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false);
            if (entryType is null)
            {
                throw new ProviderLoadException(
                    ProviderLoadFailure.MissingEntryType,
                    $"Provider entry type was not found: {manifest.EntryType}");
            }

            if (!typeof(IInteractionProviderPlugin).IsAssignableFrom(entryType))
            {
                throw new ProviderLoadException(
                    ProviderLoadFailure.InvalidPluginType,
                    $"Provider entry type does not implement {nameof(IInteractionProviderPlugin)}: {manifest.EntryType}");
            }

            IInteractionProviderPlugin plugin;
            try
            {
                plugin = (IInteractionProviderPlugin)(Activator.CreateInstance(entryType)
                    ?? throw new InvalidOperationException("Provider activation returned null."));
            }
            catch (Exception exception) when (exception is TargetInvocationException
                                              or MissingMethodException
                                              or MemberAccessException
                                              or InvalidOperationException)
            {
                throw new ProviderLoadException(
                    ProviderLoadFailure.ActivationFailed,
                    $"Provider plugin activation failed: {manifest.EntryType}",
                    exception);
            }

            return new LoadedProvider(manifest, plugin, context);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    public IReadOnlyList<ProviderLoadResult> LoadAll(IEnumerable<ProviderCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var results = new List<ProviderLoadResult>();
        foreach (var entry in entries)
        {
            try
            {
                results.Add(new ProviderLoadResult(entry, Load(entry), null, null));
            }
            catch (ProviderLoadException exception)
            {
                results.Add(new ProviderLoadResult(entry, null, exception.Failure, exception.Message));
            }
            catch (Exception exception)
            {
                results.Add(new ProviderLoadResult(entry, null, ProviderLoadFailure.AssemblyLoadFailed, exception.Message));
            }
        }

        return results;
    }
}
