using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Blaze.Interaction.Contracts;
using Blaze.Interaction.Provider.Abstractions;
using Blaze.Interaction.Runtime;

namespace Blaze.Interaction.Runtime.Tests;

public sealed class ProviderLoaderTests
{
    [Fact]
    public void Load_ValidProvider_CreatesPluginInsideCollectibleIndependentContext()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var entry = AvailableEntry(fixture.Root);

        using var loaded = new ProviderLoader().Load(entry);

        Assert.Equal("blaze.test.valid", loaded.Plugin.Descriptor.Id);
        Assert.True(loaded.LoadContext.IsCollectible);
        Assert.NotSame(AssemblyLoadContext.Default, loaded.LoadContext);
        Assert.Same(loaded.LoadContext, AssemblyLoadContext.GetLoadContext(loaded.Plugin.GetType().Assembly));
    }

    [Fact]
    public void Load_ValidProvider_SharesContractAssembliesFromDefaultContext()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");

        using var loaded = new ProviderLoader().Load(AvailableEntry(fixture.Root));
        var pluginType = loaded.Plugin.GetType();
        var contractsAssembly = Assert.IsAssignableFrom<Assembly>(pluginType.GetMethod("GetContractsAssembly")!.Invoke(loaded.Plugin, null));
        var abstractionsAssembly = Assert.IsAssignableFrom<Assembly>(pluginType.GetMethod("GetAbstractionsAssembly")!.Invoke(loaded.Plugin, null));

        Assert.Same(typeof(InteractionFrame).Assembly, contractsAssembly);
        Assert.Same(typeof(IInteractionProviderPlugin).Assembly, abstractionsAssembly);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(contractsAssembly));
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(abstractionsAssembly));
    }

    [Fact]
    public void Load_MissingEntryAssembly_ThrowsTypedFailure()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddEmptyProvider("MissingAssembly");
        ProviderTestDirectory.WriteManifest(directory, entryAssembly: "Absent.dll");

        var exception = Assert.Throws<ProviderLoadException>(
            () => new ProviderLoader().Load(AvailableEntry(fixture.Root)));

        Assert.Equal(ProviderLoadFailure.MissingEntryAssembly, exception.Failure);
    }

    [Fact]
    public void Load_MissingEntryType_ThrowsTypedFailure()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "MissingType",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.DoesNotExist");

        var exception = Assert.Throws<ProviderLoadException>(
            () => new ProviderLoader().Load(AvailableEntry(fixture.Root)));

        Assert.Equal(ProviderLoadFailure.MissingEntryType, exception.Failure);
    }

    [Fact]
    public void Load_CorruptEntryAssembly_ReportsAssemblyLoadFailure()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddEmptyProvider("CorruptAssembly");
        File.WriteAllBytes(Path.Combine(directory, "Corrupt.dll"), [0x42, 0x4c, 0x41, 0x5a, 0x45]);
        ProviderTestDirectory.WriteManifest(directory, entryAssembly: "Corrupt.dll");

        var exception = Assert.Throws<ProviderLoadException>(
            () => new ProviderLoader().Load(AvailableEntry(fixture.Root)));

        Assert.Equal(ProviderLoadFailure.AssemblyLoadFailed, exception.Failure);
    }

    [Fact]
    public void Load_ThrowingPluginConstructor_ReportsActivationFailure()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "Throwing",
            "ThrowingProvider",
            "ThrowingProvider.dll",
            "Blaze.Interaction.TestProviders.ThrowingPlugin");

        var exception = Assert.Throws<ProviderLoadException>(
            () => new ProviderLoader().Load(AvailableEntry(fixture.Root)));

        Assert.Equal(ProviderLoadFailure.ActivationFailed, exception.Failure);
        Assert.IsType<InvalidOperationException>(exception.InnerException?.InnerException);
    }

    [Fact]
    public void LoadAll_BadProvider_DoesNotBlockValidProvider()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "A-Broken",
            "ThrowingProvider",
            "ThrowingProvider.dll",
            "Blaze.Interaction.TestProviders.ThrowingPlugin",
            id: "blaze.test.throwing");
        fixture.AddBuiltProvider(
            "B-Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var entries = new ProviderCatalog().Discover(fixture.Root);

        var results = new ProviderLoader().LoadAll(entries);

        Assert.Equal(2, results.Count);
        Assert.Equal(ProviderLoadFailure.ActivationFailed, results[0].Failure);
        Assert.Null(results[0].Provider);
        Assert.True(results[1].IsSuccess);
        var validProvider = Assert.IsType<LoadedProvider>(results[1].Provider);
        Assert.Equal("blaze.test.valid", validProvider.Plugin.Descriptor.Id);
        validProvider.Dispose();
    }

    [Fact]
    public void Load_TwoProvidersWithSameDependencyName_ResolvesEachProviderVersionInItsOwnContext()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "DependencyV1",
            "DependencyProviderV1",
            "DependencyProviderV1.dll",
            "Blaze.Interaction.TestProviders.DependencyV1Plugin",
            id: "blaze.test.dependency-v1");
        fixture.AddBuiltProvider(
            "DependencyV2",
            "DependencyProviderV2",
            "DependencyProviderV2.dll",
            "Blaze.Interaction.TestProviders.DependencyV2Plugin",
            id: "blaze.test.dependency-v2");
        var entries = new ProviderCatalog().Discover(fixture.Root);
        var loader = new ProviderLoader();

        using var first = loader.Load(entries[0]);
        using var second = loader.Load(entries[1]);
        var firstVersion = InvokeString(first.Plugin, "GetDependencyVersion");
        var secondVersion = InvokeString(second.Plugin, "GetDependencyVersion");
        var firstContext = InvokeString(first.Plugin, "GetDependencyLoadContextName");
        var secondContext = InvokeString(second.Plugin, "GetDependencyLoadContextName");

        Assert.Equal("1.0.0.0", firstVersion);
        Assert.Equal("2.0.0.0", secondVersion);
        Assert.True(first.LoadContext.IsCollectible);
        Assert.True(second.LoadContext.IsCollectible);
        Assert.NotEqual(firstContext, secondContext);
        Assert.Equal(first.LoadContext.Name, firstContext);
        Assert.Equal(second.LoadContext.Name, secondContext);
    }

    [Fact]
    public void LoadUnmanagedDll_ProviderLocalRuntimeDirectory_IsUsedByNativeLoadPath()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddBuiltProvider(
            "Native",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var runtimeDirectory = Path.Combine(directory, "native", RuntimeNativeDirectoryName());
        Directory.CreateDirectory(runtimeDirectory);
        File.WriteAllBytes(Path.Combine(runtimeDirectory, "fixture_native.dll"), [0x00]);
        var mainAssemblyPath = Path.Combine(directory, "ValidProvider.dll");
        var context = new NativeProbeLoadContext(mainAssemblyPath);

        Assert.ThrowsAny<Exception>(() => context.Probe("fixture_native"));
        Assert.Equal(
            Path.Combine(runtimeDirectory, "fixture_native.dll"),
            context.ResolveUnmanagedLibraryPath("fixture_native"));
        context.Unload();
    }

    [Fact]
    public void Load_HandcraftedEntryWithAbsoluteAssemblyPath_IsRejectedBeforeLoading()
    {
        using var fixture = new ProviderTestDirectory();
        var validDirectory = fixture.AddBuiltProvider(
            "Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var manifest = CreateManifest(entryAssembly: Path.Combine(validDirectory, "ValidProvider.dll"));
        var entry = new ProviderCatalogEntry(validDirectory, manifest, null, null);

        var exception = Assert.Throws<ProviderLoadException>(() => new ProviderLoader().Load(entry));

        Assert.Equal(ProviderLoadFailure.InvalidCatalogEntry, exception.Failure);
        Assert.DoesNotContain(validDirectory, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_HandcraftedEntryWithParentTraversal_IsRejectedBeforeLoading()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var attackerDirectory = fixture.AddEmptyProvider("Attacker");
        var separator = Path.DirectorySeparatorChar;
        var manifest = CreateManifest(entryAssembly: $"..{separator}Valid{separator}ValidProvider.dll");
        var entry = new ProviderCatalogEntry(attackerDirectory, manifest, null, null);

        var exception = Assert.Throws<ProviderLoadException>(() => new ProviderLoader().Load(entry));

        Assert.Equal(ProviderLoadFailure.InvalidCatalogEntry, exception.Failure);
    }

    [Fact]
    public void Load_ProviderRootDirectoryLink_IsRejectedBeforeLoadingOutsideRoot()
    {
        using var fixture = new ProviderTestDirectory();
        var outsideDirectory = fixture.AddBuiltProvider(
            "Outside",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var linkedDirectory = Path.Combine(fixture.Root, "LinkedRoot");
        Directory.CreateSymbolicLink(linkedDirectory, outsideDirectory);
        var entry = new ProviderCatalogEntry(linkedDirectory, CreateManifest(), null, null);

        var exception = Assert.Throws<ProviderLoadException>(() => new ProviderLoader().Load(entry));

        Assert.Equal(ProviderLoadFailure.InvalidCatalogEntry, exception.Failure);
    }

    [Fact]
    public void Load_EntryAssemblyLinkOutsideProviderRoot_IsRejectedBeforeLoading()
    {
        using var fixture = new ProviderTestDirectory();
        var outsideDirectory = fixture.AddBuiltProvider(
            "Outside",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var providerDirectory = fixture.AddEmptyProvider("Provider");
        File.CreateSymbolicLink(
            Path.Combine(providerDirectory, "ValidProvider.dll"),
            Path.Combine(outsideDirectory, "ValidProvider.dll"));
        var entry = new ProviderCatalogEntry(providerDirectory, CreateManifest(), null, null);

        var exception = Assert.Throws<ProviderLoadException>(() => new ProviderLoader().Load(entry));

        Assert.Equal(ProviderLoadFailure.InvalidCatalogEntry, exception.Failure);
    }

    [Fact]
    public void Load_PluginDescriptorIdMismatch_IsRejectedAndDoesNotLeakProviderPath()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "Mismatch",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin",
            id: "blaze.test.impostor");

        var exception = Assert.Throws<ProviderLoadException>(
            () => new ProviderLoader().Load(AvailableEntry(fixture.Root)));

        Assert.Equal(ProviderLoadFailure.DescriptorMismatch, exception.Failure);
        Assert.DoesNotContain(fixture.Root, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/escape")]
    [InlineData("sub\\escape")]
    public void ResolveUnmanagedLibraryPath_NameWithDirectoryComponent_IsRejected(string libraryName)
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddBuiltProvider(
            "Native",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var context = new NativeProbeLoadContext(Path.Combine(directory, "ValidProvider.dll"));

        Assert.Throws<FileLoadException>(() => context.ResolveUnmanagedLibraryPath(libraryName));
        context.Unload();
    }

    [Fact]
    public void ResolveUnmanagedLibraryPath_AbsoluteName_IsRejected()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddBuiltProvider(
            "Native",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var context = new NativeProbeLoadContext(Path.Combine(directory, "ValidProvider.dll"));

        Assert.Throws<FileLoadException>(() => context.ResolveUnmanagedLibraryPath(Path.Combine(directory, "native.dll")));
        context.Unload();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("culture")]
    [InlineData("public-key-token")]
    public void Load_SharedAssemblyIdentityMismatch_IsRejectedInsteadOfBindingBySimpleName(string mismatch)
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddBuiltProvider(
            "Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var context = new NativeProbeLoadContext(Path.Combine(directory, "ValidProvider.dll"));
        var requested = new AssemblyName(typeof(InteractionFrame).Assembly.FullName!);
        switch (mismatch)
        {
            case "version":
                requested.Version = new Version(requested.Version!.Major + 1, 0, 0, 0);
                break;
            case "culture":
                requested.CultureName = "fr-FR";
                break;
            case "public-key-token":
                requested.SetPublicKeyToken([1, 2, 3, 4, 5, 6, 7, 8]);
                break;
        }

        Assert.Throws<FileLoadException>(() => context.ProbeManaged(requested));
        context.Unload();
    }

    [Fact]
    public void Load_SharedAssemblyExactIdentity_ReturnsDefaultAssembly()
    {
        using var fixture = new ProviderTestDirectory();
        var directory = fixture.AddBuiltProvider(
            "Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var context = new NativeProbeLoadContext(Path.Combine(directory, "ValidProvider.dll"));

        var resolved = context.ProbeManaged(new AssemblyName(typeof(InteractionFrame).Assembly.FullName!));

        Assert.Same(typeof(InteractionFrame).Assembly, resolved);
        context.Unload();
    }

    [Fact]
    public void Dispose_ClearsPluginAndContextReferencesSoCollectibleContextActuallyUnloads()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var loaded = new ProviderLoader().Load(AvailableEntry(fixture.Root));
        var (pluginReference, contextReference) = CaptureReferencesAndDispose(loaded);
        CollectUntilDead(pluginReference, contextReference);

        Assert.False(pluginReference.IsAlive);
        Assert.False(contextReference.IsAlive);
        Assert.Throws<ObjectDisposedException>(() => loaded.Plugin);
        Assert.Throws<ObjectDisposedException>(() => loaded.LoadContext);
        GC.KeepAlive(loaded);
    }

    [Fact]
    public void Dispose_ConcurrentWithPluginAccess_IsIdempotentAndOnlyReportsDisposedState()
    {
        using var fixture = new ProviderTestDirectory();
        fixture.AddBuiltProvider(
            "Valid",
            "ValidProvider",
            "ValidProvider.dll",
            "Blaze.Interaction.TestProviders.ValidPlugin");
        var loaded = new ProviderLoader().Load(AvailableEntry(fixture.Root));
        var unexpected = new ConcurrentQueue<Exception>();

        Parallel.For(0, 500, iteration =>
        {
            try
            {
                if (iteration % 3 == 0)
                {
                    loaded.Dispose();
                }
                else
                {
                    _ = loaded.Plugin;
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception exception)
            {
                unexpected.Enqueue(exception);
            }
        });

        Assert.Empty(unexpected);
        loaded.Dispose();
        Assert.Throws<ObjectDisposedException>(() => loaded.Plugin);
        Assert.Throws<ObjectDisposedException>(() => loaded.LoadContext);
    }

    private static ProviderCatalogEntry AvailableEntry(string root)
    {
        var entry = Assert.Single(new ProviderCatalog().Discover(root));
        Assert.True(entry.IsAvailable, entry.Error);
        return entry;
    }

    private static string InvokeString(IInteractionProviderPlugin plugin, string method)
    {
        return Assert.IsType<string>(plugin.GetType().GetMethod(method)!.Invoke(plugin, null));
    }

    private static ProviderManifest CreateManifest(string entryAssembly = "ValidProvider.dll")
    {
        return new ProviderManifest
        {
            Id = "blaze.test.valid",
            DisplayName = "Valid test provider",
            Version = "1.2.3",
            ProviderApiVersion = 1,
            EntryAssembly = entryAssembly,
            EntryType = "Blaze.Interaction.TestProviders.ValidPlugin",
            Category = "Test",
            Capabilities = ["interaction-point", "diagnostics"]
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Plugin, WeakReference Context) CaptureReferencesAndDispose(LoadedProvider loaded)
    {
        var plugin = loaded.Plugin;
        var context = loaded.LoadContext;
        var references = (new WeakReference(plugin), new WeakReference(context));
        loaded.Dispose();
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectUntilDead(params WeakReference[] references)
    {
        for (var attempt = 0; attempt < 10 && references.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static string RuntimeNativeDirectoryName()
    {
        var platform = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return $"{platform}-{architecture}";
    }

    private sealed class NativeProbeLoadContext(string mainAssemblyPath) : ProviderLoadContext(mainAssemblyPath)
    {
        public nint Probe(string unmanagedDllName) => LoadUnmanagedDll(unmanagedDllName);

        public Assembly? ProbeManaged(AssemblyName assemblyName) => Load(assemblyName);
    }
}
