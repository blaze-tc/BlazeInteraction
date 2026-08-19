using System.Text.Json;

namespace Blaze.Interaction.Runtime.Tests;

internal sealed class ProviderTestDirectory : IDisposable
{
    private static readonly string SessionRoot = CreateSessionRoot();
    private readonly string _root;

    public ProviderTestDirectory()
    {
        _root = Path.Combine(SessionRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string AddEmptyProvider(string directoryName)
    {
        var directory = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string AddBuiltProvider(
        string directoryName,
        string fixtureProjectName,
        string entryAssembly,
        string entryType,
        int providerApiVersion = 1,
        string id = "blaze.test.valid")
    {
        var directory = AddEmptyProvider(directoryName);
        var output = Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "TestProviders",
            fixtureProjectName,
            "bin",
            "Release",
            "net8.0");

        Assert.True(Directory.Exists(output), $"Fixture output does not exist: {output}");
        foreach (var file in Directory.EnumerateFiles(output))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        }

        WriteManifest(directory, entryAssembly, entryType, providerApiVersion, id);
        return directory;
    }

    public static void WriteManifest(
        string directory,
        string entryAssembly = "ValidProvider.dll",
        string entryType = "Blaze.Interaction.TestProviders.ValidPlugin",
        int providerApiVersion = 1,
        string id = "blaze.test.valid")
    {
        var manifest = new
        {
            id,
            displayName = "Valid test provider",
            version = "1.2.3",
            providerApiVersion,
            entryAssembly,
            entryType,
            category = "Test",
            capabilities = new[] { "interaction-point", "diagnostics" }
        };
        File.WriteAllText(
            Path.Combine(directory, "provider.json"),
            JsonSerializer.Serialize(manifest));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                           or IOException)
        {
            // Collectible AssemblyLoadContext releases mapped DLLs only after its
            // last managed reference leaves the test method. Process-exit cleanup
            // retries this process-unique directory after all tests have ended.
        }
    }

    private static string CreateSessionRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "blaze-provider-tests",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // The operating system will reclaim the process's mapped files.
            }
        };
        return root;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BlazeInteraction.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate BlazeInteraction.sln from the test output directory.");
    }
}
