using System.Text.Json;

namespace Blaze.Interaction.Runtime.Tests;

internal sealed class ProviderTestDirectory : IDisposable
{
    private readonly string _root;

    public ProviderTestDirectory()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "blaze-provider-tests",
            Guid.NewGuid().ToString("N"));
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
        var output = GetFixtureOutput(fixtureProjectName);

        Assert.True(Directory.Exists(output), $"Fixture output does not exist: {output}");
        foreach (var file in Directory.EnumerateFiles(output))
        {
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), overwrite: true);
        }

        WriteManifest(directory, entryAssembly, entryType, providerApiVersion, id);
        return directory;
    }

    public static string GetFixtureOutput(string fixtureProjectName)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the active test build configuration.");
        return Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "TestProviders",
            fixtureProjectName,
            "bin",
            configuration,
            "net8.0");
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
        Directory.Delete(_root, recursive: true);
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
