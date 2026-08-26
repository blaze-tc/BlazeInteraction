using System.Diagnostics;
using System.IO;
using System.Windows;
using Blaze.Interaction.Provider.Abstractions;

namespace Blaze.Interaction.Bridge.Wpf;

public partial class App : Application
{
    private BridgeHost? _host;
    private BridgeWindowCoordinator? _windowCoordinator;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly BridgeProviderSettingsContext _settingsContext = new();

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        try
        {
            var launch = BridgeCommandLine.Parse(eventArgs.Args);
            var settingsStore = new BridgeSettingsStore(launch.DataRoot);
            BridgeSettings settings;
            string? settingsError = null;
            try
            {
                settings = await settingsStore.LoadAsync(_shutdown.Token);
            }
            catch (BridgeSettingsException exception)
            {
                settings = BridgeSettings.CreateDefault();
                settingsError = exception.Message;
            }

            if (!string.IsNullOrWhiteSpace(launch.ProviderId))
            {
                settings = new BridgeSettings(
                    BridgeSettings.CurrentSchemaVersion,
                    launch.ProviderId);
            }

            _host = BridgeHost.Create(new BridgeHostOptions
            {
                ProvidersRoot = launch.ProvidersRoot ?? Path.Combine(AppContext.BaseDirectory, "Providers"),
                DataRoot = launch.DataRoot,
                ProfilePath = launch.ProfilePath,
                ParentProcessId = launch.ParentProcessId,
                PreferredProviderId = settings.SelectedProviderId,
                UseFallbackProviderWhenNoPreference = settings.SelectedProviderId is not null,
                PipeName = launch.PipeName
            });
            var diagnosticError = string.Join(
                Environment.NewLine,
                _host.StartupDiagnostics.Select(item =>
                    $"{item.ProviderId} [{item.Stage}]: {item.Message}"));
            var startupError = string.Join(
                Environment.NewLine,
                new[] { settingsError, diagnosticError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            _windowCoordinator = new BridgeWindowCoordinator(
                _host,
                _settingsContext,
                new WpfBridgeWindowFactory(),
                new WpfBridgeUiDispatcher(Dispatcher),
                launch.Minimized,
                () => _shutdown.Cancel(),
                startupError);
            _windowCoordinator.Start(settings);

            await _host.RunAsync(_shutdown.Token);
            _windowCoordinator.Dispose();
            _windowCoordinator = null;
            await _host.DisposeAsync();
            _host = null;
            Shutdown(0);
        }
        catch (Exception exception)
        {
            _windowCoordinator?.Dispose();
            _windowCoordinator = null;
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
