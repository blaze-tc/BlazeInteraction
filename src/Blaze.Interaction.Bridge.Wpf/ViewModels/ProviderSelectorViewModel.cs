using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;

namespace Blaze.Interaction.Bridge.Wpf;

internal interface IBridgeProviderSelection
{
    IReadOnlyList<BridgeAvailableProvider> AvailableProviders { get; }

    Task SelectProviderAsync(string providerId, CancellationToken cancellationToken);
}

internal interface IBridgeUiDispatcher
{
    Task InvokeAsync(Action action);
}

internal sealed class WpfBridgeUiDispatcher(Dispatcher dispatcher) : IBridgeUiDispatcher
{
    private readonly Dispatcher _dispatcher = dispatcher
        ?? throw new ArgumentNullException(nameof(dispatcher));

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return _dispatcher.InvokeAsync(action).Task;
    }
}

internal sealed class ProviderSelectorViewModel : INotifyPropertyChanged
{
    private readonly IBridgeProviderSelection _selection;
    private readonly IBridgeUiDispatcher _dispatcher;
    private readonly AsyncRelayCommand _confirmCommand;
    private readonly RelayCommand _cancelCommand;
    private ProviderChoiceViewModel? _selectedChoice;
    private string? _errorMessage;
    private bool _isBusy;
    private int _confirmationInFlight;

    internal ProviderSelectorViewModel(
        IBridgeProviderSelection selection,
        IBridgeUiDispatcher dispatcher,
        string? savedProviderId,
        bool canCancel)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        CanCancel = canCancel;
        Choices = Array.AsReadOnly(
            _selection.AvailableProviders.Select(ProviderChoiceViewModel.From).ToArray());
        _confirmCommand = new AsyncRelayCommand(
            ConfirmSelectedAsync,
            () => SelectedChoice?.IsAvailable == true && !IsBusy);
        _cancelCommand = new RelayCommand(
            Cancel,
            () => CanCancel && !IsBusy);

        if (!string.IsNullOrWhiteSpace(savedProviderId))
        {
            _selectedChoice = Choices.FirstOrDefault(choice =>
                choice.IsAvailable &&
                string.Equals(choice.ProviderId, savedProviderId, StringComparison.Ordinal));
            if (_selectedChoice is null)
            {
                _errorMessage =
                    $"The saved sensing provider '{savedProviderId}' is not available. Select a provider to continue.";
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    internal event Action<ProviderChoiceViewModel>? Confirmed;
    internal event Action? Cancelled;

    public IReadOnlyList<ProviderChoiceViewModel> Choices { get; }

    public ProviderChoiceViewModel? SelectedChoice
    {
        get => _selectedChoice;
        set
        {
            if (Equals(_selectedChoice, value))
            {
                return;
            }

            _selectedChoice = value;
            OnPropertyChanged();
            _confirmCommand.RaiseCanExecuteChanged();
        }
    }

    public ICommand ConfirmCommand => _confirmCommand;
    public ICommand CancelCommand => _cancelCommand;
    public bool CanCancel { get; }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (string.Equals(_errorMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            _errorMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
            _confirmCommand.RaiseCanExecuteChanged();
            _cancelCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ConfirmSelectedAsync()
    {
        var choice = SelectedChoice;
        if (choice?.IsAvailable != true ||
            Interlocked.CompareExchange(ref _confirmationInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                ErrorMessage = null;
                IsBusy = true;
            }).ConfigureAwait(false);

            await _selection.SelectProviderAsync(
                    choice.ProviderId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => Confirmed?.Invoke(choice)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => ErrorMessage = exception.Message)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _confirmationInFlight, 0);
            await _dispatcher.InvokeAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private void Cancel()
    {
        if (CanCancel && !IsBusy)
        {
            Cancelled?.Invoke();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RelayCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                execute();
            }
        }

        internal void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class AsyncRelayCommand(
        Func<Task> execute,
        Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => canExecute();

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
            {
                _ = execute();
            }
        }

        internal void RaiseCanExecuteChanged() =>
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
