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
            return shared ?? Default.LoadFromAssemblyName(assemblyName);
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
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

        var resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (resolved is not null)
        {
            return resolved;
        }

        foreach (var fileName in CandidateFileNames(unmanagedDllName))
        {
            var candidates = new[]
            {
                Path.Combine(_providerDirectory, "native", RuntimeIdentifier(), fileName),
                Path.Combine(_providerDirectory, "native", fileName),
                Path.Combine(_providerDirectory, fileName)
            };

            var candidate = candidates.FirstOrDefault(File.Exists);
            if (candidate is not null)
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
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
