using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Runtime;

public class ProviderLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(InteractionFrame).Assembly.GetName().Name!,
        typeof(IInteractionProviderPlugin).Assembly.GetName().Name!
    };

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _providerDirectory;

    public ProviderLoadContext(string mainAssemblyPath)
        : base(CreateName(mainAssemblyPath), isCollectible: true)
    {
        if (string.IsNullOrWhiteSpace(mainAssemblyPath))
        {
            throw new ArgumentException("A provider entry assembly path is required.", nameof(mainAssemblyPath));
        }

        var fullPath = Path.GetFullPath(mainAssemblyPath);
        _providerDirectory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The provider entry assembly path has no directory.", nameof(mainAssemblyPath));
        _resolver = new AssemblyDependencyResolver(fullPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            var shared = Default.Assemblies.FirstOrDefault(
                assembly => string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase));
            shared ??= Default.LoadFromAssemblyName(assemblyName);
            if (!HasExactIdentity(assemblyName, shared.GetName()))
            {
                throw new FileLoadException(
                    $"Shared contract assembly identity mismatch for '{assemblyName.Name}'.");
            }

            return shared;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is null)
        {
            return null;
        }

        if (!ProviderPathSecurity.TryResolveContainedFile(_providerDirectory, assemblyPath, out var resolvedPath))
        {
            throw new FileLoadException("A managed provider dependency resolved outside its provider directory.");
        }

        return LoadManagedAssembly(resolvedPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = ResolveUnmanagedLibraryPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }

    internal string? ResolveUnmanagedLibraryPath(string unmanagedDllName)
    {
        if (string.IsNullOrWhiteSpace(unmanagedDllName))
        {
            return null;
        }

        if (!ProviderPathSecurity.IsSimpleFileName(unmanagedDllName))
        {
            throw new FileLoadException("An unmanaged library request must be a simple provider-local file name.");
        }

        var resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (resolved is not null)
        {
            if (!ProviderPathSecurity.TryResolveContainedFile(_providerDirectory, resolved, out var resolvedPath))
            {
                throw new FileLoadException("An unmanaged provider dependency resolved outside its provider directory.");
            }

            return resolvedPath;
        }

        foreach (var fileName in CandidateFileNames(unmanagedDllName))
        {
            var candidates = new[]
            {
                Path.Combine(_providerDirectory, "native", RuntimeIdentifier(), fileName),
                Path.Combine(_providerDirectory, "native", fileName),
                Path.Combine(_providerDirectory, fileName)
            };

            foreach (var candidate in candidates.Where(File.Exists))
            {
                if (!ProviderPathSecurity.TryResolveContainedFile(_providerDirectory, candidate, out var resolvedCandidate))
                {
                    throw new FileLoadException("An unmanaged provider dependency resolved outside its provider directory.");
                }

                return resolvedCandidate;
            }
        }

        return null;
    }

    internal Assembly LoadProviderAssembly(string assemblyPath)
    {
        return LoadManagedAssembly(assemblyPath);
    }

    private Assembly LoadManagedAssembly(string assemblyPath)
    {
        using var assemblyStream = new FileStream(
            assemblyPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        return LoadFromStream(assemblyStream);
    }

    private static bool HasExactIdentity(AssemblyName requested, AssemblyName actual)
    {
        return string.Equals(requested.Name, actual.Name, StringComparison.OrdinalIgnoreCase)
            && requested.Version == actual.Version
            && string.Equals(requested.CultureName ?? string.Empty, actual.CultureName ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            && (requested.GetPublicKeyToken() ?? []).SequenceEqual(actual.GetPublicKeyToken() ?? []);
    }

    private static string CreateName(string mainAssemblyPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(mainAssemblyPath);
        return $"Blaze.Provider:{fileName}:{Guid.NewGuid():N}";
    }

    private static IEnumerable<string> CandidateFileNames(string unmanagedDllName)
    {
        if (Path.HasExtension(unmanagedDllName))
        {
            yield return unmanagedDllName;
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            yield return unmanagedDllName + ".dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "lib" + unmanagedDllName + ".dylib";
            yield return unmanagedDllName + ".dylib";
        }
        else
        {
            yield return "lib" + unmanagedDllName + ".so";
            yield return unmanagedDllName + ".so";
        }
    }

    private static string RuntimeIdentifier()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx";
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"{platform}-{architecture}";
    }
}
