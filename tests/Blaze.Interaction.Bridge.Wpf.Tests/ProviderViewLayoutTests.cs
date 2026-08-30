using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blaze.Interaction.Provider.Abstractions;

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

    [Fact]
    public void ProviderHeader_ReturnButtonExecutesBoundNavigationCommand()
    {
        ExceptionDispatchInfo? failure = null;
        var navigationRequests = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var ownsApplication = Application.Current is null;
                var application = Application.Current ?? new Application();
                application.Resources["PanelBrush"] = Brushes.Black;
                application.Resources["BorderBrush"] = Brushes.Gray;
                application.Resources["PrimaryBrush"] = Brushes.Cyan;
                application.Resources["TextPrimaryBrush"] = Brushes.White;
                application.Resources["TextSecondaryBrush"] = Brushes.LightGray;
                var viewModel = new ProviderHeaderViewModel(
                        new BridgeHostSnapshot(
                            InteractionHostStatus.Disconnected,
                            null,
                            null),
                        () => navigationRequests++);
                var view = new ProviderHeaderView(viewModel);
                view.Measure(new Size(620, 80));
                view.Arrange(new Rect(0, 0, 620, 80));

                var button = Assert.IsType<Button>(view.FindName("ReturnToProviderSelectionButton"));
                Assert.True(button.IsEnabled);
                Assert.True(button.IsHitTestVisible);
                Assert.NotNull(button.GetBindingExpression(Button.CommandProperty));
                Assert.Same(viewModel.ReturnToSelectionCommand, button.Command);

                var onClick = typeof(Button).GetMethod(
                        "OnClick",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(onClick);
                onClick.Invoke(button, null);

                Assert.Equal(1, navigationRequests);
                if (ownsApplication)
                {
                    application.Shutdown();
                }
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Provider header window timed out.");
        failure?.Throw();
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
