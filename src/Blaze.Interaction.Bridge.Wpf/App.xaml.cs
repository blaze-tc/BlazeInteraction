using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Bridge.Wpf;

public partial class App : Application
{
    private BridgeHost? _host;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly BridgeProviderSettingsContext _settingsContext = new();
    private Window? _statusWindow;
    private bool _replacingStatusWindow;
    private bool _providerWindowShown;
    private bool _launchMinimized;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        try
        {
            var launch = BridgeCommandLine.Parse(eventArgs.Args);
            _host = BridgeHost.Create(new BridgeHostOptions
            {
                ProvidersRoot = launch.ProvidersRoot ?? Path.Combine(AppContext.BaseDirectory, "Providers"),
                DataRoot = launch.DataRoot,
                ProfilePath = launch.ProfilePath,
                ParentProcessId = launch.ParentProcessId,
                PreferredProviderId = launch.ProviderId,
                PipeName = launch.PipeName
            });
            _launchMinimized = launch.Minimized;
            var window = new Window
            {
                Title = "Blaze Interaction Bridge",
                Width = 460,
                Height = 180,
                Content = new TextBlock
                {
                    Text = "Blaze Interaction Bridge is running.\nSensor Providers connect through Interaction IPC.",
                    Margin = new Thickness(24),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            _statusWindow = window;
            window.Closed += (_, _) =>
            {
                if (!_replacingStatusWindow)
                {
                    _shutdown.Cancel();
                }
            };
            MainWindow = window;
            window.Show();
            if (launch.Minimized)
            {
                window.WindowState = WindowState.Minimized;
            }

            _host.ActiveProviderChanged += OnActiveProviderChanged;

            await _host.RunAsync(_shutdown.Token);
            _host.ActiveProviderChanged -= OnActiveProviderChanged;
            await _host.DisposeAsync();
            _host = null;
            Shutdown(0);
        }
        catch (Exception exception)
        {
            if (_host is not null)
            {
                try
                {
                    await _host.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    BridgeAppDiagnostics.ReportCleanupFailure(cleanupException);
                }

                _host = null;
            }

            MessageBox.Show(
                exception.Message,
                "Blaze Interaction Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        base.OnExit(eventArgs);
    }

    private void OnActiveProviderChanged(object? sender, EventArgs eventArgs)
    {
        _ = Dispatcher.BeginInvoke(PresentActiveProviderSettings);
    }

    private void PresentActiveProviderSettings()
    {
        if (_providerWindowShown || _host is null)
        {
            return;
        }

        object? settingsView;
        try
        {
            settingsView = _host.CreateActiveProviderSettingsView(_settingsContext);
        }
        catch (Exception exception)
        {
            if (_statusWindow?.Content is TextBlock text)
            {
                text.Text = $"Provider settings failed to load.\n{exception.Message}";
            }
            return;
        }

        if (settingsView is Window providerWindow)
        {
            _providerWindowShown = true;
            providerWindow.Closed += (_, _) => _shutdown.Cancel();
            MainWindow = providerWindow;
            providerWindow.Show();
            if (_launchMinimized)
            {
                providerWindow.WindowState = WindowState.Minimized;
            }

            if (_statusWindow is not null)
            {
                _replacingStatusWindow = true;
                _statusWindow.Close();
                _replacingStatusWindow = false;
                _statusWindow = null;
            }
            return;
        }

        if (settingsView is FrameworkElement element && _statusWindow is not null)
        {
            _statusWindow.Content = element;
            _statusWindow.Width = Math.Max(_statusWindow.Width, 960);
            _statusWindow.Height = Math.Max(_statusWindow.Height, 680);
        }
    }
}

internal sealed class BridgeProviderSettingsContext : IProviderSettingsContext
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ValueTask<string?> GetValueAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _values.TryGetValue(key, out var value);
            return ValueTask.FromResult(value);
        }
    }

    public ValueTask SetValueAsync(string key, string? value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _values[key] = value;
        }
        return ValueTask.CompletedTask;
    }
}

internal static class BridgeAppDiagnostics
{
    internal static void ReportCleanupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceError("Blaze Interaction Bridge cleanup failed: {0}", exception);
    }
}
