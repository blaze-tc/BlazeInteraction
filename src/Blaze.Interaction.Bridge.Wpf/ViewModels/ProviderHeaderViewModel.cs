using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Blaze.Interaction.Bridge.Wpf;

internal sealed class ProviderHeaderViewModel : INotifyPropertyChanged
{
    private BridgeHostSnapshot _snapshot;

    internal ProviderHeaderViewModel(BridgeHostSnapshot snapshot, Action returnToSelection)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        ArgumentNullException.ThrowIfNull(returnToSelection);
        ReturnToSelectionCommand = new RelayCommand(returnToSelection);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string UnityStatus => _snapshot.Unity.IsConnected ? "Unity 已连接" : "Unity 未连接";
    public string ProviderName => _snapshot.ActiveProvider?.DisplayName ?? "未选择感应设备";
    public string ProviderStatus => _snapshot.ProviderStatus?.ToString() ?? "未运行";
    public ICommand ReturnToSelectionCommand { get; }

    internal void Apply(BridgeHostSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (Equals(_snapshot, snapshot))
        {
            return;
        }

        _snapshot = snapshot;
        OnPropertyChanged(nameof(UnityStatus));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(ProviderStatus));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
