namespace Blaze.Interaction.Provider.Abstractions;

/// <summary>Describes the writable, project-scoped storage assigned to a provider host.</summary>
public interface IProviderStorageContext
{
    string DataRoot { get; }
    string? ProfilePath { get; }
    string GetProviderDataDirectory(string providerId);
}
