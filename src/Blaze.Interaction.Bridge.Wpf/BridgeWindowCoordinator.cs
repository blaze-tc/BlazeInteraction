using System.Windows;
using System.Windows.Controls;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Bridge.Wpf;

internal interface IBridgeWindowHost : IBridgeProviderSelection
{
    BridgeHostSnapshot CurrentSnapshot { get; }
    event EventHandler? ActiveProviderChanged;
    event Action<BridgeHostSnapshot>? SnapshotChanged;
    object? CreateActiveProviderSettingsView(IProviderSettingsContext context);
}

internal interface IBridgeWindowAdapter
{
    object? Content { get; set; }
    bool IsVisible { get; }
    bool IsMinimized { get; set; }
    event EventHandler? Closed;
    void Show();
    void Hide();
    void Close();
}

internal interface IBridgeWindowFactory
{
    IBridgeWindowAdapter CreateSelectorWindow(ProviderSelectorViewModel viewModel);
    IBridgeWindowAdapter AdaptProviderWindow(object providerView);
    object CreateProviderHeader(ProviderHeaderViewModel viewModel);
    object WrapProviderContent(object header, object? originalContent);
}

internal sealed class BridgeWindowCoordinator : IDisposable
{
    private readonly IBridgeWindowHost _host;
    private readonly IProviderSettingsContext _settingsContext;
    private readonly IBridgeWindowFactory _windows;
    private readonly IBridgeUiDispatcher _dispatcher;
    private readonly bool _launchMinimized;
    private readonly Action _requestShutdown;
    private readonly string? _startupError;
    private IBridgeWindowAdapter? _selectorWindow;
    private IBridgeWindowAdapter? _providerWindow;
    private ProviderHeaderViewModel? _headerViewModel;
    private string? _presentedProviderInstanceId;
    private int _navigationCloseDepth;
    private int _disposed;

    internal BridgeWindowCoordinator(
        IBridgeWindowHost host,
        IProviderSettingsContext settingsContext,
        IBridgeWindowFactory windows,
        IBridgeUiDispatcher dispatcher,
        bool launchMinimized,
        Action requestShutdown,
        string? startupError = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settingsContext = settingsContext ?? throw new ArgumentNullException(nameof(settingsContext));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _launchMinimized = launchMinimized;
        _requestShutdown = requestShutdown ?? throw new ArgumentNullException(nameof(requestShutdown));
        _startupError = startupError;
        _host.ActiveProviderChanged += OnActiveProviderChanged;
        _host.SnapshotChanged += OnSnapshotChanged;
    }

    internal void Start(BridgeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var savedAvailable = settings.SelectedProviderId is not null &&
                             _host.AvailableProviders.Any(provider =>
                                 provider.IsAvailable &&
                                 string.Equals(
                                     provider.ProviderId,
                                     settings.SelectedProviderId,
                                     StringComparison.Ordinal));
        if (!savedAvailable)
        {
            ShowSelector(settings.SelectedProviderId, canCancel: false);
            return;
        }

        PresentActiveProvider();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _host.ActiveProviderChanged -= OnActiveProviderChanged;
        _host.SnapshotChanged -= OnSnapshotChanged;
        CloseForNavigation(_selectorWindow);
        CloseForNavigation(_providerWindow);
        _selectorWindow = null;
        _providerWindow = null;
    }

    private void OnActiveProviderChanged(object? sender, EventArgs eventArgs)
    {
        if (_selectorWindow?.IsVisible == true)
        {
            return;
        }

        _ = _dispatcher.InvokeAsync(PresentActiveProvider);
    }

    private void OnSnapshotChanged(BridgeHostSnapshot snapshot) =>
        _ = _dispatcher.InvokeAsync(() => _headerViewModel?.Apply(snapshot));

    private void PresentActiveProvider()
    {
        var active = _host.CurrentSnapshot.ActiveProvider;
        if (active is null)
        {
            return;
        }

        if (_providerWindow is not null &&
            string.Equals(
                _presentedProviderInstanceId,
                active.ProviderInstanceId,
                StringComparison.Ordinal))
        {
            _providerWindow.Show();
            return;
        }

        object? providerView;
        try
        {
            providerView = _host.CreateActiveProviderSettingsView(_settingsContext);
        }
        catch (Exception exception)
        {
            ShowSelector(
                active.ProviderId,
                canCancel: _providerWindow is not null,
                exception.Message);
            return;
        }

        if (providerView is null)
        {
            ShowSelector(
                active.ProviderId,
                canCancel: _providerWindow is not null,
                $"{active.DisplayName} did not provide a settings window.");
            return;
        }

        var nextWindow = _windows.AdaptProviderWindow(providerView);
        var nextHeader = new ProviderHeaderViewModel(_host.CurrentSnapshot, ReturnToSelection);
        var headerView = _windows.CreateProviderHeader(nextHeader);
        var originalContent = nextWindow.Content;
        nextWindow.Content = null;
        nextWindow.Content = _windows.WrapProviderContent(headerView, originalContent);
        nextWindow.Closed += OnWindowClosed;

        var previous = _providerWindow;
        _providerWindow = nextWindow;
        _headerViewModel = nextHeader;
        _presentedProviderInstanceId = active.ProviderInstanceId;
        if (previous is not null && !ReferenceEquals(previous, nextWindow))
        {
            CloseForNavigation(previous);
        }

        nextWindow.Show();
        nextWindow.IsMinimized = _launchMinimized;
    }

