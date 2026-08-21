using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Blaze.Interaction.Bridge.Wpf;

public partial class App : Application
{
    private BridgeHost? _host;
    private readonly CancellationTokenSource _shutdown = new();

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        try
        {
            var launch = BridgeCommandLine.Parse(eventArgs.Args);
            _host = BridgeHost.Create(new BridgeHostOptions
            {
                ProvidersRoot = launch.ProvidersRoot ?? Path.Combine(AppContext.BaseDirectory, "Providers"),
                ParentProcessId = launch.ParentProcessId,
                PreferredProviderId = launch.ProviderId,
                PipeName = launch.PipeName
            });
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
            window.Closed += (_, _) => _shutdown.Cancel();
            MainWindow = window;
            window.Show();
            if (launch.Minimized)
            {
                window.WindowState = WindowState.Minimized;
            }

            await _host.RunAsync(_shutdown.Token);
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
}

internal static class BridgeAppDiagnostics
{
    internal static void ReportCleanupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Trace.TraceError("Blaze Interaction Bridge cleanup failed: {0}", exception);
    }
}
