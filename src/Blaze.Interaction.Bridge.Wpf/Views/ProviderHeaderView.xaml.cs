using System.Windows.Controls;

namespace Blaze.Interaction.Bridge.Wpf;

public partial class ProviderHeaderView : UserControl
{
    internal ProviderHeaderView(ProviderHeaderViewModel viewModel)
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
    }
}