    private void ReturnToSelection()
    {
        _providerWindow?.Hide();
        ShowSelector(_host.CurrentSnapshot.ActiveProvider?.ProviderId, canCancel: true);
    }

    private void ShowSelector(
        string? selectedProviderId,
        bool canCancel,
        string? runtimeError = null)
    {
        CloseForNavigation(_selectorWindow);
        var viewModel = new ProviderSelectorViewModel(
            _host,
            _dispatcher,
            selectedProviderId,
            canCancel);
        if (!string.IsNullOrWhiteSpace(_startupError))
        {
            viewModel.SetError(_startupError);
        }
        if (!string.IsNullOrWhiteSpace(runtimeError))
        {
            viewModel.SetError(runtimeError);
        }
        viewModel.Cancelled += RestoreProviderWindow;
        viewModel.Confirmed += OnSelectionConfirmed;
        var window = _windows.CreateSelectorWindow(viewModel);
        window.Closed += OnWindowClosed;
        _selectorWindow = window;
        window.IsMinimized = false;
        window.Show();
    }

    private void RestoreProviderWindow()
    {
        CloseForNavigation(_selectorWindow);
        _selectorWindow = null;
        _providerWindow?.Show();
    }

    private void OnSelectionConfirmed(ProviderChoiceViewModel choice)
    {
        CloseForNavigation(_selectorWindow);
        _selectorWindow = null;
        PresentActiveProvider();
    }

    private void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (Volatile.Read(ref _navigationCloseDepth) == 0 &&
            Volatile.Read(ref _disposed) == 0)
        {
            _requestShutdown();
        }
    }

    private void CloseForNavigation(IBridgeWindowAdapter? window)
    {
        if (window is null)
        {
            return;
        }

        Interlocked.Increment(ref _navigationCloseDepth);
        try
        {
            window.Closed -= OnWindowClosed;
            window.Close();
        }
        finally
        {
            Interlocked.Decrement(ref _navigationCloseDepth);
        }
    }
}

internal sealed class WpfBridgeWindowFactory : IBridgeWindowFactory
{
    public IBridgeWindowAdapter CreateSelectorWindow(ProviderSelectorViewModel viewModel) =>
        new WpfBridgeWindowAdapter(new Window
        {
            Title = "Blaze Interaction · 选择感应设备",
            Width = 620,
            Height = 430,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new ProviderSelectorView { DataContext = viewModel }
        });

    public IBridgeWindowAdapter AdaptProviderWindow(object providerView) =>
        providerView switch
        {
            Window window => new WpfBridgeWindowAdapter(window),
            FrameworkElement element => new WpfBridgeWindowAdapter(new Window
            {
                Title = "Blaze Interaction",
                Width = 1080,
                Height = 760,
                Content = element
            }),
            _ => throw new InvalidOperationException(
                $"Provider settings view '{providerView.GetType().FullName}' is not a WPF view.")
        };

    public object CreateProviderHeader(ProviderHeaderViewModel viewModel) =>
        new ProviderHeaderView(viewModel);

    public object WrapProviderContent(object header, object? originalContent)
    {
        var panel = new DockPanel();
        if (header is UIElement headerElement)
        {
            DockPanel.SetDock(headerElement, Dock.Top);
            panel.Children.Add(headerElement);
        }

        if (originalContent is UIElement contentElement)
        {
            panel.Children.Add(contentElement);
        }

        return panel;
    }

    private sealed class WpfBridgeWindowAdapter(Window window) : IBridgeWindowAdapter
    {
        private readonly Window _window = window ?? throw new ArgumentNullException(nameof(window));
        public object? Content { get => _window.Content; set => _window.Content = value; }
        public bool IsVisible => _window.IsVisible;
        public bool IsMinimized
        {
            get => _window.WindowState == WindowState.Minimized;
            set
            {
                if (value)
                {
                    _window.WindowState = WindowState.Minimized;
                }
                else if (_window.WindowState == WindowState.Minimized)
                {
                    _window.WindowState = WindowState.Normal;
                }
            }
        }
        public event EventHandler? Closed
        {
            add => _window.Closed += value;
            remove => _window.Closed -= value;
        }
        public void Show() => _window.Show();
        public void Hide() => _window.Hide();
        public void Close() => _window.Close();
    }
}
