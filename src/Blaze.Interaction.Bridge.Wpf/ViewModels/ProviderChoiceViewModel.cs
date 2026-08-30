namespace Blaze.Interaction.Bridge.Wpf;

internal sealed record ProviderChoiceViewModel(
    string ProviderId,
    string ProviderInstanceId,
    string DisplayName,
    string Category,
    bool IsAvailable)
{
    internal static ProviderChoiceViewModel From(BridgeAvailableProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new ProviderChoiceViewModel(
            provider.ProviderId,
            provider.ProviderInstanceId,
            provider.DisplayName,
            provider.Category,
            provider.IsAvailable);
    }
}
