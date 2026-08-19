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
    ActivationFailed = 5,
    DescriptorMismatch = 6
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
    private ProviderLoadContext? _loadContext;

    internal LoadedProvider(
        ProviderManifest manifest,
        IInteractionProviderPlugin plugin,
        ProviderLoadContext loadContext)
    {
        Manifest = manifest;
        _plugin = plugin;
        _loadContext = loadContext;
    }

    public ProviderManifest Manifest { get; }

    public IInteractionProviderPlugin Plugin => Volatile.Read(ref _plugin)
        ?? throw new ObjectDisposedException(nameof(LoadedProvider));

    public ProviderLoadContext LoadContext => Volatile.Read(ref _loadContext)
        ?? throw new ObjectDisposedException(nameof(LoadedProvider));

    public void Dispose()
    {
        Interlocked.Exchange(ref _plugin, null);
        var loadContext = Interlocked.Exchange(ref _loadContext, null);
        if (loadContext is null)
        {
            return;
        }

        loadContext.Unload();
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
        string providerDirectory;
        string assemblyPath;
        try
        {
            manifest.Validate();
            if (manifest.ProviderApiVersion != ProviderApi.CurrentMajorVersion
                || !ProviderPathSecurity.TryGetSafeProviderRoot(entry.ProviderDirectory, out providerDirectory))
            {
                throw new FormatException();
            }

            var candidatePath = Path.Combine(providerDirectory, manifest.EntryAssembly);
            if (!ProviderPathSecurity.TryResolveContainedFile(providerDirectory, candidatePath, out assemblyPath))
            {
                throw new FormatException();
            }
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or FormatException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            throw new ProviderLoadException(
                ProviderLoadFailure.InvalidCatalogEntry,
                "The provider catalog entry failed security validation.");
        }

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
                assembly = context.LoadProviderAssembly(assemblyPath);
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

            try
            {
                ValidateDescriptor(manifest, plugin.Descriptor);
            }
            catch (ProviderLoadException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ProviderLoadException(
                    ProviderLoadFailure.DescriptorMismatch,
                    "The provider plugin descriptor could not be validated.",
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

    private static void ValidateDescriptor(ProviderManifest manifest, ProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var manifestVersion = Version.Parse(manifest.Version);
        var manifestCapabilities = manifest.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var descriptorCapabilities = descriptor.Capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.Equals(manifest.Id, descriptor.Id, StringComparison.Ordinal)
            || !string.Equals(manifest.DisplayName, descriptor.DisplayName, StringComparison.Ordinal)
            || manifestVersion != descriptor.Version
            || !string.Equals(manifest.Category, descriptor.Category, StringComparison.Ordinal)
            || manifest.Capabilities.Count != descriptor.Capabilities.Count
            || !manifestCapabilities.SetEquals(descriptorCapabilities))
        {
            throw new ProviderLoadException(
                ProviderLoadFailure.DescriptorMismatch,
                "The provider plugin descriptor does not match its manifest.");
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
            catch (Exception)
            {
                results.Add(new ProviderLoadResult(
                    entry,
                    null,
                    ProviderLoadFailure.AssemblyLoadFailed,
                    "Provider loading failed unexpectedly."));
            }
        }

        return results;
    }
}
