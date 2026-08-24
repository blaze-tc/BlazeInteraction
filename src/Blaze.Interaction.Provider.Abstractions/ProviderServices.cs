using Blaze.Interaction.Contracts;

namespace Blaze.Interaction.Provider.Abstractions;

/// <summary>Describes the writable, project-scoped storage assigned to a provider host.</summary>
public interface IProviderStorageContext
{
    string DataRoot { get; }
    string? ProfilePath { get; }
    string GetProviderDataDirectory(string providerId);
}

/// <summary>Describes the authenticated Unity client currently connected to the interaction host.</summary>
public sealed record InteractionHostStatus(
    bool IsConnected,
    int ProcessId,
    string ClientVersion,
    IReadOnlyList<InteractionSurface> Surfaces)
{
    public static InteractionHostStatus Disconnected { get; } =
        new(false, 0, string.Empty, Array.Empty<InteractionSurface>());
}

/// <summary>Publishes authenticated Unity client status to interaction providers.</summary>
public interface IInteractionHostStatus
{
    InteractionHostStatus Current { get; }
    event Action<InteractionHostStatus>? Changed;
}
