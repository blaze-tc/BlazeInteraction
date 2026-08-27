namespace Blaze.Interaction.Bridge.Wpf.Tests;

public sealed class ProviderViewLayoutTests
{
    [Fact]
    public void Selector_UsesSharedCardsAndPrimaryAction()
    {
        var xaml = ReadView("ProviderSelectorView.xaml");

        Assert.Contains("x:Name=\"ProviderChoiceList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource CardStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ConfirmProviderButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource PrimaryButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource AppBackgroundBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#667085", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#D0D5DD", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#B42318", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderHeader_UsesNamedConnectionStatesAndSharedResources()
    {
        var xaml = ReadView("ProviderHeaderView.xaml");

        Assert.Contains("x:Name=\"UnityConnectionStatus\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ProviderConnectionStatus\"", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource PrimaryBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("StaticResource PanelBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReturnToProviderSelectionButton\"", xaml, StringComparison.Ordinal);
    }

    private static string ReadView(string fileName) => File.ReadAllText(Path.Combine(
        FindRepositoryRoot(),
        "src",
        "Blaze.Interaction.Bridge.Wpf",
        "Views",
        fileName));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RadarControl.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Unable to locate RadarControl.sln from the test output directory.");
    }
}
